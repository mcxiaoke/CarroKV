package ckv

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.lang.ref.WeakReference
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write

class CkvDb private constructor(
    private val path: String,
    private val options: DbOptions
) : AutoCloseable {

    private val rwLock = ReentrantReadWriteLock()
    private val namespaces = mutableMapOf<String, NamespaceStore>()
    private var currentMaxTxId = 0L
    private var lastKnownDiskMaxTxId = 0L
    private var lastKnownDiskFileSize = 0L
    private var isDirty = false
    private var isDisposed = false

    private val watchers = mutableListOf<WatcherSlot>()
    private var nextWatcherId = 0L

    private var syncExecutor: ScheduledExecutorService? = null

    private class WatcherSlot(
        val id: Long,
        val callback: KeyChangeCallback,
        val db: CkvDb
    ) : WatcherHandle {
        override fun unwatch() {
            db.removeWatcher(id)
        }
    }

    private fun removeWatcher(id: Long) {
        rwLock.write {
            watchers.removeAll { it.id == id }
        }
    }

    init {
        initAndRecover()
        if (options.syncMode == SyncMode.DEFERRED && !options.readOnly) {
            val exec = Executors.newSingleThreadScheduledExecutor { r ->
                val t = Thread(r, "ckv-sync-worker")
                t.isDaemon = true
                t
            }
            exec.scheduleWithFixedDelay(
                { flushIfDirty() },
                options.syncIntervalMs,
                options.syncIntervalMs,
                TimeUnit.MILLISECONDS
            )
            syncExecutor = exec
        }
    }

    private fun checkDisposed() {
        if (isDisposed) throw CkvException("Database is already disposed")
    }

    private fun checkWritable() {
        checkDisposed()
        if (options.readOnly) {
            throw LockedError("Database is opened in read-only mode")
        }
    }

    private fun initAndRecover() {
        val dbFile = File(path)
        val tmpFile = File("$path.tmp")
        val bakFile = File("$path.bak")
        var dbExists = dbFile.exists()

        if (tmpFile.exists()) {
            if (!dbExists) {
                try {
                    val tmpBytes = tmpFile.readBytes()
                    val parsed = FrameLoader.loadAllWithRecovery(tmpBytes)
                    tmpFile.renameTo(dbFile)
                    applyParsed(parsed.namespaces, parsed.maxTxId, dbFile.length())
                    dbExists = true
                } catch (_: Exception) {
                    try { tmpFile.delete() } catch (_: Exception) {}
                }
            } else {
                try { tmpFile.delete() } catch (_: Exception) {}
            }
        }

        if (dbExists) {
            val bytes = dbFile.readBytes()
            val allowTailTrunc = options.engineMode == StorageEngineMode.APPEND_ONLY_LOG && !options.readOnly
            try {
                val parsed = FrameLoader.loadAllWithRecovery(
                    bytes,
                    allowTailTruncation = allowTailTrunc,
                    onWarning = options.onWarning
                )
                if (allowTailTrunc && parsed.effectiveLength < bytes.size) {
                    RandomAccessFile(dbFile, "rw").use { raf ->
                        raf.setLength(parsed.effectiveLength)
                    }
                }
                applyParsed(parsed.namespaces, parsed.maxTxId, parsed.effectiveLength)
            } catch (err: CorruptedError) {
                // 决策 D1：`.bak` 是上一次轮转**之前**的一代，用它替换主库会静默
                // 回滚此后写入的全部数据。默认不再自动回退，仅在调用方显式开启时
                // 才使用；否则错误信息中给出 `.bak` 位置，由运维离线恢复。
                val bakAvailable = bakFile.exists()

                if (options.autoRecoverFromBackup && !options.readOnly && bakAvailable) {
                    try {
                        // `.bak` 是恢复源：原样加载，绝不截断（G1）。默认参数即为 false。
                        val bakBytes = bakFile.readBytes()
                        val bakParsed = FrameLoader.loadAllWithRecovery(bakBytes)
                        Files.copy(bakFile.toPath(), dbFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
                        applyParsed(bakParsed.namespaces, bakParsed.maxTxId, bakFile.length())
                        options.onWarning?.invoke(
                            "onRecoveredFromBackup",
                            "Recovered database '$path' from backup '${bakFile.path}'"
                        )
                        return
                    } catch (_: Exception) {
                        throw CorruptedError(
                            "${err.message}; the previous generation kept at " +
                                "'${bakFile.path}' could not be loaded either"
                        )
                    }
                }

                if (bakAvailable) {
                    throw CorruptedError(
                        "${err.message}; a previous generation is kept at '${bakFile.path}': " +
                            "copy it over the primary file to recover, or open with " +
                            "autoRecoverFromBackup = true"
                    )
                }

                throw err
            }
        } else if (bakFile.exists()) {
            try {
                val bakBytes = bakFile.readBytes()
                val bakParsed = FrameLoader.loadAllWithRecovery(bakBytes)
                Files.copy(bakFile.toPath(), dbFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
                applyParsed(bakParsed.namespaces, bakParsed.maxTxId, bakFile.length())
                options.onWarning?.invoke(
                    "onRecoveredFromBackup",
                    "Recovered database '$path' from backup '${bakFile.path}'"
                )
            } catch (_: Exception) {}
        }
    }

    private fun applyParsed(loadedNamespaces: Map<String, NamespaceStore>, maxTxId: Long, fileSize: Long) {
        namespaces.clear()
        namespaces.putAll(loadedNamespaces)
        currentMaxTxId = maxTxId
        lastKnownDiskMaxTxId = maxTxId
        lastKnownDiskFileSize = fileSize
    }

    private fun flushIfDirty() {
        rwLock.write {
            if (isDisposed || options.readOnly || !isDirty) return@write
            try {
                flushInternal()
            } catch (_: Exception) {}
        }
    }

    private fun beginFlush(): Pair<LockFile, Long> {
        checkWritable()
        val lock = LockFile.acquire(path, options)
        val dbFile = File(path)
        val diskMaxTxId = if (dbFile.exists()) {
            val curLen = dbFile.length()
            if (curLen == lastKnownDiskFileSize) {
                lastKnownDiskMaxTxId
            } else {
                FrameLoader.scanMaxTxId(dbFile)
            }
        } else {
            0L
        }

        if (diskMaxTxId > currentMaxTxId) {
            lock.close()
            throw TransactionConflictError(
                "Disk TxID $diskMaxTxId is newer than memory TxID $currentMaxTxId"
            )
        }
        return Pair(lock, diskMaxTxId)
    }

    private fun commitFlush(lock: LockFile, diskMaxTxId: Long) {
        try {
            val dbFile = File(path)
            val tmpFile = File("$path.tmp")
            val bakFile = File("$path.bak")

            val (bytes, newTxId) = buildSnapshotBytes(diskMaxTxId)
            tmpFile.writeBytes(bytes)

            // Safe sync tmp file to disk
            FileOutputStream(tmpFile, true).use { fos ->
                fos.fd.sync()
            }

            if (dbFile.exists()) {
                try {
                    Files.copy(dbFile.toPath(), bakFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
                } catch (_: Exception) {}
            }

            try {
                Files.move(tmpFile.toPath(), dbFile.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
            } catch (_: Exception) {
                // Fallback for Windows without atomic move
                if (dbFile.exists()) dbFile.delete()
                tmpFile.renameTo(dbFile)
            }

            currentMaxTxId = newTxId
            lastKnownDiskMaxTxId = newTxId
            lastKnownDiskFileSize = dbFile.length()
            isDirty = false
        } finally {
            lock.close()
        }
    }

    private fun buildSnapshotBytes(diskMaxTxId: Long): Pair<ByteArray, Long> {
        val baos = ByteArrayOutputStream()
        val header = FileHeader.createBytes()
        baos.write(header)

        var totalEntries = 0
        for (store in namespaces.values) {
            totalEntries += store.items.size
        }

        if (totalEntries == 0) {
            return Pair(baos.toByteArray(), currentMaxTxId)
        }

        var nextTxId = maxOf(currentMaxTxId, diskMaxTxId) + 1L
        val sortedNs = namespaces.keys.toMutableList()
        sortedNs.sortWith { a, b ->
            BinaryHelper.compareBytes(a.toByteArray(Charsets.UTF_8), b.toByteArray(Charsets.UTF_8))
        }

        val frameEntries = mutableListOf<RawOpEntry>()
        var currentPayloadLen = 0

        fun flushFrame() {
            if (frameEntries.isEmpty()) return
            BatchFrameWriter.writeFrame(baos, nextTxId++, frameEntries)
            frameEntries.clear()
            currentPayloadLen = 0
        }

        for (ns in sortedNs) {
            val nsBytes = ns.toByteArray(Charsets.UTF_8)
            val store = namespaces[ns]!!
            for (item in store.items) {
                val entry = RawOpEntry(
                    Constants.OP_PUT,
                    item.valueType,
                    nsBytes,
                    item.key,
                    item.value
                )
                val entryLen = entry.encodedLen()
                if (frameEntries.size >= Constants.MAX_OPS_PER_FRAME ||
                    (currentPayloadLen + entryLen > Constants.MAX_PAYLOAD_LEN)
                ) {
                    flushFrame()
                }
                currentPayloadLen += entryLen
                frameEntries.add(entry)
            }
        }
        flushFrame()

        return Pair(baos.toByteArray(), nextTxId - 1L)
    }

    private fun appendEntries(entries: List<RawOpEntry>) {
        if (entries.isEmpty()) return
        val lock = LockFile.acquire(path, options)
        try {
            val dbFile = File(path)
            val diskMaxTxId = if (dbFile.exists()) {
                val curLen = dbFile.length()
                if (curLen == lastKnownDiskFileSize) {
                    lastKnownDiskMaxTxId
                } else {
                    FrameLoader.scanMaxTxId(dbFile)
                }
            } else {
                0L
            }

            if (diskMaxTxId > currentMaxTxId) {
                throw TransactionConflictError(
                    "Disk TxID $diskMaxTxId is newer than memory TxID $currentMaxTxId"
                )
            }

            if (!dbFile.exists() || dbFile.length() == 0L) {
                dbFile.parentFile?.mkdirs()
                FileOutputStream(dbFile).use { fos ->
                    fos.write(FileHeader.createBytes())
                    fos.fd.sync()
                }
            }

            val nextTxId = maxOf(currentMaxTxId, diskMaxTxId) + 1L
            val frameEntries = mutableListOf<RawOpEntry>()
            var currentPayloadLen = 0
            var currentTx = nextTxId

            FileOutputStream(dbFile, true).use { fos ->
                fun flushFrame() {
                    if (frameEntries.isEmpty()) return
                    BatchFrameWriter.writeFrame(fos, currentTx++, frameEntries)
                    frameEntries.clear()
                    currentPayloadLen = 0
                }

                for (entry in entries) {
                    val entryLen = entry.encodedLen()
                    if (frameEntries.size >= Constants.MAX_OPS_PER_FRAME ||
                        (currentPayloadLen + entryLen > Constants.MAX_PAYLOAD_LEN)
                    ) {
                        flushFrame()
                    }
                    currentPayloadLen += entryLen
                    frameEntries.add(entry)
                }
                flushFrame()

                if (options.syncMode == SyncMode.IMMEDIATE) {
                    fos.fd.sync()
                    isDirty = false
                } else {
                    isDirty = true
                }
            }

            val finalTxId = currentTx - 1L
            currentMaxTxId = finalTxId
            lastKnownDiskMaxTxId = finalTxId
            lastKnownDiskFileSize = dbFile.length()
        } finally {
            lock.close()
        }
    }

    private fun applyMutation(entries: List<RawOpEntry>, applyMem: () -> Unit) {
        checkWritable()
        rwLock.write {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                appendEntries(entries)
                applyMem()
            } else {
                applyMem()
                if (options.syncMode == SyncMode.IMMEDIATE) {
                    val (lock, diskMaxTxId) = beginFlush()
                    commitFlush(lock, diskMaxTxId)
                } else {
                    isDirty = true
                }
            }
        }
    }

    private fun notifyWatchers(events: List<KeyChangeEvent>) {
        if (events.isEmpty()) return
        val list = rwLock.read { watchers.toList() }
        for (slot in list) {
            for (event in events) {
                try {
                    slot.callback(event)
                } catch (_: Exception) {}
            }
        }
    }

    fun putBytes(ns: String?, key: String, value: ByteArray) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        validateKeyAndVal(nsBytes, keyBytes, value)

        val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_BYTES, nsBytes, keyBytes, value)
        applyMutation(listOf(entry)) {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            store.put(keyBytes, value, Constants.VAL_TYPE_BYTES)
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, value, Constants.VAL_TYPE_BYTES)))
    }

    fun putString(ns: String?, key: String, value: String) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = value.toByteArray(Charsets.UTF_8)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)

        val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_STRING, nsBytes, keyBytes, valBytes)
        applyMutation(listOf(entry)) {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            store.put(keyBytes, valBytes, Constants.VAL_TYPE_STRING)
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, valBytes, Constants.VAL_TYPE_STRING)))
    }

    fun putInt64(ns: String?, key: String, value: Long) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeInt64(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)

        val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_INT64, nsBytes, keyBytes, valBytes)
        applyMutation(listOf(entry)) {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            store.put(keyBytes, valBytes, Constants.VAL_TYPE_INT64)
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, valBytes, Constants.VAL_TYPE_INT64)))
    }

    fun putFloat64(ns: String?, key: String, value: Double) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeFloat64(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)

        val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_FLOAT64, nsBytes, keyBytes, valBytes)
        applyMutation(listOf(entry)) {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            store.put(keyBytes, valBytes, Constants.VAL_TYPE_FLOAT64)
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, valBytes, Constants.VAL_TYPE_FLOAT64)))
    }

    fun putBool(ns: String?, key: String, value: Boolean) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeBool(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)

        val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_BOOL, nsBytes, keyBytes, valBytes)
        applyMutation(listOf(entry)) {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            store.put(keyBytes, valBytes, Constants.VAL_TYPE_BOOL)
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, valBytes, Constants.VAL_TYPE_BOOL)))
    }

    fun incrementInt64(ns: String?, key: String, delta: Long = 1L): Long {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        validateKey(nsBytes, keyBytes)

        var newVal = 0L
        var valBytes: ByteArray? = null

        rwLock.write {
            val store = namespaces.getOrPut(normNs) { NamespaceStore() }
            val idx = store.findIndex(keyBytes)
            val current = if (idx >= 0) {
                val item = store.items[idx]
                if (item.valueType != Constants.VAL_TYPE_INT64) {
                    throw TypeMismatchError("Key '$key' has value_type ${item.valueType}, expected int64 (0x02)")
                }
                BinaryHelper.decodeInt64(item.value)
            } else {
                0L
            }
            newVal = current + delta
            val bBytes = BinaryHelper.encodeInt64(newVal)
            valBytes = bBytes
            val entry = RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_INT64, nsBytes, keyBytes, bBytes)
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                appendEntries(listOf(entry))
                store.put(keyBytes, bBytes, Constants.VAL_TYPE_INT64)
            } else {
                store.put(keyBytes, bBytes, Constants.VAL_TYPE_INT64)
                if (options.syncMode == SyncMode.IMMEDIATE) {
                    val (lock, diskMaxTxId) = beginFlush()
                    commitFlush(lock, diskMaxTxId)
                } else {
                    isDirty = true
                }
            }
        }
        notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.PUT, valBytes, Constants.VAL_TYPE_INT64)))
        return newVal
    }

    fun delete(ns: String?, key: String): Boolean {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        validateKey(nsBytes, keyBytes)

        var deleted = false
        val entry = RawOpEntry(Constants.OP_DELETE, Constants.VAL_TYPE_BYTES, nsBytes, keyBytes, ByteArray(0))
        applyMutation(listOf(entry)) {
            val store = namespaces[normNs]
            if (store != null) {
                val old = store.delete(keyBytes)
                deleted = old != null
            }
        }
        if (deleted) {
            notifyWatchers(listOf(KeyChangeEvent(normNs, key, KeyChangeOp.DELETE, null, null)))
        }
        return deleted
    }

    fun dropNamespace(ns: String?) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        if (nsBytes.size > Constants.MAX_NS_LEN) {
            throw KeyTooLongError("Namespace length ${nsBytes.size} exceeds max ${Constants.MAX_NS_LEN}")
        }

        val events = mutableListOf<KeyChangeEvent>()
        rwLock.write {
            val store = namespaces[normNs]
            if (store != null) {
                for (item in store.items) {
                    events.add(KeyChangeEvent(normNs, String(item.key, Charsets.UTF_8), KeyChangeOp.DELETE, null, null))
                }
            }
            val entry = RawOpEntry(Constants.OP_DROP_NAMESPACE, Constants.VAL_TYPE_BYTES, nsBytes, ByteArray(0), ByteArray(0))
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                appendEntries(listOf(entry))
                namespaces.remove(normNs)
            } else {
                namespaces.remove(normNs)
                if (options.syncMode == SyncMode.IMMEDIATE) {
                    val (lock, diskMaxTxId) = beginFlush()
                    commitFlush(lock, diskMaxTxId)
                } else {
                    isDirty = true
                }
            }
        }
        notifyWatchers(events)
    }

    fun clear(ns: String?) {
        dropNamespace(ns)
    }

    fun batch(action: (BatchWriter) -> Unit) {
        checkWritable()
        val writer = BatchWriter()
        action(writer)
        if (writer.operations.isEmpty()) return

        val events = mutableListOf<KeyChangeEvent>()
        rwLock.write {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                appendEntries(writer.operations)
                for (op in writer.operations) {
                    val nsStr = if (op.namespace.isEmpty()) "" else String(op.namespace, Charsets.UTF_8)
                    when (op.opCode) {
                        Constants.OP_PUT -> {
                            val store = namespaces.getOrPut(nsStr) { NamespaceStore() }
                            store.put(op.key, op.value, op.valueType)
                            events.add(KeyChangeEvent(nsStr, String(op.key, Charsets.UTF_8), KeyChangeOp.PUT, op.value, op.valueType))
                        }
                        Constants.OP_DELETE -> {
                            val store = namespaces[nsStr]
                            if (store?.delete(op.key) != null) {
                                events.add(KeyChangeEvent(nsStr, String(op.key, Charsets.UTF_8), KeyChangeOp.DELETE, null, null))
                            }
                        }
                        Constants.OP_DROP_NAMESPACE -> {
                            val store = namespaces[nsStr]
                            if (store != null) {
                                for (item in store.items) {
                                    events.add(KeyChangeEvent(nsStr, String(item.key, Charsets.UTF_8), KeyChangeOp.DELETE, null, null))
                                }
                                namespaces.remove(nsStr)
                            }
                        }
                    }
                }
            } else {
                for (op in writer.operations) {
                    val nsStr = if (op.namespace.isEmpty()) "" else String(op.namespace, Charsets.UTF_8)
                    when (op.opCode) {
                        Constants.OP_PUT -> {
                            val store = namespaces.getOrPut(nsStr) { NamespaceStore() }
                            store.put(op.key, op.value, op.valueType)
                            events.add(KeyChangeEvent(nsStr, String(op.key, Charsets.UTF_8), KeyChangeOp.PUT, op.value, op.valueType))
                        }
                        Constants.OP_DELETE -> {
                            val store = namespaces[nsStr]
                            if (store?.delete(op.key) != null) {
                                events.add(KeyChangeEvent(nsStr, String(op.key, Charsets.UTF_8), KeyChangeOp.DELETE, null, null))
                            }
                        }
                        Constants.OP_DROP_NAMESPACE -> {
                            val store = namespaces[nsStr]
                            if (store != null) {
                                for (item in store.items) {
                                    events.add(KeyChangeEvent(nsStr, String(item.key, Charsets.UTF_8), KeyChangeOp.DELETE, null, null))
                                }
                                namespaces.remove(nsStr)
                            }
                        }
                    }
                }
                if (options.syncMode == SyncMode.IMMEDIATE) {
                    val (lock, diskMaxTxId) = beginFlush()
                    commitFlush(lock, diskMaxTxId)
                } else {
                    isDirty = true
                }
            }
        }
        notifyWatchers(events)
    }

    fun getBytes(ns: String?, key: String): ByteArray? {
        checkDisposed()
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs] ?: return null
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            val idx = store.findIndex(keyBytes)
            if (idx < 0) return null
            return store.items[idx].value.copyOf()
        }
    }

    fun getString(ns: String?, key: String): String? {
        val bytes = getBytes(ns, key) ?: return null
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs]!!
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            val idx = store.findIndex(keyBytes)
            val item = store.items[idx]
            if (item.valueType != Constants.VAL_TYPE_STRING) {
                throw TypeMismatchError("Key '$key' has value_type ${item.valueType}, expected string (0x01)")
            }
            if (!BinaryHelper.isValidUtf8(bytes)) {
                throw InvalidUtf8Error("Invalid UTF-8 sequence in key '$key'")
            }
            return String(bytes, Charsets.UTF_8)
        }
    }

    fun getInt64(ns: String?, key: String): Long? {
        val bytes = getBytes(ns, key) ?: return null
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs]!!
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            val idx = store.findIndex(keyBytes)
            val item = store.items[idx]
            if (item.valueType != Constants.VAL_TYPE_INT64) {
                throw TypeMismatchError("Key '$key' has value_type ${item.valueType}, expected int64 (0x02)")
            }
            return BinaryHelper.decodeInt64(bytes)
        }
    }

    fun getFloat64(ns: String?, key: String): Double? {
        val bytes = getBytes(ns, key) ?: return null
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs]!!
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            val idx = store.findIndex(keyBytes)
            val item = store.items[idx]
            if (item.valueType != Constants.VAL_TYPE_FLOAT64) {
                throw TypeMismatchError("Key '$key' has value_type ${item.valueType}, expected float64 (0x03)")
            }
            return BinaryHelper.decodeFloat64(bytes)
        }
    }

    fun getBool(ns: String?, key: String): Boolean? {
        val bytes = getBytes(ns, key) ?: return null
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs]!!
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            val idx = store.findIndex(keyBytes)
            val item = store.items[idx]
            if (item.valueType != Constants.VAL_TYPE_BOOL) {
                throw TypeMismatchError("Key '$key' has value_type ${item.valueType}, expected bool (0x04)")
            }
            return BinaryHelper.decodeBool(bytes)
        }
    }

    fun containsKey(ns: String?, key: String): Boolean {
        checkDisposed()
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs] ?: return false
            val keyBytes = key.toByteArray(Charsets.UTF_8)
            return store.findIndex(keyBytes) >= 0
        }
    }

    fun count(ns: String?): Int {
        checkDisposed()
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            return namespaces[normNs]?.items?.size ?: 0
        }
    }

    fun namespaces(): List<String> {
        checkDisposed()
        rwLock.read {
            val list = namespaces.keys.toMutableList()
            list.sortWith { a, b ->
                BinaryHelper.compareBytes(a.toByteArray(Charsets.UTF_8), b.toByteArray(Charsets.UTF_8))
            }
            return list
        }
    }

    fun keys(ns: String?, prefix: String? = null): List<String> {
        checkDisposed()
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs] ?: return emptyList()
            val prefixBytes = prefix?.toByteArray(Charsets.UTF_8)

            val result = mutableListOf<String>()
            for (item in store.items) {
                if (prefixBytes == null || BinaryHelper.startsWith(item.key, prefixBytes)) {
                    result.add(String(item.key, Charsets.UTF_8))
                }
            }
            return result
        }
    }

    fun scanRange(ns: String?, startKey: String?, endKey: String?): List<Entry> {
        checkDisposed()
        rwLock.read {
            val normNs = BinaryHelper.normalizeNamespace(ns)
            val store = namespaces[normNs] ?: return emptyList()
            val startBytes = startKey?.toByteArray(Charsets.UTF_8)
            val endBytes = endKey?.toByteArray(Charsets.UTF_8)

            val result = mutableListOf<Entry>()
            for (item in store.items) {
                if (startBytes != null && BinaryHelper.compareBytes(item.key, startBytes) < 0) {
                    continue
                }
                if (endBytes != null && BinaryHelper.compareBytes(item.key, endBytes) >= 0) {
                    break
                }
                result.add(Entry(String(item.key, Charsets.UTF_8), item.value.copyOf(), item.valueType))
            }
            return result
        }
    }

    fun snapshot(): Snapshot {
        checkDisposed()
        rwLock.read {
            val cloned = mutableMapOf<String, NamespaceStore>()
            for ((k, v) in namespaces) {
                cloned[k] = v.clone()
            }
            return Snapshot(cloned)
        }
    }

    fun watch(callback: KeyChangeCallback): WatcherHandle {
        checkDisposed()
        rwLock.write {
            val id = ++nextWatcherId
            val slot = WatcherSlot(id, callback, this)
            watchers.add(slot)
            return slot
        }
    }

    fun stats(): DbStats {
        checkDisposed()
        rwLock.read {
            var total = 0L
            for (store in namespaces.values) {
                total += store.items.size
            }
            return DbStats(total, currentMaxTxId)
        }
    }

    fun fileSize(): Long {
        val f = File(path)
        return if (f.exists()) f.length() else 0L
    }

    fun flush() {
        checkWritable()
        rwLock.write {
            flushInternal()
        }
    }

    private fun flushInternal() {
        if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
            val dbFile = File(path)
            if (dbFile.exists()) {
                FileOutputStream(dbFile, true).use { fos ->
                    fos.fd.sync()
                }
            }
            isDirty = false
        } else {
            val (lock, diskMaxTxId) = beginFlush()
            commitFlush(lock, diskMaxTxId)
        }
    }

    fun compact() {
        checkWritable()
        rwLock.write {
            val (lock, diskMaxTxId) = beginFlush()
            commitFlush(lock, diskMaxTxId)
        }
    }

    override fun close() {
        rwLock.write {
            if (isDisposed) return@write
            syncExecutor?.shutdown()
            try {
                syncExecutor?.awaitTermination(1, TimeUnit.SECONDS)
            } catch (_: Exception) {}
            syncExecutor = null

            if (isDirty && !options.readOnly) {
                try {
                    flushInternal()
                } catch (_: Exception) {}
            }

            isDisposed = true
            unregisterInstance(path)
        }
    }

    private fun validateKey(nsBytes: ByteArray, keyBytes: ByteArray) {
        if (nsBytes.size > Constants.MAX_NS_LEN) {
            throw KeyTooLongError("Namespace length ${nsBytes.size} exceeds max ${Constants.MAX_NS_LEN}")
        }
        if (keyBytes.isEmpty()) {
            throw InvalidKeyError("Key must not be empty")
        }
        if (keyBytes.size > Constants.MAX_KEY_LEN) {
            throw KeyTooLongError("Key length ${keyBytes.size} exceeds max ${Constants.MAX_KEY_LEN}")
        }
    }

    private fun validateKeyAndVal(nsBytes: ByteArray, keyBytes: ByteArray, valBytes: ByteArray) {
        validateKey(nsBytes, keyBytes)
        if (valBytes.size > Constants.MAX_VAL_LEN) {
            throw ValueTooLongError("Value length ${valBytes.size} exceeds max ${Constants.MAX_VAL_LEN}")
        }
    }

    companion object {
        private val registryLock = Any()
        private val registry = mutableMapOf<String, WeakReference<CkvDb>>()

        fun open(path: String, options: DbOptions = DbOptions()): CkvDb {
            val canonicalPath = File(path).canonicalPath
            synchronized(registryLock) {
                val existing = registry[canonicalPath]?.get()
                if (existing != null && !existing.isDisposed) {
                    return existing
                }
                val db = CkvDb(canonicalPath, options)
                registry[canonicalPath] = WeakReference(db)
                return db
            }
        }

        private fun unregisterInstance(path: String) {
            val canonicalPath = File(path).canonicalPath
            synchronized(registryLock) {
                registry.remove(canonicalPath)
            }
        }
    }
}
