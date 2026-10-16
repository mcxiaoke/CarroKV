package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.RandomAccessFile

/**
 * open 路径剩余两处静默失效模式的回归测试，与 Rust / Go / .NET / Dart 四套同源套件
 * 一一对应。
 *
 * 覆盖缺陷：
 *  - **C10**：open 期的残尾自愈原先不持跨进程文件锁，第二个进程可能截掉另一个进程
 *    刚追加的帧。修复后修复动作在 `LockFile` 内执行；本套件通过"持锁时打开必须
 *    **失败**（`LockedError`）且文件字节不变"来证明这一点。
 *  - **D1-7 / D1-8**：主库缺失 + 存在 `.bak` 原先会被静默晋升（= 静默回退到旧一代），
 *    而 `.bak` 本身损坏时更会静默按**空库**打开。两者现在都默认拒绝，除非调用方
 *    显式开启 `.bak` 恢复（第 5 处静默数据丢失）。
 */
class OpenRepairLockTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_open_repair_lock").canonicalFile
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

    private val fileHeaderLen = 32L

    /** 少于 28 字节帧头：文件在最后一帧中途结束。 */
    private val tornTail = byteArrayOf(
        0x4B, 0x56, 0x46, 0x01, 0xAA.toByte(), 0xBB.toByte(),
        0xCC.toByte(), 0xDD.toByte(), 0x11, 0x22
    )

    private fun tempPath(name: String) = File(tempBase, "${seq++}_$name").path

    private fun walOptions(lockTimeoutMs: Long = 10000) = DbOptions(
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.IMMEDIATE,
        lockTimeoutMs = lockTimeoutMs
    )

    private fun appendTornTail(path: String) {
        RandomAccessFile(path, "rw").use { raf ->
            raf.seek(raf.length())
            raf.write(tornTail)
            raf.fd.sync()
        }
    }

    private fun flipByteAt(path: String, offset: Long) {
        RandomAccessFile(path, "rw").use { raf ->
            raf.seek(offset)
            val original = raf.read()
            assertTrue(original >= 0, "offset $offset 必须落在文件内")
            raf.seek(offset)
            raf.write(original xor 0xFF)
            raf.fd.sync()
        }
    }

    /** 写两代（Snapshot 模式），使 `.bak` 持有第一代。 */
    private fun writeTwoGenerations(dbPath: String) {
        for (value in listOf("v1", "v2")) {
            CkvDb.open(dbPath).use { db -> db.putString("", "version", value) }
        }
        assertTrue(File("$dbPath.bak").exists(), "本用例需要存在上一代 `.bak`")
    }

    /**
     * C10 / T11：残尾自愈必须持有跨进程文件锁。
     *
     * 持锁者存活时打开必须失败为 `LockedError`，且日志**不得**被截断；否则并发追加的
     * 帧会被这次自愈悄悄丢掉。
     */
    @Test
    fun tailRepairRequiresTheFileLock() {
        val dbPath = tempPath("t11.db")
        val opts = walOptions()

        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "a", "1")
            db.putString("", "b", "2")
            db.putString("", "c", "3")
        }

        val healthyLen = File(dbPath).length()
        appendTornTail(dbPath)
        val damagedLen = File(dbPath).length()
        assertEquals(healthyLen + tornTail.size, damagedLen)

        // 模拟"另一个进程持锁"：Kotlin 的锁是纯内核锁（无时间戳 / 无窃取启发式），
        // 因此持锁者必须真的持有文件锁，而不是仅保持句柄打开。
        val lockPath = "$dbPath.lock"
        val holder = RandomAccessFile(lockPath, "rw")
        val holderLock = holder.channel.tryLock()
        assertTrue(holderLock != null, "测试自身必须能先取得锁")

        try {
            val ex = assertThrows(LockedError::class.java) {
                CkvDb.open(dbPath, walOptions(lockTimeoutMs = 100))
            }
            assertTrue(ex.message!!.contains("lock"), "错误信息应说明锁超时：${ex.message}")
            assertEquals(damagedLen, File(dbPath).length(), "被拒开后日志不得被截断")
        } finally {
            holderLock!!.release()
            holder.close()
        }

        File(lockPath).delete()

        CkvDb.open(dbPath, opts).use { db ->
            assertEquals("1", db.getString("", "a"))
            assertEquals("2", db.getString("", "b"))
            assertEquals("3", db.getString("", "c"))
        }
        assertEquals(healthyLen, File(dbPath).length(), "自愈后应止于最后一帧边界")
    }

    /** D1-7：主库缺失 + `.bak` 完好默认拒绝，且两侧文件都不得被改动。 */
    @Test
    fun missingPrimaryWithHealthyBackupIsRefusedByDefault() {
        val dbPath = tempPath("d17.db")
        writeTwoGenerations(dbPath)
        val bakPath = "$dbPath.bak"
        val bakBefore = File(bakPath).readBytes()

        File(dbPath).delete()

        assertThrows(CorruptedError::class.java) { CkvDb.open(dbPath) }
        assertFalse(File(dbPath).exists(), "被拒打开不得创建数据库")
        assertTrue(bakBefore.contentEquals(File(bakPath).readBytes()), "离线副本不得被改写")
    }

    /** 显式开启后恢复原有晋升行为，并沿用 onRecoveredFromBackup 告警码。 */
    @Test
    fun missingPrimaryWithHealthyBackupIsPromotedWhenExplicitlyEnabled() {
        val dbPath = tempPath("d17_opt_in.db")
        writeTwoGenerations(dbPath)

        File(dbPath).delete()

        val warnings = mutableListOf<String>()
        CkvDb.open(dbPath, DbOptions(autoRecoverFromBackup = true, onWarning = { code, _ ->
            warnings.add(code)
        })).use { db ->
            assertTrue(File(dbPath).exists(), "`.bak` 应被晋升为主库")
            assertEquals("v1", db.getString("", "version"))
        }
        assertTrue(warnings.contains("onRecoveredFromBackup"), "晋升必须继续上报告警码")
    }

    /** D1-8：主库缺失且 `.bak` 损坏时不得静默开空库。 */
    @Test
    fun missingPrimaryWithDamagedBackupIsNotOpenedAsAnEmptyDatabase() {
        val dbPath = tempPath("d18.db")
        writeTwoGenerations(dbPath)
        val bakPath = "$dbPath.bak"

        flipByteAt(bakPath, fileHeaderLen + 12)
        File(dbPath).delete()

        val ex = assertThrows(CorruptedError::class.java) { CkvDb.open(dbPath) }
        assertTrue(
            ex.message!!.contains("could not be loaded"),
            "错误信息应说明离线副本不可用：${ex.message}"
        )
        assertFalse(File(dbPath).exists(), "被拒打开不得留下一个全新的空库")
    }

    /** 反向对照：没有任何 `.bak` 时，同一路径是全新库，必须照常打开。 */
    @Test
    fun missingPrimaryWithoutBackupStillOpensAsANewDatabase() {
        val dbPath = tempPath("fresh.db")
        assertFalse(File("$dbPath.bak").exists())

        CkvDb.open(dbPath).use { db ->
            db.putString("", "k", "v")
            assertEquals("v", db.getString("", "k"))
        }
    }
}
