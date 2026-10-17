package ckv

enum class SyncMode {
    IMMEDIATE,
    DEFERRED,
    MANUAL
}

enum class StorageEngineMode {
    SNAPSHOT,
    APPEND_ONLY_LOG
}

data class DbOptions(
    val readOnly: Boolean = false,
    /**
     * 打开时等待其他连接释放文件锁的毫秒数（默认 10000）。
     *
     * 自 C9 起锁是由操作系统持有的内核锁、覆盖整个连接生命周期，因此该值**只是等待预算**：
     * 不再有基于锁文件年龄的判活，也不再有窃取协议。原名 `fileLockTimeoutMs` 与其余四语言的
     * `lockTimeoutMs` 不一致（默认值 5000 也与标准 10000 不同），按 C19 统一。
     */
    val lockTimeoutMs: Long = 10000,
    // 决策 D1：`.bak` 是上一次轮转**之前**的一代，用它替换损坏的主库会静默回滚此后
    // 写入的全部数据；它现在的定位是**人工恢复用的离线副本**，默认不自动使用。
    val autoRecoverFromBackup: Boolean = false,
    val syncMode: SyncMode = SyncMode.IMMEDIATE,
    val syncIntervalMs: Long = 100,
    /**
     * 底层存储引擎模式（默认 [StorageEngineMode.SNAPSHOT]）。
     *
     * **⚠️ SNAPSHOT 的写成本是 O(库大小)。**
     * 它每次写入都会**全量序列化整库并 fsync**，代价随库大小线性增长。这是默认配置，
     * 但没有任何运行时护栏或告警阻止你把它用在远大于建议规模的库上——到那时吞吐会比
     * [StorageEngineMode.APPEND_ONLY_LOG] 差两个数量级。
     *
     * 实测（本机 Windows 10，稳态写延迟中位数）：
     * | 存活条目 | SNAPSHOT + IMMEDIATE | APPEND_ONLY_LOG |
     * | ---: | ---: | ---: |
     * | 500 | 2.29 ms (~437 ops/s) | 0.18 ms (~5,500 ops/s)，与规模无关 |
     * | 2,000 | 2.93 ms (~341 ops/s) | 同上 |
     * | 5,000 | 4.43 ms (~226 ops/s) | 同上 |
     * | ~50,000（外推，约 5.7 MB） | ~40 ms (~25 ops/s) | 同上 |
     *
     * **选型建议**：数据量超过约 2 MB，或存在高频写入时，请改用
     * [StorageEngineMode.APPEND_ONLY_LOG]。
     */
    val engineMode: StorageEngineMode = StorageEngineMode.SNAPSHOT,
    /**
     * APPEND_ONLY_LOG 模式下触发自动压实的文件大小阈值（字节），0 表示关闭。
     * 默认 4 MB，与其余四语言一致（C8）。
     */
    val compactionThresholdBytes: Long = 4L * 1024 * 1024,
    /**
     * 压实失败时是否直接上抛（默认 false）。
     *
     * 默认（D2）：压实失败只告警 + 置 `needsCompaction` + 退避重试，**本次写仍返回成功**
     * ——触发压实的那次写已经 append + fsync 落盘，报错等于让调用方重试一个已经成功的写，
     * 而 `incrementInt64` 是 read-modify-write，重试会重复累加 delta。开启本项后压实失败
     * 会向上抛出，供"文件绝不能膨胀"的场景使用。
     */
    val strictCompaction: Boolean = false,
    val onWarning: ((code: String, message: String) -> Unit)? = null
)
