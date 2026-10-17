package ckv

class KeyItem(
    val key: ByteArray,
    val value: ByteArray,
    val valueType: Byte
) {
    fun clone(): KeyItem {
        val kCopy = key.copyOf()
        val vCopy = value.copyOf()
        return KeyItem(kCopy, vCopy, valueType)
    }
}

class NamespaceStore(val items: ArrayList<KeyItem> = ArrayList()) {

    /**
     * Σ(len(key) + len(value)) over [items]，由 [put] / [delete] 增量维护。
     * 死键比例压实触发器依赖它保持每次突变 O(1)；若直接修改 [items]，账本会静默失真。
     */
    var liveBytes: Long = 0L

    init {
        for (item in items) {
            liveBytes += (item.key.size + item.value.size).toLong()
        }
    }

    fun findIndex(key: ByteArray): Int {
        var low = 0
        var high = items.size - 1
        while (low <= high) {
            val mid = (low + high) ushr 1
            val cmp = BinaryHelper.compareBytes(items[mid].key, key)
            if (cmp < 0) {
                low = mid + 1
            } else if (cmp > 0) {
                high = mid - 1
            } else {
                return mid
            }
        }
        return -(low + 1)
    }

    fun put(key: ByteArray, value: ByteArray, valueType: Byte): KeyItem? {
        val idx = findIndex(key)
        val newItem = KeyItem(key, value, valueType)
        return if (idx >= 0) {
            val old = items[idx]
            liveBytes += (value.size - old.value.size).toLong()
            items[idx] = newItem
            old
        } else {
            val insertIdx = -(idx + 1)
            items.add(insertIdx, newItem)
            liveBytes += (key.size + value.size).toLong()
            null
        }
    }

    fun delete(key: ByteArray): KeyItem? {
        val idx = findIndex(key)
        return if (idx >= 0) {
            val old = items.removeAt(idx)
            liveBytes -= (old.key.size + old.value.size).toLong()
            old
        } else {
            null
        }
    }

    fun clone(): NamespaceStore {
        val clonedList = ArrayList<KeyItem>(items.size)
        for (item in items) {
            clonedList.add(item.clone())
        }
        return NamespaceStore(clonedList)
    }
}

