package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * FIX-22 回归：close() 之后继续写必须失败。
 *
 * close() 已释放跨进程连接锁，此时写入会落到一个可能已被另一个进程打开的库上，
 * 正是锁要阻止的双写者场景。五语言统一返回 Locked 语义。
 */
class ClosedHandleTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_closed_handle").canonicalFile
        private var seq = 0

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

    private fun tempPath(name: String) = File(tempBase, "${seq++}_$name").path

    @Test
    fun `writes after close are refused and the file is untouched`() {
        val path = tempPath("closed.db")
        val db = CkvDb.open(path)
        db.putString("", "before", "1")
        db.close()

        val before = File(path).length()

        val put = runCatching { db.putString("", "after", "2") }.exceptionOrNull()
        assertTrue(put is LockedError, "put after close must throw LockedError, got $put")
        assertTrue(runCatching { db.delete("", "before") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.flush() }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.compact() }.exceptionOrNull() is LockedError)

        // P1-12: 读操作在 close 之后同样必须全部失败，禁止静默读取内存旧状态
        assertTrue(runCatching { db.getString("", "before") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.getBytes("", "before") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.containsKey("", "before") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.count("") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.namespaces() }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.keys("") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.scanRange("", null, null) }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { db.snapshot() }.exceptionOrNull() is LockedError)

        assertEquals(before, File(path).length(), "a refused write must not touch the file")

        // close() is idempotent.
        db.close()

        CkvDb.open(path).use { reopened ->
            assertNull(reopened.getString("", "after"))
            assertEquals("1", reopened.getString("", "before"))
        }
    }

    // ADR-20261017: 多句柄独立性与引用计数隔离
    @Test
    fun `shared instance close from one holder does not close other holders`() {
        val path = tempPath("shared_handles.db")
        val h1 = CkvDb.open(path)
        val h2 = CkvDb.open(path)

        org.junit.jupiter.api.Assertions.assertNotSame(h1, h2, "Each open must return a distinct handle wrapper")

        h1.putString("ns", "k", "v1")
        assertEquals("v1", h2.getString("ns", "k"))

        // h1 关闭
        h1.close()

        // h1 自身所有操作全部报错
        assertTrue(runCatching { h1.getString("ns", "k") }.exceptionOrNull() is LockedError)
        assertTrue(runCatching { h1.putString("ns", "k", "v2") }.exceptionOrNull() is LockedError)

        // h2 保持有效并可正常读写
        assertEquals("v1", h2.getString("ns", "k"))
        h2.putString("ns", "k2", "v2")
        assertEquals("v2", h2.getString("ns", "k2"))

        // h2 关闭后底座才真正销毁
        h2.close()
        assertTrue(runCatching { h2.getString("ns", "k2") }.exceptionOrNull() is LockedError)
    }

    // §7.4 静态 backupFile 测试
    @Test
    fun `static backupFile creates standalone database`() {
        val src = tempPath("src.db")
        val dst = tempPath("dst.db")

        CkvDb.open(src).use { db ->
            db.putString("ns", "hello", "world")
        }

        CkvDb.backupFile(src, dst)

        CkvDb.open(dst).use { reopened ->
            assertEquals("world", reopened.getString("ns", "hello"))
        }
    }
}
