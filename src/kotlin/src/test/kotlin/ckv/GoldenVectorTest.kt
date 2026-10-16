package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.security.MessageDigest

class GoldenVectorTest {

    companion object {
        private val testdataDir = File("../../testdata").canonicalFile
        private val tempBase = File("../../temp/kotlin_golden_test").canonicalFile

        @BeforeAll
        @JvmStatic
        fun setup() {
            if (tempBase.exists()) tempBase.deleteRecursively()
            tempBase.mkdirs()
        }

        @AfterAll
        @JvmStatic
        fun teardown() {
            if (tempBase.exists()) tempBase.deleteRecursively()
        }

        private fun sha256Hex(data: ByteArray): String {
            val md = MessageDigest.getInstance("SHA-256")
            val digest = md.digest(data)
            val sb = StringBuilder()
            for (b in digest) {
                sb.append(String.format("%02x", b))
            }
            return sb.toString()
        }
    }

    @Test
    fun testGoldenEmptyV3() {
        val file = File(testdataDir, "empty_v3.db")
        assertTrue(file.exists())
        val data = file.readBytes()
        assertEquals(32, data.size)
        assertEquals("06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328", sha256Hex(data))

        // 1) Read external vector
        val copyFile = File(tempBase, "empty_copy.db")
        copyFile.writeBytes(data)
        CkvDb.open(copyFile.path).use { db ->
            assertEquals(0, db.count(""))
            assertEquals(emptyList<String>(), db.namespaces())
            assertEquals(0L, db.stats().totalEntries)
            assertEquals(0L, db.stats().currentMaxTxId)
            assertEquals(32L, db.fileSize())
            db.flush()
        }

        // 2) Regenerate locally
        val regenFile = File(tempBase, "empty_regen.db")
        CkvDb.open(regenFile.path).use { db ->
            db.flush()
        }
        assertArrayEquals(data, regenFile.readBytes())
    }

    @Test
    fun testGoldenSinglePutV3() {
        val file = File(testdataDir, "single_put_v3.db")
        assertTrue(file.exists())
        val data = file.readBytes()
        assertEquals(80, data.size)
        assertEquals("f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a", sha256Hex(data))

        // 1) Read external vector
        val copyFile = File(tempBase, "single_copy.db")
        copyFile.writeBytes(data)
        CkvDb.open(copyFile.path).use { db ->
            assertEquals("world", db.getString("", "hello"))
            assertEquals(1, db.count(""))
            assertEquals(listOf(""), db.namespaces())
        }

        // 2) Regenerate locally
        val regenFile = File(tempBase, "single_regen.db")
        CkvDb.open(regenFile.path).use { db ->
            db.putString("", "hello", "world")
        }
        assertArrayEquals(data, regenFile.readBytes())
    }

    @Test
    fun testGoldenMultiTypesV3() {
        val file = File(testdataDir, "multi_types_v3.db")
        assertTrue(file.exists())
        val data = file.readBytes()
        assertEquals(267, data.size)
        assertEquals("f13029ba36d1d9701a6f5770b4624d6386dcd7432f8245057a4ada35f033a664", sha256Hex(data))

        // 1) Read external vector
        val copyFile = File(tempBase, "multi_copy.db")
        copyFile.writeBytes(data)
        CkvDb.open(copyFile.path).use { db ->
            assertArrayEquals(byteArrayOf(0x00, 0x01, 0xFE.toByte(), 0xFF.toByte()), db.getBytes("", "bytes"))
            assertEquals("你好", db.getString("", "str"))
            assertEquals(-1234567890123456789L, db.getInt64("", "i64"))
            assertEquals(3.141592653589793, db.getFloat64("", "f64"))
            val nanVal = db.getFloat64("", "f64nan")
            assertNotNull(nanVal)
            assertTrue(nanVal!!.isNaN())
            assertTrue(db.getBool("", "bool_t")!!)
            assertFalse(db.getBool("", "bool_f")!!)
            assertTrue(db.getBool("cfg", "enabled")!!)
            assertEquals(1000L, db.getInt64("cfg", "limit"))
            assertEquals("alice", db.getString("users", "u1"))
            assertEquals(listOf("", "cfg", "users"), db.namespaces())
            assertEquals(10L, db.stats().totalEntries)
        }

        // 2) Regenerate locally
        val regenFile = File(tempBase, "multi_regen.db")
        CkvDb.open(regenFile.path).use { db ->
            db.batch { b ->
                b.putBytes("", "bytes", byteArrayOf(0x00, 0x01, 0xFE.toByte(), 0xFF.toByte()))
                b.putString("", "str", "你好")
                b.putInt64("", "i64", -1234567890123456789L)
                b.putFloat64("", "f64", 3.141592653589793)
                b.putFloat64("", "f64nan", Double.NaN)
                b.putBool("", "bool_t", true)
                b.putBool("", "bool_f", false)
                b.putBool("cfg", "enabled", true)
                b.putInt64("cfg", "limit", 1000L)
                b.putString("users", "u1", "alice")
            }
        }
        assertArrayEquals(data, regenFile.readBytes())
    }

    @Test
    fun testGoldenUnicodeOrderV3() {
        val file = File(testdataDir, "unicode_order_v3.db")
        assertTrue(file.exists())
        val data = file.readBytes()
        assertEquals(159, data.size)
        assertEquals("e3862d04be45b555bb902e271f702ecd44f67aec097008299228cc5594492e6a", sha256Hex(data))

        // 1) Read external vector
        val copyFile = File(tempBase, "uni_copy.db")
        copyFile.writeBytes(data)
        CkvDb.open(copyFile.path).use { db ->
            assertEquals(listOf("", "cf", "é", "中文"), db.namespaces())
            assertEquals(listOf("a", "z", "\uE000", "\uD83D\uDE00"), db.keys(""))
            assertEquals("5", db.getString("é", "k"))
            assertEquals("6", db.getString("中文", "k"))
            assertEquals("7", db.getString("cf", "k"))
        }

        // 2) Regenerate locally
        val regenFile = File(tempBase, "uni_regen.db")
        CkvDb.open(regenFile.path).use { db ->
            db.batch { b ->
                b.putString("", "a", "1")
                b.putString("", "z", "2")
                b.putString("", "\uE000", "3")
                b.putString("", "\uD83D\uDE00", "4")
                b.putString("é", "k", "5")
                b.putString("中文", "k", "6")
                b.putString("cf", "k", "7")
            }
        }
        assertArrayEquals(data, regenFile.readBytes())
    }
}
