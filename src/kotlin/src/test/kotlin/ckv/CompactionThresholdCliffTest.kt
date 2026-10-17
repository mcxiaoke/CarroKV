package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * FIX-21 回归：自动压实必须按“垃圾量”触发，而不是按文件绝对大小触发。
 *
 * 旧条件 `newFileSize >= compactionThresholdBytes` 在压实后（文件恰等于存活大小）对
 * 每一次写都成立：6MB 存活实测 26ms/写，而正常摊销路径约 0.2ms。
 * 断言基于文件大小增量，不用时间阈值（CI runner 抖动会让时间断言不稳定）。
 * 与 Rust / Go / .NET / Dart 五套同源套件一一对应。
 */
class CompactionThresholdCliffTest {

    companion object {
        private const val THRESHOLD = 4L * 1024 * 1024
        private val tempBase = File("../../temp/kotlin_threshold_cliff").canonicalFile
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

    private fun wal(threshold: Long) = DbOptions(
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.DEFERRED,
        syncIntervalMs = 3_600_000,
        compactionThresholdBytes = threshold
    )

    /** 关闭压实装载 6000×1KB（约 6MB 存活），保证重开时日志本身没有垃圾。 */
    private fun loadFixture(path: String) {
        val db = CkvDb.open(path, wal(0))
        try {
            val value = "v".repeat(1000)
            for (i in 0 until 6000) {
                db.putString("ns", "k%06d".format(i), value)
            }
            db.flush()
        } finally {
            db.close()
        }
    }

    private fun overwrite(db: CkvDb, count: Int) {
        val value = "v".repeat(1000)
        for (i in 0 until count) {
            db.putString("ns", "k%06d".format(i), value)
        }
    }

    @Test
    fun `overwrites below the threshold do not rewrite the whole database`() {
        val path = tempPath("cliff.db")
        loadFixture(path)

        val db = CkvDb.open(path, wal(THRESHOLD))
        try {
            val before = File(path).length()
            assertTrue(before >= 5_500_000, "fixture unexpectedly small: $before bytes")

            // 50 次覆写 = 约 50KB 纯垃圾，比阈值低 80 倍：日志应当单纯增长。
            overwrite(db, 50)

            val after = File(path).length()
            assertTrue(
                after >= before + 40_000,
                "file did not grow ($before -> $after): every write is still triggering " +
                    "a full-database rewrite"
            )
        } finally {
            db.close()
        }
    }

    @Test
    fun `garbage past the threshold still compacts`() {
        val path = tempPath("reclaim.db")
        loadFixture(path)

        val db = CkvDb.open(path, wal(THRESHOLD))
        try {
            val before = File(path).length()

            // 约 4.5MB 纯垃圾：必须被回收一次，文件回落到接近存活大小。
            overwrite(db, 4500)

            val after = File(path).length()
            assertTrue(
                after < before + 2_000_000,
                "compaction did not reclaim $before -> $after bytes of garbage"
            )
        } finally {
            db.close()
        }
    }
}
