package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

class CrudTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_crud_test").canonicalFile

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
    fun testBasicCrudAndTypes() {
        val dbPath = File(tempBase, "crud_types.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("", "str_key", "hello world")
            db.putBytes("", "bytes_key", byteArrayOf(1, 2, 3, 4))
            db.putInt64("", "int_key", 123456789L)
            db.putFloat64("", "float_key", 2.71828)
            db.putBool("", "bool_key", true)

            assertEquals("hello world", db.getString("", "str_key"))
            assertArrayEquals(byteArrayOf(1, 2, 3, 4), db.getBytes("", "bytes_key"))
            assertEquals(123456789L, db.getInt64("", "int_key"))
            assertEquals(2.71828, db.getFloat64("", "float_key"))
            assertEquals(true, db.getBool("", "bool_key"))

            // Type mismatch
            assertThrows(TypeMismatchError::class.java) {
                db.getInt64("", "str_key")
            }

            // Key deletion
            assertTrue(db.delete("", "str_key"))
            assertNull(db.getString("", "str_key"))
            assertFalse(db.delete("", "str_key"))
        }
    }

    @Test
    fun testIncrementInt64Wrap() {
        val dbPath = File(tempBase, "increment.db").path
        CkvDb.open(dbPath).use { db ->
            val v1 = db.incrementInt64("", "counter", 5)
            assertEquals(5L, v1)
            val v2 = db.incrementInt64("", "counter", 10)
            assertEquals(15L, v2)

            // Test 64-bit integer overflow wrap
            db.putInt64("", "overflow", Long.MAX_VALUE)
            val wrapped = db.incrementInt64("", "overflow", 1)
            assertEquals(Long.MIN_VALUE, wrapped)
        }
    }

    @Test
    fun testNamespacesAndRangeScan() {
        val dbPath = File(tempBase, "namespaces.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("ns1", "a", "val_a")
            db.putString("ns1", "b", "val_b")
            db.putString("ns1", "c", "val_c")
            db.putString("ns2", "x", "val_x")

            assertEquals(listOf("ns1", "ns2"), db.namespaces())
            assertEquals(3, db.count("ns1"))
            assertEquals(1, db.count("ns2"))

            val entries = db.scanRange("ns1", "a", "c")
            assertEquals(2, entries.size)
            assertEquals("a", entries[0].key)
            assertEquals("b", entries[1].key)

            db.dropNamespace("ns1")
            assertEquals(0, db.count("ns1"))
            assertEquals(listOf("ns2"), db.namespaces())
        }
    }

    @Test
    fun testBatchRollbackOnError() {
        val dbPath = File(tempBase, "batch_err.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("", "persist_k", "persist_v")

            assertThrows(RuntimeException::class.java) {
                db.batch { b ->
                    b.putString("", "temp_k1", "temp_v1")
                    b.putString("", "temp_k2", "temp_v2")
                    throw RuntimeException("Intentional failure in transaction block")
                }
            }

            assertEquals("persist_v", db.getString("", "persist_k"))
            assertNull(db.getString("", "temp_k1"))
            assertNull(db.getString("", "temp_k2"))
        }
    }

    @Test
    fun testWatchNotificationsAndUnwatch() {
        val dbPath = File(tempBase, "watch.db").path
        val changeCount = AtomicInteger(0)

        CkvDb.open(dbPath).use { db ->
            val watcher = db.watch { event ->
                changeCount.incrementAndGet()
            }

            db.putString("", "k1", "v1")
            db.delete("", "k1")
            assertEquals(2, changeCount.get())

            watcher.unwatch()
            db.putString("", "k2", "v2")
            assertEquals(2, changeCount.get())
        }
    }

    @Test
    fun testSnapshotIsolation() {
        val dbPath = File(tempBase, "snapshot.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("", "k1", "v1")

            val snap = db.snapshot()
            db.putString("", "k1", "v2")
            db.putString("", "k2", "v2")

            assertEquals("v1", snap.getString("", "k1"))
            assertNull(snap.getString("", "k2"))
            assertEquals("v2", db.getString("", "k1"))
            assertEquals("v2", db.getString("", "k2"))
            snap.close()
        }
    }
}
