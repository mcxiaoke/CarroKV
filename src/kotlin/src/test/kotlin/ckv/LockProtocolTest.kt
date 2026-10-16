package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.RandomAccessFile

/**
 * 连接锁的回归测试（C9，门槛 T10）。
 *
 * 自 C9 起锁是 `<db>.lock` 上的**内核锁**（`FileChannel.tryLock`），由 open 获取、持有整个
 * 连接生命周期：
 *
 *  - 锁文件的*内容*不再参与协议（旧的 32B 记录、时钟判活、整套"窃取陈旧锁"已移除），
 *    因此遗留或畸形锁文件必须零代价；
 *  - 活着的持有者永远不会被误判为死的，崩溃的持有者靠进程退出由内核释放；
 *  - 锁文件只创建不删除。
 *
 * 可写连接取排他锁、只读连接取共享锁：读者之间互不排斥，只排斥写者。
 *
 * T10 跨进程的一半（另一个进程持锁后被杀掉）由 Rust 套件覆盖；这里通过锁原语本身 +
 * `open` 的拒绝路径验证同一组性质。
 */
class LockProtocolTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_lock_protocol").canonicalFile
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

    private fun options(lockTimeoutMs: Long = 150, readOnly: Boolean = false) =
        DbOptions(lockTimeoutMs = lockTimeoutMs, readOnly = readOnly)

    /** 真实的内核锁持有者（"另一个连接"）。 */
    private class Holder(dbPath: String, shared: Boolean) : AutoCloseable {
        private val raf = RandomAccessFile("$dbPath.lock", "rw")
        private val lock = raf.channel.tryLock(0L, Long.MAX_VALUE, shared)

        init {
            assertTrue(lock != null, "测试自身必须能先取得锁")
        }

        override fun close() {
            lock!!.release()
            raf.close()
        }
    }

    @Test
    fun lockFileContentsAreIrrelevant() {
        val dbPath = tempPath("garbage_lock.db")
        val lockPath = "$dbPath.lock"
        val garbage = ByteArray(40) { ((it * 7) and 0xFF).toByte() }
        File(lockPath).writeBytes(garbage)

        val started = System.currentTimeMillis()
        CkvDb.open(dbPath).use { db -> db.putString("", "k", "v") }
        val elapsed = System.currentTimeMillis() - started

        assertTrue(elapsed < 2000, "遗留锁文件不应造成任何等待，实际 ${elapsed}ms")
        assertTrue(File(lockPath).exists(), "锁文件不得被删除")
        assertTrue(garbage.contentEquals(File(lockPath).readBytes()), "锁文件不得被改写")
    }

    @Test
    fun exclusiveLockExcludesWritersAndReaders() {
        val dbPath = tempPath("exclusive.db")
        Holder(dbPath, shared = false).use {
            assertThrows(LockedError::class.java) { CkvDb.open(dbPath) }
            assertThrows(LockedError::class.java) {
                CkvDb.open(dbPath, options(readOnly = true))
            }
        }

        CkvDb.open(dbPath).use { db ->
            db.putString("", "k", "v")
            assertEquals("v", db.getString("", "k"))
        }
    }

    /**
     * 只读连接持有的共享锁同样排斥写者。
     *
     * 注意：**同一 JVM 内无法验证"两个读者共存"**——JVM 的文件锁表是进程级的，同一区间
     * 的第二次 `tryLock` 无论共享还是排他都会抛 `OverlappingFileLockException`。跨进程
     * 的共享/排他语义由 Rust 套件的 T10 覆盖，这里只断言安全侧。
     */
    @Test
    fun sharedLockAlsoExcludesWriters() {
        val dbPath = tempPath("shared.db")
        CkvDb.open(dbPath).use { db -> db.putString("", "k", "v") }

        Holder(dbPath, shared = true).use {
            assertThrows(LockedError::class.java) { CkvDb.open(dbPath) }
        }

        // 持锁者离开后，读者必须能正常打开并读到数据
        CkvDb.open(dbPath, options(readOnly = true)).use { reader ->
            assertEquals("v", reader.getString("", "k"))
        }
    }

    @Test
    fun connectionHoldsTheLockUntilClose() {
        val dbPath = tempPath("lifetime.db")

        CkvDb.open(dbPath).use { db ->
            db.putString("", "k", "v")

            val probe = RandomAccessFile("$dbPath.lock", "rw")
            val probeLock = try {
                probe.channel.tryLock()
            } catch (_: Exception) {
                null
            }
            assertTrue(probeLock == null, "连接必须持有锁直至关闭")
            probe.close()
        }

        // close 之后锁被释放（锁文件保留，不需要任何删除动作）
        val after = RandomAccessFile("$dbPath.lock", "rw")
        val lock = after.channel.tryLock()
        assertTrue(lock != null, "close 必须释放连接锁")
        lock!!.release()
        after.close()
    }
}