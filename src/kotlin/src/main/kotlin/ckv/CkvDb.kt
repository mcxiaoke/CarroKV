package ckv

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileNotFoundException
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.lang.ref.WeakReference
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.withLock
import kotlin.concurrent.write

private const val ADAPTIVE_MIN_FILE_SIZE = 2L * 1024 * 1024
private const val ADAPTIVE_DEAD_RATIO_PERCENT = 50L

/**
 * [CkvDb] 是操作数据库的连接句柄（ADR-20261017）。
 * 每次 [open] 均返回独立的句柄包装，内部持有对进程内同路径唯一的 [SharedCkvDb] 底座引用。
 * 单个句柄调用 [close] 仅关闭当前连接并递减引用计数，底座与其他活跃句柄保持完好。
 */
class CkvDb internal constructor(internal val shared: SharedCkvDb) : AutoCloseable {
    private var isDisposed = false

    val sharedInstance: Any
        get() = shared

    private fun checkDisposed() {
        if (isDisposed) throw LockedError("Database is closed: operations are refused after close()")
    }

    private fun checkWritable() {
        checkDisposed()
        if (shared.options.readOnly) {
            throw LockedError("Database is opened in read-only mode")
        }
    }

    override fun close() {
        if (isDisposed) return
        isDisposed = true
        shared.release()
    }

    fun backup(dest: String) {
        checkDisposed()
        shared.backup(dest)
    }

    fun compact() {
        checkWritable()
        shared.compact()
    }

    fun flush() {
        checkWritable()
        shared.flush()
    }

    fun putBytes(ns: String?, key: String, value: ByteArray) {
        checkWritable()
        shared.putBytes(ns, key, value)
    }

    fun putString(ns: String?, key: String, value: String) {
        checkWritable()
        shared.putString(ns, key, value)
    }

    fun putInt64(ns: String?, key: String, value: Long) {
        checkWritable()
        shared.putInt64(ns, key, value)
    }

    fun putFloat64(ns: String?, key: String, value: Double) {
        checkWritable()
        shared.putFloat64(ns, key, value)
    }

    fun putBool(ns: String?, key: String, value: Boolean) {
        checkWritable()
        shared.putBool(ns, key, value)
    }

    fun incrementInt64(ns: String?, key: String, delta: Long = 1L): Long {
        checkWritable()
        return shared.incrementInt64(ns, key, delta)
    }

    fun delete(ns: String?, key: String): Boolean {
        checkWritable()
        return shared.delete(ns, key)
    }

    fun dropNamespace(ns: String?) {
        checkWritable()
        shared.dropNamespace(ns)
    }

    fun batch(action: (BatchWriter) -> Unit) {
        checkWritable()
        val writer = BatchWriter(this)
        action(writer)
        if (writer.operations.isEmpty()) return
        shared.applyBatchOperations(writer.operations)
    }

    fun getBytes(ns: String?, key: String): ByteArray? {
        checkDisposed()
        return shared.getBytes(ns, key)
    }

    fun getString(ns: String?, key: String): String? {
        checkDisposed()
        return shared.getString(ns, key)
    }

    fun getInt64(ns: String?, key: String): Long? {
        checkDisposed()
        return shared.getInt64(ns, key)
    }

    fun getFloat64(ns: String?, key: String): Double? {
        checkDisposed()
        return shared.getFloat64(ns, key)
    }

    fun getBool(ns: String?, key: String): Boolean? {
        checkDisposed()
        return shared.getBool(ns, key)
    }

    fun containsKey(ns: String?, key: String): Boolean {
        checkDisposed()
        return shared.containsKey(ns, key)
    }

    fun count(ns: String?): Int {
        checkDisposed()
        return shared.count(ns)
    }

    fun namespaces(): List<String> {
        checkDisposed()
        return shared.namespaces()
    }

    fun keys(ns: String?, prefix: String? = null): List<String> {
        checkDisposed()
        return shared.keys(ns, prefix)
    }

