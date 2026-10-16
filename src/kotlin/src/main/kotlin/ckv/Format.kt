package ckv

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.OutputStream
import java.io.RandomAccessFile

class FileHeader {
    companion object {
        fun createBytes(): ByteArray {
            val buf = ByteArray(Constants.FILE_HEADER_LEN)
            System.arraycopy(Constants.FILE_MAGIC, 0, buf, 0, 8)
            BinaryHelper.writeShortLE(buf, 8, Constants.FORMAT_READ_VER)
            BinaryHelper.writeShortLE(buf, 10, Constants.FORMAT_WRITE_VER)
            BinaryHelper.writeIntLE(buf, 12, 0) // HeaderFlags
            // 16..28 reserved 0x00
            val crc = Crc32.compute(buf, 0, 28)
            BinaryHelper.writeIntLE(buf, 28, crc)
            return buf
        }

        fun validateDatabaseBytes(bytes: ByteArray) {
            if (bytes.size < Constants.FILE_HEADER_LEN) {
                throw CorruptedError("File size ${bytes.size} is less than header length 32")
            }
            for (i in 0 until 8) {
                if (bytes[i] != Constants.FILE_MAGIC[i]) {
                    throw CorruptedError("Invalid file magic")
                }
            }
            val readVer = BinaryHelper.readShortLE(bytes, 8).toInt() and 0xFFFF
            if (readVer > Constants.FORMAT_READ_VER.toInt() and 0xFFFF) {
                throw UnsupportedVersionError("Unsupported read version $readVer (expected <= ${Constants.FORMAT_READ_VER})")
            }
            val storedCrc = BinaryHelper.readIntLE(bytes, 28)
            val calcCrc = Crc32.compute(bytes, 0, 28)
            if (storedCrc != calcCrc) {
                throw CorruptedError(
                    "File header CRC mismatch: stored 0x${Integer.toHexString(storedCrc)}, calculated 0x${Integer.toHexString(calcCrc)}"
                )
            }
        }
    }
}

class RawOpEntry(
    val opCode: Byte,
    val valueType: Byte,
    val namespace: ByteArray,
    val key: ByteArray,
    val value: ByteArray
) {
    fun encodedLen(): Int {
        return Constants.ENTRY_FIXED_HEADER_LEN + namespace.size + key.size + value.size
    }

    fun encodeTo(os: OutputStream) {
        val hdr = ByteArray(Constants.ENTRY_FIXED_HEADER_LEN)
        hdr[0] = opCode
        hdr[1] = valueType
        BinaryHelper.writeShortLE(hdr, 2, namespace.size.toShort())
        BinaryHelper.writeShortLE(hdr, 4, key.size.toShort())
        BinaryHelper.writeIntLE(hdr, 6, value.size)
        os.write(hdr)
        if (namespace.isNotEmpty()) os.write(namespace)
        os.write(key)
        os.write(value)
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is RawOpEntry) return false
        return opCode == other.opCode &&
                valueType == other.valueType &&
                namespace.contentEquals(other.namespace) &&
                key.contentEquals(other.key) &&
                value.contentEquals(other.value)
    }

    override fun hashCode(): Int {
        var result = opCode.toInt()
        result = 31 * result + valueType.toInt()
        result = 31 * result + namespace.contentHashCode()
        result = 31 * result + key.contentHashCode()
        result = 31 * result + value.contentHashCode()
        return result
    }
}

object BatchFrameWriter {
    fun writeFrame(os: OutputStream, txId: Long, entries: List<RawOpEntry>) {
        if (entries.isEmpty()) {
            throw CorruptedError("OpCount must be >= 1 for a frame")
        }
        if (entries.size > Constants.MAX_OPS_PER_FRAME) {
            throw CorruptedError("OpCount ${entries.size} exceeds max ${Constants.MAX_OPS_PER_FRAME}")
        }

        val baos = ByteArrayOutputStream()
        for (e in entries) {
            e.encodeTo(baos)
        }
        val payload = baos.toByteArray()
        if (payload.size > Constants.MAX_PAYLOAD_LEN) {
            throw PayloadTooLargeError("Payload size ${payload.size} exceeds max ${Constants.MAX_PAYLOAD_LEN}")
        }

        val payloadCrc = Crc32.compute(payload)

        val tail20 = ByteArray(20)
        BinaryHelper.writeLongLE(tail20, 0, txId)
        BinaryHelper.writeIntLE(tail20, 8, entries.size)
        BinaryHelper.writeIntLE(tail20, 12, payload.size)
        BinaryHelper.writeIntLE(tail20, 16, payloadCrc)

        val headerCrc = Crc32.compute(tail20)

        val frameHeader = ByteArray(Constants.FRAME_HEADER_LEN)
        BinaryHelper.writeIntLE(frameHeader, 0, Constants.FRAME_MAGIC)
        BinaryHelper.writeIntLE(frameHeader, 4, headerCrc)
        System.arraycopy(tail20, 0, frameHeader, 8, 20)

        os.write(frameHeader)
        os.write(payload)
    }
}

