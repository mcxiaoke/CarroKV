package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

class BatchThreeContractTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_b3_test").canonicalFile

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
    fun `isClosed reflects handle state`() {
        val dbPath = File(tempBase, "is_closed.db").path
        val db = CkvDb.open(dbPath)
        assertFalse(db.isClosed)
        db.close()
        assertTrue(db.isClosed)
    }

    @Test
    fun `putBytes performs defensive copy`() {
        val dbPath = File(tempBase, "defensive_copy.db").path
        CkvDb.open(dbPath).use { db ->
            val buf = byteArrayOf(10, 20, 30)
            db.putBytes("ns", "k", buf)
            // 修改外部 byte 数组
            buf[0] = 99
            val read = db.getBytes("ns", "k")
            assertNotNull(read)
            assertEquals(10.toByte(), read!![0], "External modification should not affect stored bytes")
        }
    }

    @Test
    fun `clearNamespace keeps namespace in namespaces list and fires CLEAR event`() {
        val dbPath = File(tempBase, "clear_ns.db").path
        CkvDb.open(dbPath).use { db ->
            val events = CopyOnWriteArrayList<KeyChangeEvent>()
            db.watch { events.add(it) }

            db.putString("keep_ns", "k1", "v1")
            db.putString("keep_ns", "k2", "v2")
            assertEquals(2, db.count("keep_ns"))
            assertTrue(db.namespaces().contains("keep_ns"))

            // 清空命名空间
            val cleared = db.clearNamespace("keep_ns")
            assertTrue(cleared)
            assertEquals(0, db.count("keep_ns"))
            assertFalse(db.containsKey("keep_ns", "k1"))
            // 关键规范约束：保留命名空间元数据
            assertTrue(db.namespaces().contains("keep_ns"), "clearNamespace must keep namespace in namespaces()")

            // 验证事件：单个 CLEAR 事件，Key 为空串
            val clearEvents = events.filter { it.op == KeyChangeOp.CLEAR }
            assertEquals(1, clearEvents.size)
            assertEquals("keep_ns", clearEvents[0].namespace)
            assertEquals("", clearEvents[0].key)

            // 重复清空空命名空间应返回 false 且不派发新事件
            assertFalse(db.clearNamespace("keep_ns"))
            assertEquals(1, events.filter { it.op == KeyChangeOp.CLEAR }.size)
        }
    }

    @Test
    fun `dropNamespace removes namespace completely and fires DROP_NAMESPACE event`() {
        val dbPath = File(tempBase, "drop_ns.db").path
        CkvDb.open(dbPath).use { db ->
            val events = CopyOnWriteArrayList<KeyChangeEvent>()
            db.watch { events.add(it) }

            db.putString("remove_ns", "k1", "v1")
            assertTrue(db.namespaces().contains("remove_ns"))

            val dropped = db.dropNamespace("remove_ns")
            assertTrue(dropped)
            assertFalse(db.namespaces().contains("remove_ns"), "dropNamespace must completely remove namespace")

            // 验证事件：单个 DROP_NAMESPACE 事件，Key 为空串，无逐键 DELETE
            val dropEvents = events.filter { it.op == KeyChangeOp.DROP_NAMESPACE }
            assertEquals(1, dropEvents.size)
            assertEquals("remove_ns", dropEvents[0].namespace)
            assertEquals("", dropEvents[0].key)
            assertEquals(0, events.filter { it.op == KeyChangeOp.DELETE }.size)

            // 重复 drop 不存在的命名空间返回 false
            assertFalse(db.dropNamespace("remove_ns"))
        }
    }

    @Test
    fun `iterPrefix scans matching entries in order and works on Snapshot`() {
        val dbPath = File(tempBase, "iter_prefix.db").path
        CkvDb.open(dbPath).use { db ->
            db.putString("ns", "user:001", "alice")
            db.putString("ns", "user:002", "bob")
            db.putString("ns", "order:999", "item")

            val entries = db.iterPrefix("ns", "user:")
            assertEquals(2, entries.size)
            assertEquals("user:001", entries[0].key)
            assertEquals("user:002", entries[1].key)

            // 快照上的 iterPrefix
            db.snapshot().use { snap ->
                val snapEntries = snap.iterPrefix("ns", "order:")
                assertEquals(1, snapEntries.size)
                assertEquals("order:999", snapEntries[0].key)
            }
        }
    }

    @Test
    fun `batch clearNamespace and dropNamespace dispatch correct ABI events`() {
        val dbPath = File(tempBase, "batch_clear_drop.db").path
        CkvDb.open(dbPath).use { db ->
            val events = CopyOnWriteArrayList<KeyChangeEvent>()
            db.watch { events.add(it) }

            db.putString("b_clear", "k1", "v1")
            db.putString("b_drop", "k2", "v2")

            db.batch { b ->
                b.clearNamespace("b_clear")
                b.dropNamespace("b_drop")
            }

            assertTrue(db.namespaces().contains("b_clear"))
            assertFalse(db.namespaces().contains("b_drop"))
            assertEquals(0, db.count("b_clear"))

            val clearEvents = events.filter { it.op == KeyChangeOp.CLEAR }
            val dropEvents = events.filter { it.op == KeyChangeOp.DROP_NAMESPACE }
            assertEquals(1, clearEvents.size)
            assertEquals(1, dropEvents.size)
        }
    }
}