    fun scanRange(ns: String?, startKey: String?, endKey: String?): List<Entry> {
        checkDisposed()
        return shared.scanRange(ns, startKey, endKey)
    }

    fun snapshot(): Snapshot {
        checkDisposed()
        return shared.snapshot()
    }

    fun watch(callback: KeyChangeCallback): WatcherHandle {
        checkDisposed()
        return shared.watch(callback)
    }

    fun stats(): DbStats {
        checkDisposed()
        return shared.stats()
    }

    fun fileSize(): Long {
        checkDisposed()
        return shared.fileSize()
    }

    companion object {

        internal val registryLock = Any()
        private val registry = mutableMapOf<String, WeakReference<SharedCkvDb>>()

        /**
         * reentrant open 的选项冲突检查（§3.1 冻结）。
         *
         * **`readOnly` 冲突必须抛错，不得静默复用。** 命中单例时若只发一条 `onWarning`
         * 就返回原实例，调用方以 `readOnly = true` 打开却拿到一个读写实例——那是静默提权；
         * 而 `onWarning` 默认是 `null`，默认配置下连告警都看不见。反方向（要写拿到只读实例）
         * 同样会让后续写入以 `LockedError` 的形式在远处失败、误导排查。两个方向都拒绝。
         *
         * `engineMode` / `syncMode` 冲突维持告警：它们只造成性能与落盘语义差异，
         * 不构成权限问题。
         */
        private fun checkOptionsConflict(existingOpts: DbOptions, newOpts: DbOptions) {
            if (existingOpts.readOnly != newOpts.readOnly) {
                throw ConflictingOptionsError(
                    "readOnly conflict on reentrant open: existing=${existingOpts.readOnly}, " +
                        "requested=${newOpts.readOnly}. The in-process singleton is returned " +
                        "unchanged by contract, so it cannot honour the requested permission. " +
                        "Close the existing handle first, or open a different canonical path."
                )
            }

            val conflicts = mutableListOf<String>()
            if (existingOpts.engineMode != newOpts.engineMode) {
                conflicts.add("engineMode (existing=${existingOpts.engineMode}, requested=${newOpts.engineMode})")
            }
            if (existingOpts.syncMode != newOpts.syncMode) {
                conflicts.add("syncMode (existing=${existingOpts.syncMode}, requested=${newOpts.syncMode})")
            }
            if (conflicts.isNotEmpty()) {
                val msg = "Options conflict on reentrant open: ${conflicts.joinToString(", ")}. Using existing singleton options."
                if (newOpts.onWarning != null) {
                    newOpts.onWarning.invoke("onOptionsConflict", msg)
                } else if (existingOpts.onWarning != null) {
                    existingOpts.onWarning.invoke("onOptionsConflict", msg)
                }
            }
        }

        fun backupFile(source: String, dest: String, options: DbOptions = DbOptions()) {
            val roOpts = options.copy(readOnly = true)
            open(source, roOpts).use { db ->
                db.backup(dest)
            }
        }

        fun open(path: String, options: DbOptions = DbOptions()): CkvDb {
            val canonicalPath = File(path).canonicalPath
            synchronized(registryLock) {
                val existing = registry[canonicalPath]?.get()
                if (existing != null && !existing.isDisposed) {
                    checkOptionsConflict(existing.options, options)
                    existing.refCount++
                    return CkvDb(existing)
                }
                val shared = SharedCkvDb(canonicalPath, options)
                registry[canonicalPath] = WeakReference(shared)
                return CkvDb(shared)
            }
        }

        internal fun unregisterInstance(path: String, instance: SharedCkvDb) {
            val canonicalPath = File(path).canonicalPath
            synchronized(registryLock) {
                // FIX-07：只移除仍指向自己的条目。以前按键移除，会在"本实例已关闭、
                // 同路径又被新实例注册"的时序下删掉新实例的条目。
                if (registry[canonicalPath]?.get() === instance) {
                    registry.remove(canonicalPath)
                }
            }
        }
    }
}


