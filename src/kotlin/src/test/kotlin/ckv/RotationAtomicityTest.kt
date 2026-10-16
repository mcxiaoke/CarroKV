package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.IOException

/**
 * 快照轮转原子性的回归测试（C5 / V10）。
 *
 * 轮转把活库覆盖到 `.bak`、再把 `.tmp` 覆盖到活库。`Files.move` / `rename` 本身就能
 * 覆盖目标，因此：
 *  - **先删除目标**只会制造"`.db` 与 `.bak` 同时不存在"的窗口：轮转若在该窗口内失败，
 *    两代数据全部消失（C5，本套件第一个用例直接验证该不变量）；
 *  - **就地截断目标再写入**（生成 `.bak` 时用 `Files.copy(REPLACE_EXISTING)`）会在中途
 *    崩溃时留下半份 `.bak`，上一代完整备份同时丢失（V10）。该崩溃窗口无法从公开 API
 *    构造，因此只以"`.bak` 始终是完整可打开的一代 + 无 `.stage` 残留"作为回归保护。
 *
 * 轮转原语是 `internal`（同模块可见），故本套件可直接调用；让"活库轮转"失败在公开 API
 * 上无法确定性地构造。
 */
class RotationAtomicityTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_rotation_atomicity").canonicalFile
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

    private fun tempFile(name: String) = File(tempBase, "${seq++}_$name")

    /** C5：轮转未成功时，目标（上一代）必须原样保留。 */
    @Test
    fun failedRotationMustNotDestroyTheDestination() {
        val src = tempFile("absent.tmp") // 故意不存在：rename 必然失败
        val dst = tempFile("gen.bak")
        val previous = ByteArray(64) { it.toByte() }
        dst.writeBytes(previous)

        assertThrows(IOException::class.java) {
            FileRotation.replaceWithRetry(src, dst)
        }

        assertTrue(dst.exists(), "失败的轮转不得删除目标（C5）")
        assertArrayEquals(previous, dst.readBytes(), "上一代必须逐字节原样保留")
        assertFalse(File("${dst.path}.stage").exists(), "此处不应留下暂存副本")
    }

    /** 正常路径：覆盖目标（含大文件），且不留下暂存副本。 */
    @Test
    fun rotationReplacesAnExistingDestination() {
        val src = tempFile("gen.tmp")
        val dst = tempFile("gen.bak")
        dst.writeBytes(ByteArray(4096) { 0xAA.toByte() })
        val newGeneration = ByteArray(32) { it.toByte() }
        src.writeBytes(newGeneration)

        FileRotation.replaceWithRetry(src, dst)

        assertArrayEquals(newGeneration, dst.readBytes(), "目标必须被完整替换")
        assertFalse(src.exists(), "源文件必须被轮转消费掉")
        assertFalse(File("${dst.path}.stage").exists(), "成功路径不得留下暂存副本")
    }

    /**
     * 端到端：`.bak` 持有**完整**的上一代且可独立打开（不是半份文件），反复轮转不产生
     * `.stage` / `.tmp` 残留。
     */
    @Test
    fun rotationLeavesACompletePreviousGeneration() {
        val dbPath = tempFile("rotate.db").path
        val bakPath = "$dbPath.bak"

        CkvDb.open(dbPath).use { db -> db.putString("", "k", "v1") }
        val gen1Len = File(dbPath).length()

        CkvDb.open(dbPath).use { db -> db.putString("", "k", "v2") }

        assertTrue(File(bakPath).exists(), "轮转必须产出 `.bak`")
        assertEquals(gen1Len, File(bakPath).length(), "`.bak` 必须持有完整的上一代")

        // `.bak` 必须能被独立打开：半份文件会在打开时被判为损坏
        val copy = tempFile("gen1_copy.db").path
        File(bakPath).copyTo(File(copy), overwrite = true)
        CkvDb.open(copy).use { restored ->
            assertEquals("v1", restored.getString("", "k"))
        }

        for (i in 3..8) {
            CkvDb.open(dbPath).use { db -> db.putString("", "k", "v$i") }
            assertTrue(File(bakPath).exists(), "第 $i 次轮转后 `.bak` 必须存在")
        }
        assertFalse(File("$dbPath.bak.stage").exists(), "轮转不得留下 `.stage`")
        assertFalse(File("$dbPath.stage").exists(), "轮转不得留下 `.stage`")
    }
}
