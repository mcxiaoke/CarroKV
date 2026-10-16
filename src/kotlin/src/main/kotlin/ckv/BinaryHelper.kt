package ckv

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction

object BinaryHelper {
    private const val CANONICAL_QUIET_NAN: Long = 0x7FF8000000000000L

    fun readShortLE(buf: ByteArray, offset: Int): Short {
        return ((buf[offset].toInt() and 0xFF) or
                ((buf[offset + 1].toInt() and 0xFF) shl 8)).toShort()
    }

    fun writeShortLE(buf: ByteArray, offset: Int, value: Short) {
        val v = value.toInt()
        buf[offset] = (v and 0xFF).toByte()
        buf[offset + 1] = ((v ushr 8) and 0xFF).toByte()
    }

    fun readIntLE(buf: ByteArray, offset: Int): Int {
        return (buf[offset].toInt() and 0xFF) or
                ((buf[offset + 1].toInt() and 0xFF) shl 8) or
                ((buf[offset + 2].toInt() and 0xFF) shl 16) or
                ((buf[offset + 3].toInt() and 0xFF) shl 24)
    }

    fun writeIntLE(buf: ByteArray, offset: Int, value: Int) {
        buf[offset] = (value and 0xFF).toByte()
        buf[offset + 1] = ((value ushr 8) and 0xFF).toByte()
        buf[offset + 2] = ((value ushr 16) and 0xFF).toByte()
        buf[offset + 3] = ((value ushr 24) and 0xFF).toByte()
    }

    fun readLongLE(buf: ByteArray, offset: Int): Long {
        var res = 0L
        for (i in 0 until 8) {
            res = res or ((buf[offset + i].toLong() and 0xFFL) shl (i * 8))
        }
        return res
    }

    fun writeLongLE(buf: ByteArray, offset: Int, value: Long) {
        for (i in 0 until 8) {
            buf[offset + i] = ((value ushr (i * 8)) and 0xFFL).toByte()
        }
    }

    fun encodeInt64(value: Long): ByteArray {
        val buf = ByteArray(8)
        writeLongLE(buf, 0, value)
        return buf
    }

    fun decodeInt64(bytes: ByteArray): Long {
        if (bytes.size != 8) {
            throw TypeMismatchError("Expected 8 bytes for int64, got ${bytes.size}")
        }
        return readLongLE(bytes, 0)
    }

    fun encodeFloat64(value: Double): ByteArray {
        val bits = if (value.isNaN()) {
            CANONICAL_QUIET_NAN
        } else {
            java.lang.Double.doubleToRawLongBits(value)
        }
        val buf = ByteArray(8)
        writeLongLE(buf, 0, bits)
        return buf
    }

    fun decodeFloat64(bytes: ByteArray): Double {
        if (bytes.size != 8) {
            throw TypeMismatchError("Expected 8 bytes for float64, got ${bytes.size}")
        }
        val bits = readLongLE(bytes, 0)
        return java.lang.Double.longBitsToDouble(bits)
    }

    fun encodeBool(value: Boolean): ByteArray {
        return byteArrayOf(if (value) 0x01 else 0x00)
    }

    fun decodeBool(bytes: ByteArray): Boolean {
        if (bytes.size != 1) {
            throw TypeMismatchError("Expected 1 byte for bool, got ${bytes.size}")
        }
        return when (val b = bytes[0].toInt() and 0xFF) {
            0 -> false
            1 -> true
            else -> throw TypeMismatchError("Invalid boolean byte value 0x${Integer.toHexString(b)}")
        }
    }

    fun normalizeNamespace(ns: String?): String {
        return if (ns == null || ns == Constants.DEFAULT_NAMESPACE_ALIAS) "" else ns
    }

    fun compareBytes(a: ByteArray, b: ByteArray): Int {
        val minLen = minOf(a.size, b.size)
        for (i in 0 until minLen) {
            val byteA = a[i].toInt() and 0xFF
            val byteB = b[i].toInt() and 0xFF
            if (byteA != byteB) {
                return byteA - byteB
            }
        }
        return a.size - b.size
    }

    fun startsWith(target: ByteArray, prefix: ByteArray): Boolean {
        if (target.size < prefix.size) return false
        for (i in prefix.indices) {
            if (target[i] != prefix[i]) return false
        }
        return true
    }

    fun isValidUtf8(bytes: ByteArray): Boolean {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        return try {
            decoder.decode(ByteBuffer.wrap(bytes))
            true
        } catch (_: CharacterCodingException) {
            false
        }
    }
}