class SharedCkvDb internal constructor(
    internal val path: String,
    internal val options: DbOptions
) : AutoCloseable {
    internal var refCount = 1
    internal var isDisposed = false

    private val rwLock = ReentrantReadWriteLock()
    private val namespaces = mutableMapOf<String, NamespaceStore>()
    private var currentMaxTxId = 0L
    private var lastKnownDiskMaxTxId = 0L
    private var lastKnownDiskFileSize = 0L
    private var isDirty = false
    
    /** 目录 fsync 不可用的告警只发一次（规范 §1.2 第 7 条：best-effort，失败不影响轮转）。 */
    private var dirSyncWarned = false

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

    /**
     * FIX-01：串行化所有写 `<db>.tmp` 的路径。后台刷盘（C16：读锁内取快照、锁外序列化）
     * 与显式 [flush] / [compact] / [close] 写的是同一个 `.tmp`，没有这把门会互相截断覆盖。
     * 锁序恒为 flushGate -> rwLock；AOL 压实路径持有 rwLock 且不取 flushGate。
     */
    private val flushGate = ReentrantLock()

    /**
     * FIX-01：每次已提交的突变自增。后台刷盘把快照与代际一起记录，轮转前若代际已变，
     * 说明快照陈旧（有更新的写入或已显式落盘），必须丢弃，否则会覆盖已确认写入并误清 [isDirty]。
     */
    private var dirtyGeneration = 0L

    private class WatcherSlot(
        val id: Long,
        val callback: KeyChangeCallback,
        val db: SharedCkvDb
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
        // FIX-22：close() 已释放跨进程锁，继续写会绕过唯一的多写者保护。
        if (isDisposed) throw LockedError("Database is closed: writes are refused after close()")
    }

    private fun checkWritable() {
        checkDisposed()
        if (options.readOnly) {
            throw LockedError("Database is opened in read-only mode")
        }
    }

    /**
     * **唯一的写入口收口**：先做可写性前置校验（已关闭 / 只读），再取写锁执行写事务。
     *
     * 旧实现把 `checkWritable()` 手写在各个公开方法里，漏掉一处就是一个洞：
     * `incrementInt64` 与 `dropNamespace` 直接 `rwLock.write { }`，于是在
     * `readOnly = true` 下：
     *  - AOL 引擎会**真的**往 `.db` 追加帧并 fsync（appendEntries 内部只有 precheckDiskState，
     *    没有任何可写性校验）；
     *  - SNAPSHOT + Immediate 会先 `getOrPut` 塞入空 store、之后才抛 LockedError，
     *    于是失败却在 `namespaces()` 里留下一个幽灵命名空间。
     *
     * 新增写方法时**只能**经由本函数，禁止旁路 `rwLock.write`。
     * 注意本函数不可重入：调用方不得在已持有写锁的代码里再调用它。
     */
    private inline fun <T> writeLocked(action: () -> T): T {
        checkWritable()
        return rwLock.write { action() }
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
                // 主库不存在，此时 `.tmp` 是唯一完好的数据。必须区分三类失败：
                //  1. `.tmp` 读不出来 / 内容确实不可解析 → 删除它，继续走分支③（与旧行为一致）；
                //  2. `.tmp` 有效但 `renameTo` 返回 false（杀软/索引器占用、目录不可写）
                //     → **晋升失败必须让 open 失败**，保留 `.tmp` 原样；
                //  3. 晋升成功 → 正常加载。
                // 旧实现丢弃 renameTo 的返回值后照样 applyParsed 并置 dbExists = true，于是
                // 内存里有数据、磁盘上没有 .db，库进入不可写状态；且 catch 无条件删除 `.tmp`，
                // 把唯一的好数据一并销毁 —— C1-C3 之后的第 4 处静默数据丢失。
                val tmpBytes = try {
                    tmpFile.readBytes()
                } catch (_: Exception) {
                    null
                }
                val parsed = tmpBytes?.let {
                    try { FrameLoader.loadAllWithRecovery(it) } catch (_: Exception) { null }
                }
                when {
                    parsed == null -> {
                        // 内容确实不可解析（或读不出来）：删除 .tmp 是安全的。
                        try { tmpFile.delete() } catch (_: Exception) {}
                    }
                    !tmpFile.renameTo(dbFile) -> {
                        throw CorruptedError(
                            "Primary file '$path' is missing and a valid '.tmp' could not be promoted " +
                                "to it (rename failed); the only intact copy is still at " +
                                "'${tmpFile.path}': close whatever holds the file handle and reopen"
                        )
                    }
                    else -> {
                        applyParsed(parsed.namespaces, parsed.maxTxId, dbFile.length())
                        dbExists = true
                    }
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

    /**
     * 后台定时刷盘（C16）：采用短读锁提取快照、锁外落盘写 .tmp 与 fsync、短写锁原子替换，
     * 避免长时间持有写锁阻塞并发读者。
     */
    private fun flushIfDirty() {
        // FIX-01：Snapshot 模式的后台刷盘会写 `.tmp`，必须与显式路径互斥。
        // AOL 只做 fsync，不取门（也就不会与持 rwLock 的压实路径形成循环等待）。
        if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
            flushIfDirtyLocked()
        } else {
            flushGate.withLock { flushIfDirtyLocked() }
        }
    }

    private fun flushIfDirtyLocked() {
        var generation = 0L
        val plan = rwLock.read {
            if (isDisposed || options.readOnly || !isDirty) null
            else if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                Pair(StorageEngineMode.APPEND_ONLY_LOG, null)
            } else {
                try {
                    val diskMaxTxId = beginFlush()
                    val pair = buildSnapshotBytes(diskMaxTxId)
                    generation = dirtyGeneration
                    Pair(StorageEngineMode.SNAPSHOT, pair)
                } catch (_: Exception) {
                    null
                }
            }
        } ?: return

        if (plan.first == StorageEngineMode.APPEND_ONLY_LOG) {
            val dbFile = File(path)
            if (dbFile.exists()) {
                try {
                    FileOutputStream(dbFile, true).use { fos ->
                        fos.fd.sync()
                    }
                } catch (_: Exception) {}
            }
            rwLock.write {
                if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                    isDirty = false
                }
            }
            return
        }

        // Snapshot 模式：锁外执行 .tmp 写入与 fsync (C16)
        val snapshot = plan.second ?: return
        val (bytes, newTxId) = snapshot
        val dbFile = File(path)
        val tmpFile = File("$path.tmp")
        val bakFile = File("$path.bak")
        try {
            tmpFile.writeBytes(bytes)
            FileOutputStream(tmpFile, true).use { fos ->
                fos.fd.sync()
            }
        } catch (_: Exception) {
            try { tmpFile.delete() } catch (_: Exception) {}
            return
        }

        // 短写锁完成原子轮转与元数据更新
        rwLock.write {
            // FIX-01：快照陈旧时丢弃本次 `.tmp`，不轮转、不清脏标记、不动磁盘账本。
            if (isDisposed || options.readOnly || !isDirty || dirtyGeneration != generation) {
                try { tmpFile.delete() } catch (_: Exception) {}
                return@write
            }
            try {
                if (dbFile.exists()) {
                    FileRotation.stageAndReplace(dbFile, bakFile)
                }
                FileRotation.replaceWithRetry(tmpFile, dbFile)
                fsyncParentDirBestEffort(dbFile)
                currentMaxTxId = maxOf(currentMaxTxId, newTxId)
                lastKnownDiskMaxTxId = currentMaxTxId
                lastKnownDiskFileSize = dbFile.length()
                isDirty = false
            } catch (_: Exception) {
                try { tmpFile.delete() } catch (_: Exception) {}
            }
        }
    }

    /**
     * 轮转之后的目录项落盘（规范 §1.2 第 7 条）：best-effort，失败只告警一次，
     * 绝不让轮转失败——`.db` 已经被完整替换，抛错只会让调用方误判写入失败（同 D2）。
     */
    private fun fsyncParentDirBestEffort(file: File) {
        val parent = file.parentFile ?: return
        if (FileRotation.fsyncDirectory(parent)) return
        if (dirSyncWarned) return
        dirSyncWarned = true
        options.onWarning?.invoke(
            "onDirSyncUnavailable",
            "Directory fsync is unavailable on this platform/filesystem; " +
                "a power loss immediately after rotation may roll back the last rename " +
                "(directory: ${parent.path})"
        )
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

        // RC2：一次 readAttributes 同时回答"是否存在"与"多大"，取代 exists()+length()
        // 两次路径查询（在带过滤驱动的文件系统上，每次路径查询都是几十微秒）。
        // 读取失败按"不存在"处理，与 File.exists() 吞掉 IO 错误返回 false 的旧语义一致。
        val size: Long? = try {
            Files.readAttributes(dbFile.toPath(), java.nio.file.attribute.BasicFileAttributes::class.java).size()
        } catch (_: java.io.IOException) {
            null
        }

        if (size != null) {
            diskFileSize = size
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
            fsyncParentDirBestEffort(dbFile)

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

            // RC2：先打开追加流，再用 channel.size() 判断是否需要写文件头。
            // 旧实现是 exists() + length() 两次路径查询，加上"另开一个流写头"的第二次打开；
            // 目录缺失只可能发生在新建库的首次写，因此把 mkdirs 收进 NotFound 分支。
            val fos = try {
                FileOutputStream(dbFile, true)
            } catch (_: FileNotFoundException) {
                dbFile.parentFile?.mkdirs()
                FileOutputStream(dbFile, true)
            }

            var newFileSize: Long
            var finalTxId: Long
            fos.use {
                if (fos.channel.size() == 0L) {
                    fos.write(FileHeader.createBytes())
                    fos.fd.sync()
                }

                val nextTxId = maxOf(currentMaxTxId, diskMaxTxId) + 1L
                val frameEntries = mutableListOf<RawOpEntry>()
                var currentPayloadLen = 0
                var currentTx = nextTxId

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
                finalTxId = currentTx - 1L
                newFileSize = fos.channel.size()
            }

            currentMaxTxId = finalTxId
            lastKnownDiskMaxTxId = finalTxId
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

        // FIX-21：按“垃圾量”触发，而不是按文件绝对大小。压实后文件恰等于存活大小，
        // 旧条件在存活数据达到阈值后对**每一次写**都成立（6MB 存活实测 26ms/写，
        // 正常摊销路径约 0.2ms）。garbage 由各命名空间的 O(1) 账本派生，无需新增状态，
        // 重开库后依然正确（进程内“累计追加字节”计数器会清零）。
        val live = minOf(livePayloadBytes(), newFileSize)
        val garbage = newFileSize - live
        val effectiveThreshold = maxOf(options.compactionThresholdBytes, live / 4)
        if (garbage >= effectiveThreshold) {
            attemptCompaction()
            return
        }

        // 自适应触发：被墓碑与覆写值占据的日志永远长不到阈值，改按死键比例回收。
        if (shouldCompactByDeadRatio(newFileSize)) {
            attemptCompaction()
        }
    }

    /** 所有命名空间的存活载荷合计（key + value 字节数）。O(#命名空间)，不含条目遍历。 */
    private fun livePayloadBytes(): Long {
        var total = 0L
        for (store in namespaces.values) {
            total += store.liveBytes
        }
        return total
    }

    /**
     * 未达大小阈值的日志是否已被死数据主导。`liveBytes` 只计载荷，恒小于文件大小
     * （文件头与帧头开销是差额）；夹紧后即便账本偶有偏差也不会算出荒谬比例。
     */
    private fun shouldCompactByDeadRatio(fileSize: Long): Boolean {
        if (fileSize < ADAPTIVE_MIN_FILE_SIZE) return false
        val live = minOf(livePayloadBytes(), fileSize)
        return (fileSize - live) * 100 >= fileSize * ADAPTIVE_DEAD_RATIO_PERCENT
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
        writeLocked {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                // 顺序即正确性（C1/C2）：先 append，再 applyMem，最后才考虑压实
                val newFileSize = appendEntries(entries)
                applyMem()
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存（旧顺序是 applyMem → beginFlush）
                applySnapshotMutation(applyMem)
            }
            // FIX-01：后台刷盘以此判断自己的快照是否已过时。
            dirtyGeneration++
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

        writeLocked {
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
            // FIX-01：与 applyMutation 保持一致，让后台刷盘能判断自己的快照是否过时。
            dirtyGeneration++
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
        writeLocked {
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
            dirtyGeneration++
        }
        notifyWatchers(events)
    }

    fun clear(ns: String?) {
        dropNamespace(ns)
    }

    internal fun applyBatchOperations(operations: List<RawOpEntry>) {
        if (operations.isEmpty()) return

        val events = mutableListOf<KeyChangeEvent>()

        fun applyOps() {
            for (op in operations) {
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

        writeLocked {
            if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
                // C1/C2：append → applyMem → 压实（独立锁周期）
                val newFileSize = appendEntries(operations)
                applyOps()
                maybeCompactAfterWrite(newFileSize)
            } else {
                // C6：先磁盘预检、再改内存
                applySnapshotMutation { applyOps() }
            }
            dirtyGeneration++
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
        // FIX-01：与后台刷盘共用 `.tmp`（锁序：flushGate -> rwLock）。
        flushGate.withLock {
            rwLock.write {
                flushInternal()
            }
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
        // FIX-01：与后台刷盘共用 `.tmp`。
        flushGate.withLock {
            rwLock.write {
                val diskMaxTxId = beginFlush()
                commitFlush(diskMaxTxId)
            }
        }
    }

    override fun close() {
        release()
    }

    internal fun release() {
        val shouldClose = synchronized(CkvDb.registryLock) {
            refCount--
            if (refCount <= 0) {
                CkvDb.unregisterInstance(path, this)
                true
            } else {
                false
            }
        }
        if (shouldClose) {
            closeUnderlying()
        }
    }

    
    fun backup(dest: String) {
        checkDisposed()
        rwLock.read {
            val (bytes, _) = buildSnapshotBytes(0L)
            val destFile = File(dest)
            val tmpFile = File("$dest.tmp")
            tmpFile.parentFile?.mkdirs()
            tmpFile.writeBytes(bytes)
            FileOutputStream(tmpFile, true).use { fos ->
                fos.fd.sync()
            }
            FileRotation.replaceWithRetry(tmpFile, destFile)
        }
    }

    internal fun closeUnderlying() {
        val exec = rwLock.write {
            if (isDisposed) return
            syncExecutor.also { syncExecutor = null }
        }

        // FIX-01：必须在持锁之外等待后台任务结束——工作线程可能正等着 rwLock 完成轮转，
        // 持锁 awaitTermination 会自锁（旧实现靠 1 秒超时"解开"，代价是可能提前返回）。
        exec?.shutdown()
        try {
            exec?.awaitTermination(Long.MAX_VALUE, TimeUnit.DAYS)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }

        flushGate.withLock {
            rwLock.write {
                if (isDisposed) return@write

                if (isDirty && !options.readOnly) {
                    try {
                        flushInternal()
                    } catch (e: Exception) {
                        // FIX-06：最终刷盘失败必须上报，不能静默吞掉。
                        options.onWarning?.invoke(
                            "onFinalFlushFailed",
                            "Final flush during close failed; pending writes may be lost: $e"
                        )
                    }
                }

                // D2：给此前失败的压实一次兜底重试机会
                retryPendingCompaction()

                // C9：释放连接锁（锁文件保留为标记文件）
                connectionLock?.close()
                connectionLock = null

                isDisposed = true
                CkvDb.unregisterInstance(path, this)
            }
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


}
