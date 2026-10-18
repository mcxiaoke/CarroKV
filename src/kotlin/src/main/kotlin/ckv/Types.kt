package ckv

data class Entry(
    val key: String,
    val value: ByteArray,
    val valueType: Byte
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is Entry) return false
        return key == other.key && valueType == other.valueType && value.contentEquals(other.value)
    }

    override fun hashCode(): Int {
        var result = key.hashCode()
        result = 31 * result + value.contentHashCode()
        result = 31 * result + valueType
        return result
    }
}

enum class KeyChangeOp {
    PUT,
    DELETE,
    DROP_NAMESPACE,
    CLEAR
}

data class KeyChangeEvent(
    val namespace: String,
    val key: String,
    val op: KeyChangeOp,
    val value: ByteArray? = null,
    val valueType: Byte? = null
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is KeyChangeEvent) return false
        if (namespace != other.namespace || key != other.key || op != other.op || valueType != other.valueType) return false
        if (value == null) return other.value == null
        return other.value != null && value.contentEquals(other.value)
    }

    override fun hashCode(): Int {
        var result = namespace.hashCode()
        result = 31 * result + key.hashCode()
        result = 31 * result + op.hashCode()
        result = 31 * result + (value?.contentHashCode() ?: 0)
        result = 31 * result + (valueType?.toInt() ?: 0)
        return result
    }
}

typealias KeyChangeCallback = (KeyChangeEvent) -> Unit

fun interface WatcherHandle : AutoCloseable {
    fun unwatch()
    override fun close() = unwatch()
}

data class DbStats(
    val totalEntries: Long,
    val currentMaxTxId: Long
)
