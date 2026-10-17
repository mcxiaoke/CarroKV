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

        assertEquals(before, File(path).length(), "a refused write must not touch the file")

        // close() is idempotent.
        db.close()

        CkvDb.open(path).use { reopened ->
            assertNull(reopened.getString("", "after"))
            assertEquals("1", reopened.getString("", "before"))
        }
    }
}
