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
    val fileLockTimeoutMs: Long = 5000,
    // 决策 D1：`.bak` 是上一次轮转**之前**的一代，用它替换损坏的主库会静默回滚此后
    // 写入的全部数据；它现在的定位是**人工恢复用的离线副本**，默认不自动使用。
    val autoRecoverFromBackup: Boolean = false,
    val allowTailTruncation: Boolean = true,
    val syncMode: SyncMode = SyncMode.IMMEDIATE,
    val syncIntervalMs: Long = 100,
    val engineMode: StorageEngineMode = StorageEngineMode.SNAPSHOT,
    val onWarning: ((code: String, message: String) -> Unit)? = null
)