class Snapshot(
    private val namespaces: Map<String, NamespaceStore>
) : AutoCloseable {

    private var isClosed = false

    private fun checkClosed() {
        if (isClosed) throw CkvException("Snapshot is already closed")
    }

    fun getBytes(ns: String?, key: String): ByteArray? {
        checkClosed()
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val store = namespaces[normNs] ?: return null
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val idx = store.findIndex(keyBytes)
        if (idx < 0) return null
        return store.items[idx].value.copyOf()
    }

    fun getString(ns: String?, key: String): String? {
        val bytes = getBytes(ns, key) ?: return null
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

    fun getInt64(ns: String?, key: String): Long? {
        val bytes = getBytes(ns, key) ?: return null
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

    fun getFloat64(ns: String?, key: String): Double? {
        val bytes = getBytes(ns, key) ?: return null
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

    fun getBool(ns: String?, key: String): Boolean? {
        val bytes = getBytes(ns, key) ?: return null
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

    fun containsKey(ns: String?, key: String): Boolean {
        checkClosed()
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val store = namespaces[normNs] ?: return false
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        return store.findIndex(keyBytes) >= 0
    }

    fun count(ns: String?): Int {
        checkClosed()
        val normNs = BinaryHelper.normalizeNamespace(ns)
        return namespaces[normNs]?.items?.size ?: 0
    }

    fun namespaces(): List<String> {
        checkClosed()
        val list = namespaces.keys.toMutableList()
        list.sortWith { a, b ->
            BinaryHelper.compareBytes(a.toByteArray(Charsets.UTF_8), b.toByteArray(Charsets.UTF_8))
        }
        return list
    }

    fun keys(ns: String?, prefix: String? = null): List<String> {
        checkClosed()
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

    fun scanRange(ns: String?, startKey: String?, endKey: String?): List<Entry> {
        checkClosed()
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

    override fun close() {
        isClosed = true
    }
}

class BatchWriter internal constructor(private val db: CkvDb? = null) {
    internal val operations = mutableListOf<RawOpEntry>()

    fun putBytes(ns: String?, key: String, value: ByteArray) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        validateKeyAndVal(nsBytes, keyBytes, value)
        operations.add(RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_BYTES, nsBytes, keyBytes, value))
    }

    fun putString(ns: String?, key: String, value: String) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = value.toByteArray(Charsets.UTF_8)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)
        operations.add(RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_STRING, nsBytes, keyBytes, valBytes))
    }

    fun putInt64(ns: String?, key: String, value: Long) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeInt64(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)
        operations.add(RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_INT64, nsBytes, keyBytes, valBytes))
    }

    fun putFloat64(ns: String?, key: String, value: Double) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeFloat64(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)
        operations.add(RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_FLOAT64, nsBytes, keyBytes, valBytes))
    }

    fun putBool(ns: String?, key: String, value: Boolean) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        val valBytes = BinaryHelper.encodeBool(value)
        validateKeyAndVal(nsBytes, keyBytes, valBytes)
        operations.add(RawOpEntry(Constants.OP_PUT, Constants.VAL_TYPE_BOOL, nsBytes, keyBytes, valBytes))
    }

    fun delete(ns: String?, key: String) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)
        validateKey(nsBytes, keyBytes)
        operations.add(RawOpEntry(Constants.OP_DELETE, Constants.VAL_TYPE_BYTES, nsBytes, keyBytes, ByteArray(0)))
    }

    fun dropNamespace(ns: String?) {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        if (nsBytes.size > Constants.MAX_NS_LEN) {
            throw KeyTooLongError("Namespace length ${nsBytes.size} exceeds max ${Constants.MAX_NS_LEN}")
        }
        operations.add(RawOpEntry(Constants.OP_DROP_NAMESPACE, Constants.VAL_TYPE_BYTES, nsBytes, ByteArray(0), ByteArray(0)))
    }

    /**
     * 按本批次可见的状态读取键：暂存区优先，暂存墓碑（delete / dropNamespace）一律视为
     * "不存在"，只有本批次未触碰的键才回落到已提交状态。
     *
     * 回落在数据库读锁内进行，因此能看到其他线程在批次执行期间的提交；需要冻结基准视图
     * 的调用方请先取 [CkvDb.snapshot]。暂存区从后向前扫描，最近一次操作优先。
     */
    fun getBytes(ns: String?, key: String): ByteArray? {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)

        for (i in operations.indices.reversed()) {
            val op = operations[i]
            if (!op.namespace.contentEquals(nsBytes)) continue
            when (op.opCode) {
                Constants.OP_PUT ->
                    if (op.key.contentEquals(keyBytes)) return op.value.copyOf()
                Constants.OP_DELETE ->
                    if (op.key.contentEquals(keyBytes)) return null
                Constants.OP_DROP_NAMESPACE -> return null
                Constants.BATCH_OP_CLEAR_STAGING -> return null
            }
        }

        return db?.getBytes(normNs, key)
    }

    /**
     * [getBytes] 的字符串版本：与 [CkvDb.getString] 一致，要求值以字符串类型写入，
     * 否则抛 [TypeMismatchError]；非法 UTF-8 抛 [InvalidUtf8Error]。
     */
    fun getString(ns: String?, key: String): String? {
        val normNs = BinaryHelper.normalizeNamespace(ns)
        val nsBytes = normNs.toByteArray(Charsets.UTF_8)
        val keyBytes = key.toByteArray(Charsets.UTF_8)

        for (i in operations.indices.reversed()) {
            val op = operations[i]
            if (!op.namespace.contentEquals(nsBytes)) continue
            when (op.opCode) {
                Constants.OP_PUT -> if (op.key.contentEquals(keyBytes)) {
                    if (op.valueType != Constants.VAL_TYPE_STRING) {
                        throw TypeMismatchError(
                            "Key '$key' has value_type ${op.valueType}, expected string (0x01)"
                        )
                    }
                    if (!BinaryHelper.isValidUtf8(op.value)) {
                        throw InvalidUtf8Error("Invalid UTF-8 sequence in key '$key'")
                    }
                    return String(op.value, Charsets.UTF_8)
                }
                Constants.OP_DELETE ->
                    if (op.key.contentEquals(keyBytes)) return null
                Constants.OP_DROP_NAMESPACE -> return null
                Constants.BATCH_OP_CLEAR_STAGING -> return null
            }
        }

        return db?.getString(normNs, key)
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
