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

    /** 自动压实失败后待重试（D2）：触发它的那次写已经落盘。 */
    /** 跨进程连接锁，自 C9 起由 open 获取、持有整个连接生命周期（内核锁）。 */
    private var connectionLock: LockFile? = null

    private var needsCompaction = false

    /** 连续压实失败次数，用于加宽退避窗口。 */
    private var compactionFailures = 0

    /** 距上次压实尝试以来的写次数。 */
    private var writesSinceCompactionAttempt = 0L

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
        // C9: 跨进程内核锁在任何磁盘动作之前获取，并持有整个连接生命周期。因此
        // initAndRecover 的所有修复动作（.tmp 晋升/删除、残尾自愈、.bak 回退）都在锁内执行，
        // 不会再截掉另一个进程刚追加的帧（C10）。
        connectionLock = LockFile.acquire(path, options)
        try {
            initAndRecover()
        } catch (err: Throwable) {
            connectionLock?.close()
            connectionLock = null
            throw err
        }
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

    /**
     * Opens the database.
     *
     * 构造期间已获取连接锁并持有整个连接生命周期（C9），因此这里执行的每一种修复
     * （晋升/删除 `.tmp`、残尾截断、`.bak` 回退）都在没有其他进程能追加的前提下写盘。
     * 这正是原先的 C10：残尾自愈没有持锁，会截掉并发追加者刚写入的帧。
     */
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
            // 分支 ③：主库不存在但存在上一代。
            //
            // 该状态不可能由轮转中断产生（中断必然留下完整的 `.tmp`，由上面的步骤 1 晋升），
            // 因此它意味着主库被外部删除或替换。把 `.bak` 晋升为主库 = 静默回退到旧一代；
            // 而当 `.bak` 本身不可读时，旧实现会静默按**空库**打开——这是 C1-C3 之后的
            // 第 4 处静默数据丢失（D1-7/D1-8）。两种情形都拒绝打开，除非调用方显式开启
            // `.bak` 恢复。
            val bakBytes = bakFile.readBytes()
            val bakParsed = try {
                // `.bak` 是恢复源：原样加载，绝不截断（G1）。
                FrameLoader.loadAllWithRecovery(bakBytes)
            } catch (bakError: CorruptedError) {
                throw CorruptedError(
                    "Primary file '$path' is missing and the previous generation kept at " +
                        "'${bakFile.path}' could not be loaded either: ${bakError.message}"
                )
            }

            if (!options.autoRecoverFromBackup || options.readOnly) {
                throw CorruptedError(
                    "Primary file '$path' is missing while a previous generation is kept at " +
                        "'${bakFile.path}': copy it over the primary file to recover, or open with " +
                        "autoRecoverFromBackup = true"
                )
            }

            Files.copy(bakFile.toPath(), dbFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
            applyParsed(bakParsed.namespaces, bakParsed.maxTxId, bakBytes.size.toLong())
            options.onWarning?.invoke(
                "onRecoveredFromBackup",
                "Recovered database '$path' from backup '${bakFile.path}'"
            )
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

    /**
     * 写路径第一阶段：所有会拒绝本次写的校验都在这里完成，且都在任何内存修改之前。
     * 返回磁盘上当前的 maxTxID。
     *
     * 自 C9 起这里不再取锁：连接锁由构造/Open 获取并持有整个生命周期，写路径不会再与
     * 自身冲突（那正是 C2）。
     */
    private fun beginFlush(): Long {
        checkWritable()
        return precheckDiskState(File(path))
    }

    /**
     * 写前磁盘状态预检（C7）。
     *
     * 旧实现只比较 `diskMaxTxId > currentMaxTxId`：外部把磁盘替换成 **TxID 更小**的一代
     * （回滚、外部恢复、换文件）时完全检测不到，进程会继续往被替换的文件上写。其余四语言
     * 一律比对 `(maxTxId, fileSize)` 与上次已知状态（Rust `db.rs::begin_flush`），此处对齐。
     *
     * 空库（恰好 32B 文件头、无帧）在磁盘上表达不出 TxID，故该情形只比文件大小。
     */
    private fun precheckDiskState(dbFile: File): Long {
        var diskMaxTxId = 0L
        var diskFileSize = 0L

        if (dbFile.exists()) {
            diskFileSize = dbFile.length()
            diskMaxTxId = when {
                diskFileSize == lastKnownDiskFileSize -> lastKnownDiskMaxTxId
                diskFileSize >= Constants.FILE_HEADER_LEN.toLong() -> FrameLoader.scanMaxTxId(dbFile)
                else -> 0L
            }
        }

        val known = lastKnownDiskMaxTxId != 0L || lastKnownDiskFileSize != 0L
        if (known) {
            val stateMatches = if (diskFileSize == Constants.FILE_HEADER_LEN.toLong()) {
                diskFileSize == lastKnownDiskFileSize
            } else {
                diskMaxTxId == lastKnownDiskMaxTxId && diskFileSize == lastKnownDiskFileSize
            }
            if (!stateMatches) {
                throw TransactionConflictError(
                    "Disk state changed externally: expected " +
                        "($lastKnownDiskMaxTxId, $lastKnownDiskFileSize), " +
                        "found ($diskMaxTxId, $diskFileSize)"
                )
            }
        }

        return diskMaxTxId
    }

    /**
     * Snapshot 模式下的原子写（C6 + G3）：**先**做磁盘预检（可能抛
     * `TransactionConflictError` / `LockedError`），**再**改内存，最后落盘。
     *
     * 旧实现是 `applyMem()` → `beginFlush()` → `commitFlush()`：预检一旦失败，内存已改、
     * 磁盘未落，进程内状态与磁盘永久分叉（其余四语言一律是 begin → apply → commit）。
     * 另外 `beginFlush()` 返回的锁只有 `commitFlush` 的 `finally` 会释放，因此这里必须
     * 保证 `applyMem()` 抛异常时锁被释放（G3）。
     */
    private fun applySnapshotMutation(applyMem: () -> Unit) {
        if (options.syncMode != SyncMode.IMMEDIATE) {
            applyMem()
            isDirty = true
            return
        }

        val diskMaxTxId = beginFlush()
        applyMem()
        commitFlush(diskMaxTxId)
    }

    private fun commitFlush(diskMaxTxId: Long) {
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
                // 生成上一代 `.bak`：绝不用 `Files.copy(REPLACE_EXISTING)` 直接写到 `.bak`
                // —— 它会先截断目标再写入，中途崩溃即留下**半份** `.bak`，上一代完整备份
                // 同时被覆盖丢失（V10）。改为"写 `.stage` → fsync → 原子替换"。
                // 旧实现还用 `catch (_: Exception) {}` 吞掉失败，轮转出问题也无任何告警。
                FileRotation.stageAndReplace(dbFile, bakFile)
            }

            // C5：覆盖式移动，绝不先 `delete()` 目标（旧兜底路径 `dbFile.delete()` +
            // `renameTo` 会制造"两代同时不存在"的窗口）。
            FileRotation.replaceWithRetry(tmpFile, dbFile)

            currentMaxTxId = newTxId
            lastKnownDiskMaxTxId = newTxId
            lastKnownDiskFileSize = dbFile.length()
            isDirty = false
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

    /**
     * 把本次变更的帧追加进日志，返回追加后的文件大小。
     *
     * 这里**故意不触发压实**：压实写出的快照取自当前内存状态，而本次变更此刻还没有
     * 进内存，一旦在此处压实，就会用"不含本次写"的全量快照覆盖日志（C1）。压实改由
     * 调用方在 `applyMem()` 之后、且在独立锁周期内执行（[maybeCompactAfterWrite]）。
     */
    private fun appendEntries(entries: List<RawOpEntry>): Long {
        if (entries.isEmpty()) return 0L
        try {
            val dbFile = File(path)
            // C7：与 beginFlush 完全一致的预检口径（比对 (TxID, 文件大小)）
            val diskMaxTxId = precheckDiskState(dbFile)

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
            val newFileSize = dbFile.length()
            lastKnownDiskFileSize = newFileSize
            return newFileSize
        } finally {
        }
    }

    /**
     * 自动压实失败后跳过多少次写再重试：首次 64 次，每连续失败一次翻倍，上限 4096。
     */
    private fun compactionBackoff(priorFailures: Int): Long {
        val base = 64L
        val cap = 4096L
        val shift = if (priorFailures > 6) 6 else priorFailures
        val window = base shl shift
        return if (window > cap) cap else window
    }

    /**
     * 本次写已进内存后的自动压实策略（D2）：越过阈值时压实一次；此前失败过则按退避窗口
     * 重试。
     */
    private fun maybeCompactAfterWrite(newFileSize: Long) {
        if (options.compactionThresholdBytes <= 0L) return

        if (needsCompaction) {
            writesSinceCompactionAttempt++
            val priorFailures = if (compactionFailures > 0) compactionFailures - 1 else 0
            if (writesSinceCompactionAttempt < compactionBackoff(priorFailures)) return
            attemptCompaction()
            return
        }

        if (newFileSize >= options.compactionThresholdBytes) {
            attemptCompaction()
        }
    }

    /**
     * 执行一次自动压实并记录结果。
     *
     * 除非开启 `strictCompaction`，失败**不会**让调用方的写失败：该写已经 append + fsync
     * 落盘，报错等于让调用方重试一个已经成功的写（D2）。
     */
    private fun attemptCompaction() {
        writesSinceCompactionAttempt = 0L
        try {
            val diskMaxTxId = beginFlush()
            commitFlush(diskMaxTxId)
            needsCompaction = false
            compactionFailures = 0
        } catch (err: Exception) {
            compactionFailures++
            needsCompaction = true

            // 两种模式下都发告警；strictCompaction 只决定是否额外上抛。
            options.onWarning?.invoke(
                "onCompactionFailed",
                "Compaction failed (size $lastKnownDiskFileSize bytes, threshold " +
                    "${options.compactionThresholdBytes} bytes, consecutive failures " +
                    "$compactionFailures): $err"
            )

            if (options.strictCompaction) throw err
        }
    }

    /**
     * `close()` 时的兜底重试（best-effort）：一次瞬时失败不该拖到句柄生命周期结束。
     * 结果不上抛——失败已经通过 `onCompactionFailed` 上报，且 close 是清理路径。
     */
    private fun retryPendingCompaction() {
        if (!needsCompaction ||
            options.readOnly ||
            options.engineMode != StorageEngineMode.APPEND_ONLY_LOG
        ) {
            return
        }
        try {
            attemptCompaction()
        } catch (_: Exception) {
            // 已通过 onWarning 上报；close 不得因此失败。
        }
    }

    private fun applyMutation(entries: List<RawOpEntry>, applyMem: () -> Unit) {
        checkWritable()
        rwLock.write {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                // 顺序即正确性（C1/C2）：先 append，再 applyMem，最后才考虑压实
                val newFileSize = appendEntries(entries)
                applyMem()
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存（旧顺序是 applyMem → beginFlush）
                applySnapshotMutation(applyMem)
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
                // C1/C2：append → applyMem → 压实（独立锁周期）
                val newFileSize = appendEntries(listOf(entry))
                store.put(keyBytes, bBytes, Constants.VAL_TYPE_INT64)
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存
                applySnapshotMutation {
                    store.put(keyBytes, bBytes, Constants.VAL_TYPE_INT64)
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
                // C1/C2：append → applyMem → 压实（独立锁周期）
                val newFileSize = appendEntries(listOf(entry))
                namespaces.remove(normNs)
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存
                applySnapshotMutation { namespaces.remove(normNs) }
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

        fun applyOps() {
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
        }

        rwLock.write {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                // C1/C2：append → applyMem → 压实（独立锁周期）
                val newFileSize = appendEntries(writer.operations)
                applyOps()
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存
                applySnapshotMutation { applyOps() }
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
            val diskMaxTxId = beginFlush()
            commitFlush(diskMaxTxId)
        }
    }

    fun compact() {
        checkWritable()
        rwLock.write {
            val diskMaxTxId = beginFlush()
            commitFlush(diskMaxTxId)
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

            // D2：给此前失败的压实一次兜底重试机会
            retryPendingCompaction()

            // C9：释放连接锁（锁文件保留为标记文件）
            connectionLock?.close()
            connectionLock = null

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
