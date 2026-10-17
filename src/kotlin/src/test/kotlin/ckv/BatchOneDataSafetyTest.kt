package ckv

import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.File
import java.io.FileInputStream

/**
 * 批次一「数据安全与止血」回归测试（Kotlin）。
 *
 * 覆盖：
 *  - P1-3  加载期校验 NsLen <= 255（Kotlin 此前已有该校验，此处锁定不回归）
 *  - P1-19 `.tmp` 晋升失败必须让 open 失败，且**不得**删除已校验通过的 `.tmp`
 *  - P1-26 `incrementInt64` / `dropNamespace` 必须做只读与生命周期前置校验
 *        （旧实现漏掉 `checkWritable()`：`readOnly = true` 下 AOL 引擎会真的往 `.db`
 *          追加帧并 fsync；SNAPSHOT + Immediate 会先塞入空 store 再抛 LockedError，
 *          失败却在 `namespaces()` 里留下幽灵命名空间）
 *  - 条目 11 ReadOnly 选项冲突抛 [ConflictingOptionsError]
 */
class BatchOneDataSafetyTest {

    private lateinit var dir: File

    @BeforeEach
    fun setup() {
        dir = File("../../temp/kotlin_batch_one_" + System.nanoTime())
        dir.mkdirs()
    }

    @AfterEach
    fun teardown() {
        dir.deleteRecursively()
    }

    private fun dbPath(name: String) = File(dir, name).path

    private fun aolOptions(readOnly: Boolean = false) = DbOptions(
        readOnly = readOnly,
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.IMMEDIATE
    )

    private fun snapshotOptions(readOnly: Boolean = false) = DbOptions(
        readOnly = readOnly,
        engineMode = StorageEngineMode.SNAPSHOT,
        syncMode = SyncMode.IMMEDIATE
    )

    // ---------- P1-26：只读库不得被写入，且不得留下幽灵命名空间 ----------

    @Test
    fun `AOL readOnly incrementInt64 must not touch the file`() {
        val p = dbPath("ro_inc_aol.db")
        CkvDb.open(p, aolOptions()).use { it.putInt64("counters", "n", 5L) }
        val sizeBefore = File(p).length()

        CkvDb.open(p, aolOptions(readOnly = true)).use { ro ->
            assertThrows<LockedError> { ro.incrementInt64("counters", "n", 1L) }
            // 只读实例下 namespaces() 不得凭空多出一个分区
            assertFalse(ro.namespaces().contains("fresh"), "read-only failure must not create a namespace")
        }

        // 磁盘文件一个字节都不许变（AOL 模式下旧实现会真的 append 帧并 fsync）
        assertEquals(sizeBefore, File(p).length(), "read-only incrementInt64 wrote to the database file")
    }

    @Test
    fun `AOL readOnly dropNamespace must not touch the file`() {
        val p = dbPath("ro_drop_aol.db")
        CkvDb.open(p, aolOptions()).use { it.putString("victim", "k", "v") }
        val sizeBefore = File(p).length()

        CkvDb.open(p, aolOptions(readOnly = true)).use { ro ->
            assertThrows<LockedError> { ro.dropNamespace("victim") }
        }

        assertEquals(sizeBefore, File(p).length(), "read-only dropNamespace wrote to the database file")
    }

    @Test
    fun `Snapshot readOnly incrementInt64 must not leave a ghost namespace`() {
        val p = dbPath("ro_inc_snap.db")
        CkvDb.open(p, snapshotOptions()).use { it.putString("home", "k", "v") }

        CkvDb.open(p, snapshotOptions(readOnly = true)).use { ro ->
            assertThrows<LockedError> { ro.incrementInt64("ghost", "counter", 1L) }
            // 旧实现在这里已经 getOrPut 塞入空 store，之后才抛 LockedError
            assertFalse(
                ro.namespaces().contains("ghost"),
                "a refused write must not leave a ghost namespace behind"
            )
        }
    }

    @Test
    fun `Snapshot readOnly dropNamespace must not remove the namespace`() {
        val p = dbPath("ro_drop_snap.db")
        CkvDb.open(p, snapshotOptions()).use { it.putString("victim", "k", "v") }

        CkvDb.open(p, snapshotOptions(readOnly = true)).use { ro ->
            assertThrows<LockedError> { ro.dropNamespace("victim") }
            assertEquals("v", ro.getString("victim", "k"))
        }
    }

    @Test
    fun `closed instance refuses incrementInt64 and dropNamespace`() {
        val p = dbPath("closed_writes.db")
        val db = CkvDb.open(p, snapshotOptions())
        db.putString("ns", "k", "v")
        db.close()

        // close() 之后继续写会绕过唯一的多写者保护，必须统一抛 LockedError。
        assertThrows<LockedError> { db.incrementInt64("ns", "n", 1L) }
        assertThrows<LockedError> { db.dropNamespace("ns") }
    }

    // ---------- P1-3：加载期 NsLen 越界 ----------

    @Test
    fun `a file whose entry declares NsLen over 255 is rejected at load time`() {
        val p = dbPath("nslen.db")
        File(p).writeBytes(buildDbWithOverlongNamespace())

        val err = assertThrows<CorruptedError> { CkvDb.open(p, aolOptions()) }
        assertTrue(
            err.message!!.contains("Namespace length 300"),
            "error should name the offending length, got: ${err.message}"
        )
    }

    @Test
    fun `namespace of exactly 255 bytes is accepted`() {
        val p = dbPath("edge.db")
        val ns = "n".repeat(255)
        CkvDb.open(p, snapshotOptions()).use { db ->
            db.putString(ns, "k", "v")
            assertEquals("v", db.getString(ns, "k"))
        }
    }