class ParsedResult(
    val namespaces: Map<String, NamespaceStore>,
    val maxTxId: Long,
    val effectiveLength: Long
)

private class LoadOp(
    val seq: Int,
    val opType: Byte,
    val valType: Byte,
    val ns: String,
    val key: ByteArray,
    val value: ByteArray
)

object FrameLoader {
    fun scanMaxTxId(file: File): Long {
        val fileSize = file.length()
        if (fileSize <= Constants.FILE_HEADER_LEN) return 0L
        RandomAccessFile(file, "r").use { raf ->
            raf.seek(Constants.FILE_HEADER_LEN.toLong())
            var offset = Constants.FILE_HEADER_LEN.toLong()
            var lastTxId = 0L
            val hdr = ByteArray(Constants.FRAME_HEADER_LEN)

            while (offset < fileSize) {
                if (fileSize - offset < Constants.FRAME_HEADER_LEN) {
                    throw CorruptedError("Truncated frame header in fast scan")
                }
                raf.readFully(hdr)
                val magic = BinaryHelper.readIntLE(hdr, 0)
                if (magic != Constants.FRAME_MAGIC) {
                    throw CorruptedError("Invalid frame magic in fast scan")
                }
                val txId = BinaryHelper.readLongLE(hdr, 8)
                val payloadLen = BinaryHelper.readIntLE(hdr, 20) and 0x7FFFFFFF
                lastTxId = txId
                offset += Constants.FRAME_HEADER_LEN + payloadLen
                raf.seek(offset)
            }
            return lastTxId
        }
    }

    /**
     * Whether the region of [bytes] starting at [from] consists only of zeros.
     *
     * Filesystems may materialise a crash-truncated (never written) tail as
     * zeros. Treating such a region as media damage would turn a routine crash
     * into a hard failure, so it is classified as an unwritten tail instead.
     * RocksDB tolerates zeroed trailing data in its default WAL recovery mode for
     * the same reason.
     */
    private fun tailRegionIsAllZero(bytes: ByteArray, from: Int): Boolean {
        for (i in from until bytes.size) {
            if (bytes[i] != 0.toByte()) return false
        }
        return true
    }

