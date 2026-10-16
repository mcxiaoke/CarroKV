package ckv

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class Crc32Test {
    @Test
    fun testStandardVector() {
        val bytes = "123456789".toByteArray(Charsets.US_ASCII)
        val crc = Crc32.compute(bytes)
        // 0xCBF43926 in signed 32-bit int is -873187034
        assertEquals(0xCBF43926.toInt(), crc)
    }
}
