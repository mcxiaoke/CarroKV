package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 批内"读已写"（read-your-own-writes），与 Rust / Go / .NET / Dart 四套同源套件一一对应。
 *
 * 语义：暂存区优先；暂存墓碑（delete / dropNamespace）一律视为"不存在"，即使主库仍有该键；
 * 只有本批次未触碰的键才回落到已提交状态。
 */
class BatchReadYourOwnWritesTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_ryow").canonicalFile
        private var seq = 0

        @AfterAll
        @JvmStatic
        fun teardown() {
            if (tempBase.exists()) tempBase.deleteRecursively()
        }
    }

    private fun tempPath(name: String): String {
        tempBase.mkdirs()
        return File(tempBase, "${seq++}_$name").path
    }

    @Test
    fun `staged writes win and tombstones hide`() {
        CkvDb.open(tempPath("ryow.db"), DbOptions()).use { db ->
            db.putString("", "a", "committed")
            db.putString("", "b", "committed-b")

            db.batch { b ->
                // 未触碰的键回落到已提交状态
                assertEquals("committed", b.getString("", "a"))
                assertEquals("committed-b", b.getString("", "b"))

                b.putString("", "a", "staged")
                assertEquals("staged", b.getString("", "a"))

                // 最近一次暂存操作优先
                b.putString("", "a", "staged-2")
                assertEquals("staged-2", b.getString("", "a"))

                // 暂存删除遮蔽已提交值
                b.delete("", "b")
                assertNull(b.getString("", "b"))
                assertNull(b.getBytes("", "b"))

                // 全新键在批内可见
                b.putInt64("", "n", 42L)
                assertEquals(8, b.getBytes("", "n")!!.size)

                // 类型规则在暂存区同样生效
                assertThrows(TypeMismatchError::class.java) { b.getString("", "n") }
            }

            assertEquals("staged-2", db.getString("", "a"))
            assertNull(db.getString("", "b"))
            assertEquals(42L, db.getInt64("", "n"))
        }
    }

    @Test
    fun `drop namespace hides staged and committed keys`() {
        CkvDb.open(tempPath("ryow_ns.db"), DbOptions()).use { db ->
            db.putString("users", "u1", "alice")

            db.batch { b ->
                b.putString("users", "u2", "bob")
                assertEquals("bob", b.getString("users", "u2"))

                // 其他命名空间不受影响
                b.putString("other", "k", "v")
                assertEquals("v", b.getString("other", "k"))

                b.dropNamespace("users")
                assertNull(b.getString("users", "u1"))
                assertNull(b.getString("users", "u2"))
            }

            assertEquals(0, db.count("users"))
            assertEquals("v", db.getString("other", "k"))
        }
    }

    @Test
    fun `read your own writes in wal mode`() {
        val options = DbOptions(
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            syncMode = SyncMode.IMMEDIATE
        )
        CkvDb.open(tempPath("ryow_wal.db"), options).use { db ->
            db.putString("", "counter", "1")

            db.batch { b ->
                assertEquals("1", b.getString("", "counter"))
                b.putString("", "counter", "2")
                assertEquals("2", b.getString("", "counter"))
            }

            assertEquals("2", db.getString("", "counter"))
        }
    }

    @Test
    fun `bytes view reads staged binary value`() {
        CkvDb.open(tempPath("ryow_bytes.db"), DbOptions()).use { db ->
            db.batch { b ->
                b.putBytes("", "bin", byteArrayOf(0, 1, 2, 0x7F))
                assertArrayEquals(byteArrayOf(0, 1, 2, 0x7F), b.getBytes("", "bin"))
            }
            assertArrayEquals(byteArrayOf(0, 1, 2, 0x7F), db.getBytes("", "bin"))
        }
    }
}