    /**
     * Loads frames and heals a torn tail.
     *
     * Damage classification (C4). The log is append-only, so a torn write can only
     * ever leave the file ending *inside* the last frame:
     *
     * * the file ends before a frame is complete (short header, or a payload that
     *   runs past EOF) -> torn tail -> warn, keep every intact frame before it;
     * * the damaged region is entirely zero-filled -> unwritten tail (how some
     *   filesystems materialise a crash) -> treated like a torn tail;
     * * a complete frame whose magic / header CRC / payload CRC / entry encoding
     *   is invalid -> those bytes were written and are wrong, i.e. media damage ->
     *   [CorruptedError], never a silent truncation, because intact frames may
     *   follow the damaged one and truncating would discard them.
     *
     * `allowTailTruncation = false` disables healing entirely: every anomaly is
     * reported as [CorruptedError] (snapshot mode, and recovery sources like `.bak`).
     */
    fun loadAllWithRecovery(
        bytes: ByteArray,
        allowTailTruncation: Boolean = false,
        onWarning: ((code: String, message: String) -> Unit)? = null
    ): ParsedResult {
        FileHeader.validateDatabaseBytes(bytes)

        var offset = Constants.FILE_HEADER_LEN
        val fileSize = bytes.size
        var lastTxId = 0L
        var effectiveLength = Constants.FILE_HEADER_LEN.toLong()
        val allOps = mutableListOf<LoadOp>()
        var seqCounter = 0

        while (offset < fileSize) {
            val remaining = fileSize - offset
            if (remaining < Constants.FRAME_HEADER_LEN) {
                if (allowTailTruncation) {
                    onWarning?.invoke("onTailTruncated", "Truncated partial frame header at offset $offset")
                    break
                }
                throw CorruptedError("Truncated frame header at offset $offset")
            }

            val magic = BinaryHelper.readIntLE(bytes, offset)
            if (magic != Constants.FRAME_MAGIC) {
                // A complete frame header fits in the file, so these bytes were
                // written: this cannot be a torn write, only media damage or an
                // unwritten (zero-filled) tail.
                if (allowTailTruncation && tailRegionIsAllZero(bytes, offset)) {
                    onWarning?.invoke("onTailTruncated", "Truncated zero-filled tail at offset $offset")
                    break
                }
                throw CorruptedError("Corrupted frame header: invalid frame magic 0x${Integer.toHexString(magic)} at offset $offset")
            }

            val storedHdrCrc = BinaryHelper.readIntLE(bytes, offset + 4)
            val calcHdrCrc = Crc32.compute(bytes, offset + 8, 20)
            if (storedHdrCrc != calcHdrCrc) {
                // The header is present and its content is wrong (media damage).
                throw CorruptedError("Corrupted frame header CRC mismatch at offset $offset")
            }

            val txId = BinaryHelper.readLongLE(bytes, offset + 8)
            val opCount = BinaryHelper.readIntLE(bytes, offset + 16)
            val payloadLen = BinaryHelper.readIntLE(bytes, offset + 20)
            val storedPayloadCrc = BinaryHelper.readIntLE(bytes, offset + 24)

            if (txId <= lastTxId) {
                throw CorruptedError("Non-monotonic TxID: got $txId <= previous $lastTxId at offset $offset")
            }
            if (opCount < 1 || opCount > Constants.MAX_OPS_PER_FRAME) {
                throw CorruptedError("Invalid opCount $opCount at offset $offset")
            }
            if (payloadLen < 0 || payloadLen > Constants.MAX_PAYLOAD_LEN) {
                throw CorruptedError("Invalid payload length $payloadLen at offset $offset")
            }

            val frameTotalLen = Constants.FRAME_HEADER_LEN + payloadLen
            if (remaining < frameTotalLen) {
                if (allowTailTruncation) {
                    onWarning?.invoke("onTailTruncated", "Truncated frame payload at offset $offset")
                    break
                }
                throw CorruptedError("Truncated frame payload at offset $offset")
            }

            val calcPayloadCrc = Crc32.compute(bytes, offset + Constants.FRAME_HEADER_LEN, payloadLen)
            if (storedPayloadCrc != calcPayloadCrc) {
                // The whole frame is present -- the header validated and the payload
                // length lies inside the file -- yet its content does not match the
                // recorded CRC: the bytes were written and are wrong (media damage).
                throw CorruptedError("Corrupted frame payload CRC mismatch at offset $offset")
            }

            // Decode entries inside frame payload
            var pOffset = offset + Constants.FRAME_HEADER_LEN
            val pEnd = pOffset + payloadLen

            for (i in 0 until opCount) {
                if (pEnd - pOffset < Constants.ENTRY_FIXED_HEADER_LEN) {
                    throw CorruptedError("Truncated entry fixed header at offset $pOffset")
                }
                val opCode = bytes[pOffset]
                val valType = bytes[pOffset + 1]
                val nsLen = BinaryHelper.readShortLE(bytes, pOffset + 2).toInt() and 0xFFFF
                val keyLen = BinaryHelper.readShortLE(bytes, pOffset + 4).toInt() and 0xFFFF
                val valLen = BinaryHelper.readIntLE(bytes, pOffset + 6)

                if (nsLen > Constants.MAX_NS_LEN) {
                    throw CorruptedError("Namespace length $nsLen exceeds max ${Constants.MAX_NS_LEN}")
                }
                if (opCode != Constants.OP_DROP_NAMESPACE && keyLen < 1) {
                    throw CorruptedError("Key length must be >= 1 for opCode $opCode")
                }
                if (keyLen > Constants.MAX_KEY_LEN) {
                    throw CorruptedError("Key length $keyLen exceeds max ${Constants.MAX_KEY_LEN}")
                }
                if (valLen < 0 || valLen > Constants.MAX_VAL_LEN) {
                    throw CorruptedError("Value length $valLen exceeds max ${Constants.MAX_VAL_LEN}")
                }

                pOffset += Constants.ENTRY_FIXED_HEADER_LEN
                val entryTotalLen = nsLen + keyLen + valLen
                if (pEnd - pOffset < entryTotalLen) {
                    throw CorruptedError("Truncated entry body at offset $pOffset")
                }

                val nsBytes = ByteArray(nsLen)
                if (nsLen > 0) System.arraycopy(bytes, pOffset, nsBytes, 0, nsLen)
                pOffset += nsLen

                val keyBytes = ByteArray(keyLen)
                System.arraycopy(bytes, pOffset, keyBytes, 0, keyLen)
                pOffset += keyLen

                val valBytes = ByteArray(valLen)
                if (valLen > 0) System.arraycopy(bytes, pOffset, valBytes, 0, valLen)
                pOffset += valLen

                if (!BinaryHelper.isValidUtf8(nsBytes)) {
                    throw InvalidUtf8Error("Namespace contains invalid UTF-8")
                }
                if (!BinaryHelper.isValidUtf8(keyBytes)) {
                    throw InvalidUtf8Error("Key contains invalid UTF-8")
                }

                val nsStr = if (nsBytes.isEmpty()) "" else String(nsBytes, Charsets.UTF_8)
                allOps.add(LoadOp(seqCounter++, opCode, valType, nsStr, keyBytes, valBytes))
            }

            lastTxId = txId
            offset += frameTotalLen
            effectiveLength = offset.toLong()
        }

        // Apply all operations to in-memory store
        val namespaces = mutableMapOf<String, NamespaceStore>()
        for (op in allOps) {
            when (op.opType) {
                Constants.OP_PUT -> {
                    val store = namespaces.getOrPut(op.ns) { NamespaceStore() }
                    store.put(op.key, op.value, op.valType)
                }
                Constants.OP_DELETE -> {
                    namespaces[op.ns]?.delete(op.key)
                }
                Constants.OP_DROP_NAMESPACE -> {
                    namespaces.remove(op.ns)
                }
            }
        }

        return ParsedResult(namespaces, lastTxId, effectiveLength)
    }
}
