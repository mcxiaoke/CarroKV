package ckv

object Crc32 {
    private val TABLE = IntArray(256) { i ->
        var c = i
        for (j in 0 until 8) {
            c = if ((c and 1) != 0) {
                (c ushr 1) xor 0xEDB88320.toInt()
            } else {
                c ushr 1
            }
        }
        c
    }

    fun compute(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset): Int {
        var crc = 0xFFFFFFFF.toInt()
        val end = offset + length
        for (i in offset until end) {
            val b = bytes[i].toInt() and 0xFF
            crc = (crc ushr 8) xor TABLE[(crc xor b) and 0xFF]
        }
        return crc xor 0xFFFFFFFF.toInt()
    }
}
