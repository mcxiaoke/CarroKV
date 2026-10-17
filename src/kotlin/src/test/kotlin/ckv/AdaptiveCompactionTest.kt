package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 自适应压实（死键比例触发），与 Rust / Go / .NET / Dart 四套同源套件一一对应。
 *
 * 三条被钉住的性质：
 *  1. **死键比例触发**：未达 `compactionThresholdBytes` 的日志，只要死数据过半就被回收，
 *     且回收不丢任何存活键；
 *  2. **基线**：低于 2 MB 的日志即使九成是死数据也不重写——重写的代价大于收益；
 *  3. **总开关**：`compactionThresholdBytes = 0` 仍然关闭**全部**自动压实路径，
 *     包括死键比例触发。
 */
class AdaptiveCompactionTest {

    companion object {
        private const val BASELINE = 2L * 1024 * 1024
        private val tempBase = File("../../temp/kotlin_adaptive_compaction").canonicalFile
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

    private fun adaptiveOptions(threshold: Long) = DbOptions(
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.DEFERRED,
        syncIntervalMs = 3_600_000, // 让后台刷盘完全不参与
        compactionThresholdBytes = threshold
    )

    private fun fill(db: CkvDb, ns: String, count: Int, valueLen: Int) {
        val value = "v".repeat(valueLen)
        for (i in 0 until count) {
            db.putString(ns, "k%05d".format(i), value)
        }
    }

    @Test
    fun `dead ratio compacts below the size threshold`() {
        val path = tempPath("adaptive.db")
        val db = CkvDb.open(path, adaptiveOptions(Long.MAX_VALUE / 2))
        try {
            fill(db, "ns", 300, 10 * 1024)
            val grown = File(path).length()
            assertTrue(grown > BASELINE, "日志应超过自适应基线，实际 $grown 字节")

            // 触发点在死键比例越过 50% 时立刻生效，因此会在删除循环中途完成重写。
            for (i in 0 until 250) {
                assertTrue(db.delete("ns", "k%05d".format(i)))
            }

            val after = File(path).length()
            assertTrue(
                after < BASELINE,
                "死键比例压实应已回收日志：$grown -> $after 字节"
            )
        } finally {
            db.close()
        }

        val reopened = CkvDb.open(path, adaptiveOptions(Long.MAX_VALUE / 2))
        try {
            assertEquals(50, reopened.count("ns"))
            for (i in 250 until 300) {
                val key = "k%05d".format(i)
                assertEquals(10 * 1024, reopened.getString("ns", key)?.length, "压实丢了键 $key")
            }
            for (i in 0 until 250) {
                assertNull(reopened.getString("ns", "k%05d".format(i)))
            }
        } finally {
            reopened.close()
        }
    }

    @Test
    fun `small logs are not rewritten for being mostly dead`() {
        val path = tempPath("small.db")
        val db = CkvDb.open(path, adaptiveOptions(Long.MAX_VALUE / 2))
        try {
            fill(db, "ns", 50, 1024)
            val grown = File(path).length()
            assertTrue(grown < BASELINE, "日志应低于基线，实际 $grown 字节")

            for (i in 0 until 45) {
                db.delete("ns", "k%05d".format(i))
            }

            val after = File(path).length()
            assertTrue(after >= grown, "低于基线的日志不应被重写：$grown -> $after 字节")
            assertTrue(after < BASELINE, "低于基线的日志不应被重写：$grown -> $after 字节")
            assertEquals(5, db.count("ns"))
        } finally {
            db.close()
        }
    }

    @Test
    fun `zero threshold disables the dead ratio trigger`() {
        val path = tempPath("disabled.db")
        val db = CkvDb.open(path, adaptiveOptions(0))
        try {
            fill(db, "ns", 300, 10 * 1024)
            val grown = File(path).length()

            for (i in 0 until 270) {
                db.delete("ns", "k%05d".format(i))
            }

            val after = File(path).length()
            assertTrue(
                after > grown && after > BASELINE,
                "threshold = 0 必须关闭全部自动压实：$grown -> $after 字节"
            )
            assertEquals(30, db.count("ns"))
        } finally {
            db.close()
        }
    }
}
