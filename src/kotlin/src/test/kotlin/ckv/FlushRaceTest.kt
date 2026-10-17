package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * FIX-01 回归：后台定时刷盘与显式 flush 必须互斥，且后台快照陈旧时不得轮转。
 *
 * 两个缺陷都以同一可观测后果出现：已显式落盘的写入在 close() + 重开之后消失。
 * 大值用于把后台序列化窗口拉长到几十毫秒；删掉大值使显式 flush 写出极小的快照，
 * 于是“陈旧快照后轮转”的时序是确定的，而不靠运气。
 * 与 Rust / Go / .NET 套件一一对应（Dart 单 isolate、flush 同步，不存在交错）。
 */
class FlushRaceTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_flush_race").canonicalFile
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

    private fun deferred(intervalMs: Long) = DbOptions(
        syncMode = SyncMode.DEFERRED,
        syncIntervalMs = intervalMs
    )

    @Test
    fun `explicit flush during background flush keeps both generations`() {
        val big = "x".repeat(15 * 1024 * 1024)
        repeat(5) { round ->
            val path = tempPath("race$round.db")
            CkvDb.open(path, deferred(1)).use { db ->
                db.putString("", "big", big)
                // 让后台克隆快照并开始序列化。
                Thread.sleep(5)
                // 大值删除后显式 flush 的快照极小，会先于后台的 15MB 写完成。
                db.delete("", "big")
                db.putString("", "w", "after")
                db.flush()
                // 给陈旧的后台快照充足时间完成轮转。
                Thread.sleep(150)
            }

            CkvDb.open(path).use { reopened ->
                assertEquals(
                    "after",
                    reopened.getString("", "w"),
                    "round $round: an explicitly flushed write was overwritten by a stale snapshot"
                )
                assertEquals(
                    null,
                    reopened.getString("", "big"),
                    "round $round: a deleted value came back from a stale snapshot"
                )
            }
        }
    }

    @Test
    fun `rapid writes with background flush are not silently dropped`() {
        repeat(3) { round ->
            val path = tempPath("rapid$round.db")
            CkvDb.open(path, deferred(1)).use { db ->
                for (i in 0 until 200) {
                    db.putString("ns", "k%04d".format(i), "value-$i")
                }
            }

            CkvDb.open(path).use { reopened ->
                assertEquals(200, reopened.count("ns"), "round $round: lost writes after close()")
                assertEquals("value-87", reopened.getString("ns", "k0087"))
                assertEquals("value-199", reopened.getString("ns", "k0199"))
            }
        }
    }
}
