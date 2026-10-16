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
    val autoRecoverFromBackup: Boolean = true,
    val allowTailTruncation: Boolean = true,
    val syncMode: SyncMode = SyncMode.IMMEDIATE,
    val syncIntervalMs: Long = 100,
    val engineMode: StorageEngineMode = StorageEngineMode.SNAPSHOT,
    val onWarning: ((code: String, message: String) -> Unit)? = null
)
