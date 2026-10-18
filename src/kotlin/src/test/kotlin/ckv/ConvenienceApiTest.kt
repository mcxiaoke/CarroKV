package ckv

import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files

class ConvenienceApiTest {

    private lateinit var testDir: File

    @BeforeEach
    fun setUp() {
        testDir = Files.createTempDirectory("ckv_kt_test_convenience").toFile()
    }

    @AfterEach
    fun tearDown() {
        testDir.deleteRecursively()
    }

    @Test
    fun testDbDefaultNamespaceOverloadsAndOperator() {
        val dbFile = File(testDir, "test_convenience.db")
        CkvDb.open(dbFile.absolutePath).use { db ->
            // 1. 基础重载
            db.putString("strKey", "strVal")
            assertEquals("strVal", db.getString("strKey"))
            assertEquals("strVal", db.getString("", "strKey"))

            db.putBytes("byteKey", "byteVal".toByteArray(Charsets.UTF_8))
            assertEquals("byteVal", String(db.getBytes("byteKey")!!, Charsets.UTF_8))

            db.putInt64("intKey", 42L)
            assertEquals(42L, db.getInt64("intKey"))
            assertEquals(50L, db.incrementInt64("intKey", 8L))
            assertEquals(50L, db.getInt64("intKey"))

            db.putFloat64("floatKey", 3.14159)
            assertEquals(3.14159, db.getFloat64("floatKey")!!, 0.00001)

            db.putBool("boolKey", true)
            assertTrue(db.getBool("boolKey")!!)

            assertTrue(db.containsKey("strKey"))
            assertTrue(db.containsKey("intKey"))
            assertFalse(db.containsKey("non_existent"))

            assertEquals(5, db.count())
            val keys = db.keys()
            assertTrue(keys.contains("strKey"))
            assertTrue(keys.contains("intKey"))

            // 2. 运算符索引器语法糖
            db["indexedKey"] = "indexedValue"
            assertEquals("indexedValue", db["indexedKey"])
            assertEquals("indexedValue", db.getString("indexedKey"))

            db["indexedKey"] = null
            assertNull(db["indexedKey"])
            assertFalse(db.containsKey("indexedKey"))

            // 3. 迭代与扫描
            val allPrefix = db.iterPrefix()
            assertTrue(allPrefix.isNotEmpty())

            val prefixFiltered = db.default.iterPrefix("str")
            assertEquals(1, prefixFiltered.size)
            assertEquals("strKey", prefixFiltered[0].key)

            val allRange = db.scanRange()
            assertTrue(allRange.isNotEmpty())

            // 4. 删除与清空
            assertTrue(db.delete("strKey"))
            assertFalse(db.containsKey("strKey"))

            assertTrue(db.clearNamespace())
            assertEquals(0, db.count())
        }
    }

    @Test
    fun testNamespaceScopeFacade() {
        val dbFile = File(testDir, "test_scope.db")
        CkvDb.open(dbFile.absolutePath).use { db ->
            val userScope = db.ns("users")
            userScope["user:1"] = "Alice"
            userScope.putInt64("age:1", 30L)

            assertEquals("Alice", userScope["user:1"])
            assertEquals("Alice", db.getString("users", "user:1"))
            assertEquals(30L, userScope.getInt64("age:1"))
            assertEquals(2, userScope.count())

            val defaultScope = db.default
            defaultScope["site"] = "github"
            assertEquals("github", db.getString("", "site"))
            assertEquals("github", db["site"])

            assertTrue(userScope.delete("user:1"))
            assertFalse(userScope.containsKey("user:1"))
        }
    }

    @Test
    fun testSnapshotAndBatchConvenience() {
        val dbFile = File(testDir, "test_snap_batch.db")
        CkvDb.open(dbFile.absolutePath).use { db ->
            // BatchWriter 便利接口
            db.batch { b ->
                b["batchKey"] = "batchVal"
                b.putInt64("batchInt", 999L)
                b.ns("audit")["action"] = "login"

                // 读己所写
                assertEquals("batchVal", b["batchKey"])
                assertEquals("login", b.ns("audit")["action"])
            }

            assertEquals("batchVal", db["batchKey"])
            assertEquals(999L, db.getInt64("batchInt"))
            assertEquals("login", db.getString("audit", "action"))

            // Snapshot 便利接口
            db.snapshot().use { snap ->
                assertEquals("batchVal", snap["batchKey"])
                assertEquals(999L, snap.getInt64("batchInt"))
                assertTrue(snap.containsKey("batchKey"))
                assertEquals("login", snap.ns("audit")["action"])
                assertEquals(2, snap.count())
            }
        }
    }
}
