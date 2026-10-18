package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 批次 B「数据正确性」回归测试（Kotlin）。
 *
 * 覆盖：
 *  - N-X3 `SyncMode.MANUAL` 必须与 Deferred 同语义（内存只写，只在 `flush()` 时落盘）。
 *    旧实现用 `== DEFERRED` 分流，MANUAL 落进两阶段提交分支，每次 put 都全量序列化
 *    + fsync，Manual 契约直接失效。
 *  - N-X2 AOL + Deferred 后台刷盘的代际守卫：连续写后 flush，重开必须一条不丢。
 *
 * N-X4（Phase2 序列化实时 map）是防御性修复：当前锁序下 DEFERRED 写与 IMMEDIATE
 * Phase2 无法并存（同一实例只有一个 syncMode），因此没有可观测后果可断言。
 */
class BatchBDurabilityTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_batch_b").canonicalFile

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

    @Test
    fun manualModeNeverWritesUntilFlush() {
        val dbPath = File(tempBase, "manual_disk.db").path
        val opts = DbOptions(syncMode = SyncMode.MANUAL)

        CkvDb.open(dbPath, opts).use { db ->
            val dbFile = File(dbPath)
            // 关键断言：每次写都不得落盘。旧实现把 MANUAL 折进两阶段提交分支，
            // 每写一次文件就会变长，这里第一写与第五十写的长度必然不同。
            db.putString("", "k0", "v0")
            val afterFirstWrite = if (dbFile.exists()) dbFile.length() else -1L

            repeat(49) { db.putString("", "k${it + 1}", "v${it + 1}") }
            val afterFiftyWrites = if (dbFile.exists()) dbFile.length() else -1L

            assertEquals(
                afterFirstWrite,
                afterFiftyWrites,
                "MANUAL must not write to disk on put(); only flush() may"
            )

            // 也不应有后台刷盘：跨过多个后台周期后文件长度仍不变。
            Thread.sleep(300)
            val afterIdle = if (dbFile.exists()) dbFile.length() else -1L
            assertEquals(afterFiftyWrites, afterIdle, "MANUAL must not start a background flush")

            db.flush()
            assertTrue(dbFile.exists() && dbFile.length() > 0)
        }

        CkvDb.open(dbPath).use { db ->
            repeat(50) { assertEquals("v$it", db.getString("", "k$it")) }
        }
    }

    @Test
    fun aolDeferredKeepsEveryWriteAcrossFlushAndReopen() {
        val dbPath = File(tempBase, "aol_deferred.db").path
        val opts = DbOptions(
            syncMode = SyncMode.DEFERRED,
            syncIntervalMs = 10,
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            compactionThresholdBytes = Long.MAX_VALUE
        )

        CkvDb.open(dbPath, opts).use { db ->
            repeat(200) { db.putString("", "k$it", "v$it") }
            Thread.sleep(100)
            db.flush()
        }

        CkvDb.open(dbPath).use { db ->
            repeat(200) { assertEquals("v$it", db.getString("", "k$it"), "key k$it lost") }
        }
    }

    @Test
    fun aolDeferredWithLargeValuesSurvivesReopen() {
        val dbPath = File(tempBase, "aol_large.db").path
        val opts = DbOptions(
            syncMode = SyncMode.DEFERRED,
            syncIntervalMs = 5,
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            compactionThresholdBytes = Long.MAX_VALUE
        )
        val big = "x".repeat(256 * 1024)

        CkvDb.open(dbPath, opts).use { db ->
            repeat(20) { db.putString("", "big$it", big) }
            db.flush()
        }

        CkvDb.open(dbPath).use { db ->
            repeat(20) { assertEquals(big.length, db.getString("", "big$it")!!.length) }
        }
    }
}