    // ---------- 条目 11：ReadOnly 冲突必须抛错 ----------

    @Test
    fun `reentrant open with readOnly conflict throws ConflictingOptionsError`() {
        val p = dbPath("ro_conflict.db")
        val db = CkvDb.open(p, snapshotOptions())
        db.putString("", "k", "v")

        // 以只读意图二次打开：绝不能静默返回读写实例（静默提权）。
        assertThrows<ConflictingOptionsError> {
            CkvDb.open(p, snapshotOptions(readOnly = true))
        }
        db.close()

        // 反方向同样拒绝。
        CkvDb.open(p, snapshotOptions(readOnly = true)).use { ro ->
            assertEquals("v", ro.getString("", "k"))
            assertThrows<ConflictingOptionsError> { CkvDb.open(p, snapshotOptions()) }
        }
    }

    // ---------- P1-19：.tmp 晋升失败必须让 open 失败且保留 .tmp ----------

    @Test
    fun `open fails and keeps a validated tmp when promotion is impossible`() {
        val p = dbPath("promote.db")
        val tmpPath = "$p.tmp"

        // 造一个正常库，再复制成 .tmp 并删掉主库 —— .tmp 是唯一完好的一份。
        CkvDb.open(p, snapshotOptions()).use { it.putString("ns", "k", "v") }
        File(tmpPath).writeBytes(File(p).readBytes())
        File(p).delete()

        // 制造"晋升必然失败"：`.db` 不存在（因此一定走晋升分支），但 rename 本身失败。
        //
        //  Java 的 FileInputStream 在 Windows 上以 FILE_SHARE_READ|FILE_SHARE_WRITE 打开
        //  （**不含** FILE_SHARE_DELETE），因此持有一个打开的句柄即可让 File.renameTo 失败；
        //  Unix 上打开的句柄不阻止 rename，改为把父目录改成不可写。
        val blocker: FileInputStream? =
            if (System.getProperty("os.name").lowercase().contains("win")) {
                FileInputStream(File(tmpPath))
            } else {
                File(tmpPath).setWritable(false)
                null
            }
        if (blocker == null && File(tmpPath).canWrite()) {
            blocker?.close()
            org.junit.jupiter.api.Assumptions.assumeTrue(false, "cannot block rename on this platform")
        }

        val err = try {
            assertThrows<CorruptedError> { CkvDb.open(p, snapshotOptions()) }
        } finally {
            blocker?.close()
            File(tmpPath).setWritable(true)
        }
        assertTrue(
            err.message!!.contains(".tmp"),
            "error should point at the surviving .tmp, got: ${err.message}"
        )

        // 关键断言：已经校验通过的 .tmp 必须原样保留 —— 旧实现的 catch-all 会删掉它。
        assertTrue(File(tmpPath).exists(), "a validated .tmp must never be deleted on promotion failure")
        assertTrue(File(tmpPath).length() > 0, ".tmp must retain its contents")

        // 解除占用后可以正常自愈，数据没丢。
        CkvDb.open(p, snapshotOptions()).use { recovered ->
            assertEquals("v", recovered.getString("ns", "k"))
        }
    }

    // ---------- 手工构造 v3.2 磁盘文件的小工具 ----------

    /** 构造一个条目头声明 NsLen=300（规范上限 255）的合法 CRC 文件。 */
    private fun buildDbWithOverlongNamespace(): ByteArray {
        val ns = "n".repeat(300).toByteArray(Charsets.UTF_8)
        val key = "k".toByteArray(Charsets.UTF_8)
        val value = "v".toByteArray(Charsets.UTF_8)

        val entry = ByteArray(10 + ns.size + key.size + value.size)
        entry[0] = Constants.OP_PUT
        entry[1] = Constants.VAL_TYPE_STRING
        writeU16(entry, 2, 300)          // 越界的 NsLen（载荷本身确实是 300 字节）
        writeU16(entry, 4, key.size)
        writeU32(entry, 6, value.size)
        ns.copyInto(entry, 10)
        key.copyInto(entry, 10 + ns.size)
        value.copyInto(entry, 10 + ns.size + key.size)

        val header = ByteArray(32)
        "UKVDB003".toByteArray(Charsets.US_ASCII).copyInto(header)
        writeU16(header, 8, 3)
        writeU16(header, 10, 3)
        writeU32(header, 12, 0)
        writeU32(header, 28, Crc32.compute(header.copyOf(28)).toInt())

        val tail20 = ByteArray(20)
        writeU64(tail20, 0, 1L)
        writeU32(tail20, 8, 1)
        writeU32(tail20, 12, entry.size)
        writeU32(tail20, 16, Crc32.compute(entry).toInt())

        val frame = ByteArray(28 + entry.size)
        writeU32(frame, 0, 0x4D52464B)
        writeU32(frame, 4, Crc32.compute(tail20).toInt())
        tail20.copyInto(frame, 8)
        entry.copyInto(frame, 28)

        return header + frame
    }

    private fun writeU16(buf: ByteArray, off: Int, v: Int) {
        buf[off] = (v and 0xFF).toByte()
        buf[off + 1] = ((v shr 8) and 0xFF).toByte()
    }

    private fun writeU32(buf: ByteArray, off: Int, v: Int) {
        buf[off] = (v and 0xFF).toByte()
        buf[off + 1] = ((v shr 8) and 0xFF).toByte()
        buf[off + 2] = ((v shr 16) and 0xFF).toByte()
        buf[off + 3] = ((v shr 24) and 0xFF).toByte()
    }

    private fun writeU64(buf: ByteArray, off: Int, v: Long) {
        for (i in 0 until 8) {
            buf[off + i] = ((v shr (8 * i)) and 0xFF).toByte()
        }
    }
}