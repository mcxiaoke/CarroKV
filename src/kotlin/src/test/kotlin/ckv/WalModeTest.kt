package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.atomic.AtomicInteger

class WalModeTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_wal_test").canonicalFile

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
    fun testWalAppendAndPersistence() {
        val dbPath = File(tempBase, "wal_append.db").path
        val opts = DbOptions(
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            syncMode = SyncMode.IMMEDIATE
        )

        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "k1", "v1")
            db.putString("", "k2", "v2")
            db.putString("", "k1", "v1_updated")
            db.delete("", "k2")
        }

        CkvDb.open(dbPath, opts).use { db2 ->
            assertEquals("v1_updated", db2.getString("", "k1"))
            assertNull(db2.getString("", "k2"))
            assertEquals(1, db2.count(""))
        }
    }

    @Test
    fun testWalTailTruncationRecovery() {
        val dbPath = File(tempBase, "wal_trunc.db").path
        val warningCount = AtomicInteger(0)

        val opts = DbOptions(
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            syncMode = SyncMode.IMMEDIATE,
            onWarning = { code, _ ->
                if (code == "onTailTruncated") {
                    warningCount.incrementAndGet()
                }
            }
        )

        // Step 1: Write healthy entries
        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "k1", "v1")
            db.putString("", "k2", "v2")
        }

        val healthyLen = File(dbPath).length()
        assertTrue(healthyLen > 32)

        // Step 2: Inject corrupt tail bytes (torn frame simulation)
        FileOutputStream(File(dbPath), true).use { fos ->
            fos.write("CORRUPT_TAIL_BYTES!!".toByteArray(Charsets.UTF_8))
        }

        val corruptedLen = File(dbPath).length()
        assertEquals(healthyLen + 20, corruptedLen)

        // Step 3: Reopen database - should auto-truncate corrupted tail
        CkvDb.open(dbPath, opts).use { db2 ->
            assertEquals(1, warningCount.get())
            assertEquals("v1", db2.getString("", "k1"))
            assertEquals("v2", db2.getString("", "k2"))

            // Step 4: Write new entry to verify WAL continues healthy append
            db2.putString("", "k3", "v3")
        }

        CkvDb.open(dbPath, opts).use { db3 ->
            assertEquals("v3", db3.getString("", "k3"))
            assertEquals("v1", db3.getString("", "k1"))
        }
    }

    @Test
    fun testWalCompaction() {
        val dbPath = File(tempBase, "wal_compact.db").path
        val opts = DbOptions(
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            syncMode = SyncMode.IMMEDIATE
        )

        CkvDb.open(dbPath, opts).use { db ->
            for (i in 0 until 50) {
                db.putString("", "k_overwrite", "val_$i")
            }
            val bloatedSize = db.fileSize()
            assertTrue(bloatedSize > 1000)

            // Run compaction
            db.compact()
            val compactedSize = db.fileSize()
            assertTrue(compactedSize < bloatedSize)
            assertEquals("val_49", db.getString("", "k_overwrite"))
        }

        // Reopen compacted DB
        CkvDb.open(dbPath, opts).use { db2 ->
            assertEquals("val_49", db2.getString("", "k_overwrite"))
            assertEquals(1, db2.count(""))
        }
    }

    @Test
    fun testWalDropNamespace() {
        val dbPath = File(tempBase, "wal_namespaces.db").path
        val opts = DbOptions(
            engineMode = StorageEngineMode.APPEND_ONLY_LOG,
            syncMode = SyncMode.IMMEDIATE
        )

        CkvDb.open(dbPath, opts).use { db ->
            db.putString("ns1", "k1", "v1")
            db.putString("ns2", "k2", "v2")
            db.dropNamespace("ns1")
        }

        CkvDb.open(dbPath, opts).use { db2 ->
            assertEquals(listOf("ns2"), db2.namespaces())
            assertNull(db2.getString("ns1", "k1"))
            assertEquals("v2", db2.getString("ns2", "k2"))
        }
    }
}
