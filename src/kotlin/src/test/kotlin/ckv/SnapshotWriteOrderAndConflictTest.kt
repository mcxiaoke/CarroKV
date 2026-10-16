package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File

/**
 * Snapshot 模式写路径"先磁盘预检、后改内存"的顺序（C6）与外部改动检测口径（C7）。
 *
 * - **C6**：旧实现是 `applyMem()` → `beginFlush()` → `commitFlush()`。`beginFlush()` 失败
 *   （锁超时、外部改动）时内存已经改了、磁盘没落，进程内状态与磁盘永久分叉。其余四语言
 *   一律是 begin → apply → commit。本套件对 put / increment / batch / dropNamespace 四条
 *   路径逐条断言"写失败后内存不得残留"。
 * - **C7**：旧冲突检测只比较 `diskMaxTxId > currentMaxTxId`，外部把磁盘换成 **TxID 更小**
 *   的一代时完全检测不到。现在比对 `(maxTxId, fileSize)` 与上次已知状态。
 */
class SnapshotWriteOrderAndConflictTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_write_order").canonicalFile
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

    /** Snapshot + Immediate，锁超时压到 100ms 以便快速触发 LockedError。 */
    private fun options() = DbOptions(
        engineMode = StorageEngineMode.SNAPSHOT,
        syncMode = SyncMode.IMMEDIATE,
        lockTimeoutMs = 100
    )

    /**
     * 打开库、写入两代（`.bak` 持第一代），然后**在库之外**把上一代覆盖回主库。
     *
     * 于是下一次写会被写前预检判为并发修改（`TransactionConflictError`）——这正是 C6 要
     * 验证的场景："预检失败时内存不得已经改掉"。
     *
     * 早先用"另一个持锁者"制造同样的拒绝，但那在 C9 之后不再适用：锁现在由连接自己持有
     * 整个生命周期（第二个连接会直接 `LockedError`，根本拿不到实例），故改用外部改动触发。
     */
    private fun seedAndRollbackExternally(dbPath: String): CkvDb {
        val db = CkvDb.open(dbPath, options())
        db.putString("ns", "existing", "value-1")
        db.putString("ns", "second", "value-2")
        val bakPath = "$dbPath.bak"
        assertTrue(File(bakPath).exists(), "本用例需要存在上一代 `.bak`")
        File(bakPath).copyTo(File(dbPath), overwrite = true)
        return db
    }

    /** C6：写前预检失败时 put 不得残留任何内存改动。 */
    @Test
    fun failedPutMustNotMutateMemory() {
        val dbPath = tempPath("c6_put.db")
        seedAndRollbackExternally(dbPath).use { db ->
            assertThrows(TransactionConflictError::class.java) {
                db.putString("ns", "fresh", "value-2")
            }

            assertNull(db.getString("ns", "fresh"), "写失败后内存不得残留新键（C6）")
            assertEquals(2, db.count("ns"))
            assertEquals("value-1", db.getString("ns", "existing"))
        }
    }

    /** C6：increment 也是 read-modify-write，失败时内存（与计数器）都不得变。 */
    @Test
    fun failedIncrementMustNotMutateMemory() {
        val dbPath = tempPath("c6_increment.db")
        seedAndRollbackExternally(dbPath).use { db ->
            assertThrows(TransactionConflictError::class.java) { db.incrementInt64("ns", "counter", 5L) }
            assertEquals(null, db.getInt64("ns", "counter"), "内存计数器仍应是上一代的值（C6）")
        }
    }

    /** C6：batch 整批失败时，批内所有操作用不得部分生效。 */
    @Test
    fun failedBatchMustNotMutateMemory() {
        val dbPath = tempPath("c6_batch.db")
        seedAndRollbackExternally(dbPath).use { db ->
            assertThrows(TransactionConflictError::class.java) {
                db.batch { w ->
                    w.putString("ns", "fresh", "value-2")
                    w.delete("ns", "existing")
                }
            }

            assertNull(db.getString("ns", "fresh"), "失败批处理不得留下新键（C6）")
            assertEquals("value-1", db.getString("ns", "existing"), "失败批处理不得删除键（C6）")
        }
    }

    /** C6：dropNamespace 失败时命名空间必须还在。 */
    @Test
    fun failedDropNamespaceMustNotMutateMemory() {
        val dbPath = tempPath("c6_drop.db")
        seedAndRollbackExternally(dbPath).use { db ->
            assertThrows(TransactionConflictError::class.java) { db.dropNamespace("ns") }

            assertEquals(2, db.count("ns"), "失败的 dropNamespace 不得改动内存（C6）")
            assertTrue(db.containsKey("ns", "existing"))
        }
    }

    /**
     * C7：外部把磁盘换成 **TxID 更小** 的一代（这里用上一代 `.bak` 覆盖主库），
     * 下一次写必须被判定为并发修改，而不是静默覆盖外部改动。
     */
    @Test
    fun smallerExternalGenerationMustBeDetected() {
        val dbPath = tempPath("c7.db")
        val bakPath = "$dbPath.bak"

        CkvDb.open(dbPath, options()).use { db ->
            db.putString("ns", "k", "v1")
            db.putString("ns", "k", "v2")
            // 第三代：多一个键，确保与上一代**文件大小也不同**
            db.putString("ns", "k", "v3")
            db.putString("ns", "extra", "makes-the-generation-differ-in-size")
            assertTrue(File(bakPath).exists(), "本用例需要存在上一代 `.bak`")

            // 外部回滚：把上一代覆盖到主库（TxID 更小、大小也不同）
            File(bakPath).copyTo(File(dbPath), overwrite = true)

            assertThrows(TransactionConflictError::class.java) {
                db.putString("ns", "after-rollback", "must-be-rejected")
            }
            assertNull(db.getString("ns", "after-rollback"))
        }
    }

    /** 反向对照：没有外部改动时，连续写必须正常（防止预检变”过严“）。 */
    @Test
    fun untouchedDatabaseKeepsAcceptingWrites() {
        val dbPath = tempPath("no_conflict.db")
        CkvDb.open(dbPath, options()).use { db ->
            for (i in 1..5) {
                db.putString("ns", "k", "v$i")
                assertEquals("v$i", db.getString("ns", "k"))
            }
            // 重开后继续写也必须正常（lastKnownDisk* 由打开路径正确初始化）
            CkvDb.open(dbPath, options()).use { reopened ->
                reopened.putString("ns", "after-reopen", "ok")
                assertEquals("ok", reopened.getString("ns", "after-reopen"))
                assertFalse(reopened.containsKey("ns", "missing"))
            }
        }
    }
}
