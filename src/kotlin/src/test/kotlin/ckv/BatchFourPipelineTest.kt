package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

class BatchFourPipelineTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_b4_test").canonicalFile

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
    fun `warning queue and reentrancy does not corrupt state`() {
        val dbPath = File(tempBase, "warning_queue.db").path
        val warningCount = AtomicInteger(0)
        var dbHandle: CkvDb? = null

        val opts = DbOptions(
            compactionThresholdBytes = 100,
            strictCompaction = false,
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            onWarning = { code, msg ->
                warningCount.incrementAndGet()
                // 在警告回调内读取数据库，验证无重入死锁或状态损坏
                dbHandle?.getInt64("ns", "key")
                dbHandle?.namespaces()
            }
        )

        val db = CkvDb.open(dbPath, opts)
        dbHandle = db

        // 阻断 .tmp 触发 compaction 告警
        val blocker = File("$dbPath.tmp")
        blocker.mkdirs()

        try {
            for (i in 0 until 30) {
                db.putString("ns", "k$i", "payload_to_trigger_compaction_failure")
            }
        } finally {
            blocker.deleteRecursively()
            db.close()
        }

        assertTrue(warningCount.get() > 0, "Compaction failure should trigger warning")
    }

    @Test
    fun `batch CoW deduplication per namespace`() {
        val dbPath = File(tempBase, "cow_dedup.db").path
        val db = CkvDb.open(dbPath)

        db.putString("bulk", "seed", "0")
        val snap = db.snapshot()

        val start = System.currentTimeMillis()
        db.batch { b ->
            for (i in 0 until 2000) {
                if (i % 2 == 0) {
                    b.putString("bulk", "k$i", "val")
                } else {
                    b.delete("bulk", "k${i - 1}")
                }
            }
        }
        val elapsed = System.currentTimeMillis() - start

        // 避免 O(N^2) 重复克隆，耗时应在合理范围内
        assertTrue(elapsed < 2000, "2000 batch ops should be fast without repeated cloning, elapsed: $elapsed ms")

        // 快照隔离正常
        assertEquals("0", snap.getString("bulk", "seed"))
        db.close()
    }

    @Test
    fun `rollback on flush failure avoids double increment and retains isDirty`() {
        val dbPath = File(tempBase, "rollback_test.db").path
        val db = CkvDb.open(dbPath)

        db.putInt64("bank", "balance", 100L)

        // 创建目录阻断 .tmp 文件创建与落盘
        val blocker = File("$dbPath.tmp")
        blocker.mkdirs()

        assertThrows<CkvException> {
            db.incrementInt64("bank", "balance", 50L)
        }

        // 内存必须原子回滚回 100，不得残留 150
        assertEquals(100L, db.getInt64("bank", "balance"))

        // 解除阻断后重试
        blocker.deleteRecursively()

        val newVal = db.incrementInt64("bank", "balance", 50L)
        assertEquals(150L, newVal) // 如果发生 double increment 会变成 200
        assertEquals(150L, db.getInt64("bank", "balance"))

        db.close()
    }

    @Test
    fun `flushAsync and backupAsync first-class methods`() {
        val dbPath = File(tempBase, "async_api.db").path
        val backupPath = File(tempBase, "async_backup.db").path

        val db = CkvDb.open(dbPath, DbOptions(syncMode = SyncMode.MANUAL))
        db.putString("data", "greeting", "hello async")

        // 异步刷盘
        db.flushAsync().get()

        // 异步备份
        db.backupAsync(backupPath).get()
        db.close()

        // 验证备份库能独立以只读模式打开并读取数据
        val backupDb = CkvDb.open(backupPath, DbOptions(readOnly = true))
        assertEquals("hello async", backupDb.getString("data", "greeting"))
        backupDb.close()
    }
}
