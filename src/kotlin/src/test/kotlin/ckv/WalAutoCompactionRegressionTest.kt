package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * Kotlin 的自动压实路径（C8）与压实失败契约（D2）的回归测试，与 Rust / Go / .NET / Dart
 * 四套同源套件一一对应。
 *
 * C8 之前 Kotlin 既没有 `compactionThresholdBytes` 也没有自动压实：WAL 只增不减，必须
 * 人工 `compact()`。补上之后，自动压实必须遵守与其余四语言完全相同的顺序与失败契约：
 *  - **顺序（C1/C2）**：append → applyMem → 压实，且压实发生在独立锁周期内；
 *  - **失败契约（D2）**：压实失败只告警 + 置 `needsCompaction` + 退避重试，**本次写仍
 *    返回成功**（该写已 fsync 落盘）；`strictCompaction = true` 时才上抛。
 */
class WalAutoCompactionRegressionTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_auto_compaction").canonicalFile
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

    private val tinyThreshold = 512L

    private fun tempPath(name: String) = File(tempBase, "${seq++}_$name").path

    private fun walOptions(
        threshold: Long = 512L,
        strictCompaction: Boolean = false,
        onWarning: ((String, String) -> Unit)? = null
    ) = DbOptions(
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.IMMEDIATE,
        compactionThresholdBytes = threshold,
        strictCompaction = strictCompaction,
        onWarning = onWarning
    )

    /**
     * 在 `<db>.tmp` 路径放一个**非空目录**，让 `commitFlush` 写临时文件必然失败，而
     * append 照常成功——用真实文件系统条件注入"仅压实失败"，不污染生产 API。
     *
     * 目录必须非空：Windows 上删除空目录会成功，注入会被恢复流程清掉，故障不再发生
     * （Go / .NET / Dart 侧踩过同一个坑）。
     */
    private fun blockTmpPath(blocked: String) {
        val dir = File("$blocked.tmp")
        dir.mkdirs()
        File(dir, "keep").writeText("keep")
    }

    private fun unblockTmpPath(blocked: String) {
        val dir = File("$blocked.tmp")
        File(dir, "keep").delete()
        dir.delete()
    }

    /** T1（C8 + C1）：跨阈值的自动压实不得丢写，重开后全量键必须都在。 */
    @Test
    fun autoCompactionMustNotLoseWrites() {
        val dbPath = tempPath("t1.db")
        val expected = (0 until 300).map { "key%04d".format(it) to "value%04d".format(it) }

        CkvDb.open(dbPath, walOptions()).use { db ->
            for ((key, value) in expected) {
                db.putString("", key, value)
            }
        }

        // 防"空跑"：阈值从未被越过时用例会假通过。压实会在第一次成功压实后产出 `.bak`。
        assertTrue(
            File("$dbPath.bak").exists(),
            "自动压实从未运行（阈值 $tinyThreshold 未达到或压实路径是死的）——用例无意义"
        )

        CkvDb.open(dbPath, walOptions()).use { db ->
            val broken = expected.filter { (key, value) -> db.getString("", key) != value }
            assertTrue(broken.isEmpty(), "自动压实丢失了 ${broken.size}/300 个键: ${broken.take(5)}")
        }
    }

    /** T2（C2）：阈值触发后压实必须真正执行、回收空间，且不得每次写阻塞一个锁超时。 */
    @Test
    fun autoCompactionMustRunWithoutLockStalls() {
        val dbPath = tempPath("t2.db")
        val value = "x".repeat(64)
        var peakFileSize = 0L
        var worstWrite = 0L

        CkvDb.open(dbPath, walOptions()).use { db ->
            repeat(200) {
                val started = System.nanoTime()
                db.putString("", "hot", value)
                val elapsed = System.nanoTime() - started
                if (elapsed > worstWrite) worstWrite = elapsed
                val size = File(dbPath).length()
                if (size > peakFileSize) peakFileSize = size
            }
        }

        assertTrue(
            peakFileSize <= 8 * tinyThreshold,
            "文件涨到 $peakFileSize 字节：压实从未回收空间（阈值 $tinyThreshold）"
        )
        assertTrue(File("$dbPath.bak").exists(), "压实从未产出 `.bak` 轮转")
        assertFalse(File("$dbPath.tmp").exists(), "压实留下了 `.tmp`")
        assertTrue(
            worstWrite < 2_000_000_000L,
            "单次写耗时 ${worstWrite / 1_000_000}ms：压实正在阻塞写路径"
        )
    }

    /** D2：压实失败不得让本次写失败，且数据必须仍可重放。 */
    @Test
    fun compactionFailureMustNotFailTheWrite() {
        val dbPath = tempPath("d2_default.db")
        val warnings = mutableListOf<String>()
        blockTmpPath(dbPath)

        try {
            CkvDb.open(dbPath, walOptions(onWarning = { code, _ -> warnings.add(code) })).use { db ->
                val value = "y".repeat(64)
                var crossing = false
                for (i in 0 until 200) {
                    db.putString("", "k$i", value) // 任何一次都不得抛错
                    if (File(dbPath).length() >= tinyThreshold) crossing = true
                }
                assertTrue(crossing, "阈值必须被越过，否则用例无意义")
                assertTrue(
                    warnings.contains("onCompactionFailed"),
                    "压实失败必须发 onCompactionFailed 告警，收到 $warnings"
                )
            }
        } finally {
            unblockTmpPath(dbPath)
        }

        // 数据必须完整（写已 fsync 落盘）——压实失败只能影响文件是否被回收
        CkvDb.open(dbPath, walOptions(threshold = Long.MAX_VALUE - 1)).use { db ->
            assertEquals("y".repeat(64), db.getString("", "k0"))
            assertEquals("y".repeat(64), db.getString("", "k199"))
        }
    }

    /** D2：退避窗口内的写不得触发重试，跨过窗口后恰好重试一次。 */
    @Test
    fun compactionRetriesAreThrottledByTheBackoffWindow() {
        val dbPath = tempPath("d2_backoff.db")
        var failures = 0
        blockTmpPath(dbPath)

        try {
            CkvDb.open(dbPath, walOptions(onWarning = { code, _ ->
                if (code == "onCompactionFailed") failures++
            })).use { db ->
                val value = "z".repeat(64)
                var i = 0
                // 越过阈值 → 第一次压实失败
                while (failures == 0 && i < 2000) {
                    db.putString("", "k${i++}", value)
                }
                assertEquals(1, failures, "越阈值那次写必须触发一次压实尝试")

                // 退避窗口为 64 次写：窗口内不得再重试
                repeat(30) { db.putString("", "pad${i++}", value) }
                assertEquals(1, failures, "退避窗口内的写不得重试压实")

                // 跨过窗口后恰好再多一次
                repeat(40) { db.putString("", "pad${i++}", value) }
                assertTrue(failures >= 2, "跨过退避窗口后必须重试一次，实际 $failures")
                assertTrue(failures <= 3, "一次跨窗口不应产生多次重试，实际 $failures")
            }
        } finally {
            unblockTmpPath(dbPath)
        }
    }

    /** D2：`close()` 必须对未完成的压实做一次兜底重试。 */
    @Test
    fun closeRetriesPendingCompaction() {
        val dbPath = tempPath("d2_close.db")
        blockTmpPath(dbPath)

        val db = CkvDb.open(dbPath, walOptions())
        val value = "w".repeat(64)
        // FIX-21 之后压实按“垃圾量”触发：新增键只是让存活数据变大，不会触发压实。
        // 这里反复覆写同一个键来制造垃圾，才能走到被注入阻断的压实路径。
        // 200 次覆写 ≈ 13KB 垃圾，远超 tinyThreshold，必然触发一次（被阻断的）压实。
        repeat(200) { db.putString("", "hot", value) }
        assertFalse(File("$dbPath.bak").exists(), "注入生效时不得产出 `.bak`")

        // 解除注入后 close：兜底重试应成功并产出 `.bak`
        unblockTmpPath(dbPath)
        db.close()

        assertTrue(File("$dbPath.bak").exists(), "close() 必须对未完成的压实兜底重试一次")
    }

    /** D2：`strictCompaction = true` 时压实失败必须上抛，且那次写依然已持久化。 */
    @Test
    fun strictCompactionSurfacesTheFailureButTheWriteStaysDurable() {
        val dbPath = tempPath("d2_strict.db")
        blockTmpPath(dbPath)
        var thrown = false

        try {
            CkvDb.open(dbPath, walOptions(strictCompaction = true)).use { db ->
                val value = "s".repeat(64)
                var i = 0
                while (!thrown && i < 2000) {
                    try {
                        db.putString("", "k$i", value)
                    } catch (_: Exception) {
                        thrown = true
                    }
                    i++
                }
                assertTrue(thrown, "严格模式下压实失败必须上抛")
            }
        } finally {
            unblockTmpPath(dbPath)
        }

        // 报错的那次写其实已经落盘——这正是默认不上抛的原因（D2）
        CkvDb.open(dbPath, walOptions(threshold = Long.MAX_VALUE - 1)).use { db ->
            assertFalse(db.containsKey("", "missing"))
            assertTrue(db.count("") > 0, "报错的那次写也必须已持久化")
        }
    }

    /** 反向对照：阈值设为 0（关闭）时不得压实，文件按写入持续增长。 */
    @Test
    fun zeroThresholdDisablesAutoCompaction() {
        val dbPath = tempPath("disabled.db")
        CkvDb.open(dbPath, walOptions(threshold = 0L)).use { db ->
            val value = "q".repeat(64)
            repeat(200) { db.putString("", "k$it", value) }
            assertTrue(File(dbPath).length() > tinyThreshold, "关闭自动压实后文件应持续增长")
            assertFalse(File("$dbPath.bak").exists(), "关闭自动压实后不得产出 `.bak`")
        }
    }
}
