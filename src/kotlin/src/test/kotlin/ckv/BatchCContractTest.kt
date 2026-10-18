package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 批次 C 回归（Kotlin）：
 *  - M1-01 非零 HeaderFlags 拒绝打开（规范 §3.1）
 *  - D2 Snapshot 释放后读必须温和返回空（API-SPEC §3.5.3）
 *  - D3 空键合法（与其余四语言对齐）
 *  - M5-01 value 超限必须落到 KeyTooLongError 体系（规范 §4）
 */
class BatchCContractTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_batch_c").canonicalFile

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
    }

    @Test
    fun nonZeroHeaderFlagsAreRejected() {
        val dbPath = File(tempBase, "flags.db").path
        CkvDb.open(dbPath).use { db -> db.putString("", "k", "v") }

        val bytes = File(dbPath).readBytes()
        bytes[12] = 1
        // 重算头部 CRC（模拟写入方有意设置未知必选位）；不重算会被判成 Corrupted。
        BinaryHelper.writeIntLE(bytes, 28, Crc32.compute(bytes, 0, 28))
        File(dbPath).writeBytes(bytes)

        val ex = assertThrows(UnsupportedVersionError::class.java) { CkvDb.open(dbPath) }
        assertTrue(ex.message!!.contains("flags"), "unexpected message: ${ex.message}")
    }

    @Test
    fun snapshotAfterCloseReadsEmpty() {
        val dbPath = File(tempBase, "snap.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("", "k", "v")
            val snap = db.snapshot()
            assertEquals("v", snap.getString("", "k"))
            snap.close()

            assertNull(snap.getString("", "k"))
            assertNull(snap.getBytes("", "k"))
            assertFalse(snap.containsKey("", "k"))
            assertEquals(0, snap.count(""))
            assertTrue(snap.keys("").isEmpty())
            assertTrue(snap.iterPrefix("").isEmpty())
            assertTrue(snap.scanRange("", null, null).isEmpty())
        }
    }

    @Test
    fun emptyKeyIsAccepted() {
        val dbPath = File(tempBase, "empty_key.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("", "", "empty-key-value")
            assertEquals("empty-key-value", db.getString("", ""))
            assertTrue(db.containsKey("", ""))
        }
        CkvDb.open(dbPath).use { db ->
            assertEquals("empty-key-value", db.getString("", ""))
        }
    }

    @Test
    fun oversizedValueLandsInKeyTooLongHierarchy() {
        val dbPath = File(tempBase, "too_long.db").path
        CkvDb.open(dbPath).use { db ->
            val huge = "x".repeat(16 * 1024 * 1024 + 1)
            // KeyTooLargeError 是 KeyTooLongError 的 typealias；旧实现抛 ValueTooLongError
            // 逃出了这套体系，规范 §4 要求两者同属一个错误。
            assertThrows(KeyTooLongError::class.java) { db.putString("", "k", huge) }
        }
    }
}
