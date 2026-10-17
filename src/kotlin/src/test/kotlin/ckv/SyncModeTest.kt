package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

class SyncModeTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_sync_test").canonicalFile

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
    fun testSyncModeManualFlushAndClose() {
        val dbPath = File(tempBase, "manual.db").path
        val opts = DbOptions(syncMode = SyncMode.MANUAL)

        // 1. Manual mode writes
        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "k1", "v1")
            db.putString("", "k2", "v2")

            // Immediately readable in-memory
            assertEquals("v1", db.getString("", "k1"))
            assertEquals("v2", db.getString("", "k2"))

            // Explicit flush
            db.flush()
            assertTrue(File(dbPath).exists())
        }

        // Reopen to verify persistence
        CkvDb.open(dbPath).use { db2 ->
            assertEquals("v1", db2.getString("", "k1"))
            assertEquals("v2", db2.getString("", "k2"))
        }

        // 2. Test auto-flush on close
        val dbPathAuto = File(tempBase, "manual_close.db").path
        CkvDb.open(dbPathAuto, opts).use { db3 ->
            db3.putString("", "k_auto", "v_auto")
        }

        CkvDb.open(dbPathAuto).use { db4 ->
            assertEquals("v_auto", db4.getString("", "k_auto"))
        }
    }

    @Test
    fun testSyncModeDeferredBackgroundFlush() {
        val dbPath = File(tempBase, "deferred.db").path
        val opts = DbOptions(
            syncMode = SyncMode.DEFERRED,
            syncIntervalMs = 50
        )

        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "key_async", "val_async")
            assertEquals("val_async", db.getString("", "key_async"))

            // Wait for background timer to flush (> 100ms)
            val deadline = System.currentTimeMillis() + 3000
            while (!File(dbPath).exists() && System.currentTimeMillis() < deadline) {
                Thread.sleep(20)
            }
            assertTrue(File(dbPath).exists())
        }

        CkvDb.open(dbPath).use { db2 ->
            assertEquals("val_async", db2.getString("", "key_async"))
        }
    }

    @Test
    fun testSyncModeHighFrequencyWriteCoalescing() {
        val dbPath = File(tempBase, "coalesce.db").path
        val opts = DbOptions(syncMode = SyncMode.MANUAL)

        CkvDb.open(dbPath, opts).use { db ->
            for (i in 0 until 100) {
                db.putInt64("", "counter", i.toLong())
            }
            db.flush()
        }

        CkvDb.open(dbPath).use { db2 ->
            assertEquals(99L, db2.getInt64("", "counter"))
        }
    }
}
