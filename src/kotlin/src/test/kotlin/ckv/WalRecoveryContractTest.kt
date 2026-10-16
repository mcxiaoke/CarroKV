package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.RandomAccessFile

/**
 * 日志损坏分类与 `.bak` 恢复契约的回归测试，与 Rust / Go / .NET / Dart 四套同源套件
 * 一一对应，确保五语言行为一致。
 *
 * 覆盖缺陷：
 *  - C4：日志中间损坏（损坏点之后仍有完整帧）原先被当作断电残尾静默截断，
 *    连带丢弃损坏点之后的全部完整帧；
 *  - D1：主库损坏时原先会静默用 `.bak` 替换主库（`.bak` 是上一次轮转**之前**的一代，
 *    替换即回滚此后写入的全部数据）；
 *  - G1：恢复源在加载时不得被残尾截断；
 *  - D1-9：只读打开损坏库不得写盘。
 *
 * 注意：Kotlin 目前**没有**自动压实（缺 `compactionThresholdBytes`，见方案 C8），
 * 因此 Rust / Go / .NET / Dart 套件中的"自动压实"用例在此不适用，
 * 待 C8 落地时一并补齐。
 */
class WalRecoveryContractTest {

    companion object {
        private val tempBase = File("../../temp/kotlin_wal_recovery").canonicalFile
        private var probeSeq = 0

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

    private val fileHeaderLen = 32

    private fun options(
        autoRecoverFromBackup: Boolean = false,
        readOnly: Boolean = false,
        onWarning: ((String, String) -> Unit)? = null
    ) = DbOptions(
        readOnly = readOnly,
        autoRecoverFromBackup = autoRecoverFromBackup,
        engineMode = StorageEngineMode.APPEND_ONLY_LOG,
        syncMode = SyncMode.IMMEDIATE,
        onWarning = onWarning
    )

    /**
     * 默认（SnapshotAtomic）模式的选项。
     *
     * `.bak` 的恢复契约用例必须用这个模式：WAL 模式下只有压实才会轮转出 `.bak`，
     * 单纯写入两代并不会产生备份。
     */
    private fun defaultModeOptions(
        autoRecoverFromBackup: Boolean = false,
        readOnly: Boolean = false,
        onWarning: ((String, String) -> Unit)? = null
    ) = DbOptions(
        readOnly = readOnly,
        autoRecoverFromBackup = autoRecoverFromBackup,
        onWarning = onWarning
    )

    private fun flipByteAt(path: String, offset: Long) {
        RandomAccessFile(path, "rw").use { raf ->
            raf.seek(offset)
            val original = raf.read()
            assertTrue(original >= 0, "offset $offset must be inside the file")
            raf.seek(offset)
            raf.write(original xor 0xFF)
            raf.fd.sync()
        }
    }

    /**
     * 单次 `putString` 产生的帧长度，实测取得，不依赖帧布局常量。
     *
     * 每次调用使用独立探针文件：复用同一路径会在下一次调用时再追加一帧，
     * 使测得的长度翻倍。
     */
    private fun probeFrameLen(): Long {
        val probe = File(tempBase, "probe_frame_len_${probeSeq++}.db").path
        CkvDb.open(probe, options()).use { db -> db.putString("", "a", "1") }
        val total = File(probe).length()
        assertTrue(total > fileHeaderLen, "probe database must contain one frame, got $total bytes")
        return total - fileHeaderLen
    }

    private fun writeThreeFrames(dbPath: String) {
        CkvDb.open(dbPath, options()).use { db ->
            db.putString("", "a", "1")
            db.putString("", "b", "2")
            db.putString("", "c", "3")
        }
    }

    /** 写两代（`.bak` 持第一代），随后翻转主库文件头中的一个字节使其校验失败。 */
    private fun writeTwoGenerationsAndCorruptPrimary(dbPath: String, opts: DbOptions) {
        for (value in listOf("v1", "v2")) {
            CkvDb.open(dbPath, opts).use { db -> db.putString("", "version", value) }
        }
        flipByteAt(dbPath, 14) // 落在 32 字节文件头内
    }

    /** C4：损坏记录之后仍有完整记录——这在只追加文件上不可能是断电残尾，必须报损坏。 */
    @Test
    fun midLogDamageMustBeReportedAsCorruption() {
        val dbPath = File(tempBase, "t4.db").path
        val opts = options()
        val frameLen = probeFrameLen()

        writeThreeFrames(dbPath)
        val healthyLen = fileHeaderLen + 3 * frameLen

        // 翻转第二帧内的一个字节，第三帧保持完整
        flipByteAt(dbPath, fileHeaderLen + frameLen + 8)

        assertThrows(CorruptedError::class.java) { CkvDb.open(dbPath, opts) }

        // 被拒开后日志必须原样保留，供离线抢救
        assertEquals(healthyLen, File(dbPath).length())
    }

    /** C4 反向对照：真正的残尾（文件在最后一帧内结束）必须继续自愈。 */
    @Test
    fun tornTailMustStillHeal() {
        val dbPath = File(tempBase, "t5.db").path
        val opts = options()
        val frameLen = probeFrameLen()

        writeThreeFrames(dbPath)
        val healthyLen = File(dbPath).length()

        RandomAccessFile(dbPath, "rw").use { raf ->
            raf.setLength(healthyLen - frameLen / 2)
            raf.fd.sync()
        }

        CkvDb.open(dbPath, opts).use { db ->
            assertEquals("1", db.getString("", "a"))
            assertEquals("2", db.getString("", "b"))
            assertNull(db.getString("", "c"), "残帧必须整体丢弃")
        }

        assertEquals(fileHeaderLen + 2 * frameLen, File(dbPath).length())
    }

    /** D1：主库损坏时默认必须报错拒开，错误信息需指向 `.bak`。 */
    @Test
    fun corruptPrimaryIsRejectedByDefault() {
        val dbPath = File(tempBase, "d1.db").path
        val bakPath = "$dbPath.bak"
        val warnings = mutableListOf<String>()

        val opts = defaultModeOptions(onWarning = { code, _ -> warnings.add(code) })
        writeTwoGenerationsAndCorruptPrimary(dbPath, opts)
        assertTrue(File(bakPath).exists(), "本用例需要存在上一代 `.bak`")

        val primaryLen = File(dbPath).length()
        val bakLen = File(bakPath).length()

        val ex = assertThrows(CorruptedError::class.java) { CkvDb.open(dbPath, opts) }
        assertTrue(
            ex.message?.contains(bakPath) == true,
            "错误信息必须把运维引向那份离线副本，实际为：${ex.message}"
        )

        assertFalse(warnings.contains("onRecoveredFromBackup"), "未显式开启时不得自动恢复")
        assertEquals(primaryLen, File(dbPath).length(), "主库必须原样保留")
        assertEquals(bakLen, File(bakPath).length(), "`.bak` 必须原样保留")
    }

    /** D1 的显式逃生口：开启 autoRecoverFromBackup 后恢复旧行为（含告警）。 */
    @Test
    fun corruptPrimaryHealsWhenExplicitlyEnabled() {
        val dbPath = File(tempBase, "d1_optin.db").path
        val warnings = mutableListOf<String>()
        val opts = defaultModeOptions(autoRecoverFromBackup = true, onWarning = { code, _ -> warnings.add(code) })

        writeTwoGenerationsAndCorruptPrimary(dbPath, opts)

        CkvDb.open(dbPath, opts).use { db ->
            // 上一代只有 v1；损坏的那一代含 v1+v2
            assertEquals("v1", db.getString("", "version"))
        }
        assertTrue(warnings.contains("onRecoveredFromBackup"), "恢复告警必须触发")
    }

    /**
     * G1：恢复源在加载时绝不能被截断——截断它会毁掉备份本身的数据。
     *
     * 特意使用 APPEND_ONLY_LOG：只有该模式允许对**主日志**做残尾截断，
     * 若恢复源继承该行为，撕裂的 `.bak` 会被悄悄削平后覆盖到主库。
     */
    @Test
    fun recoverySourceIsNotTruncated() {
        val dbPath = File(tempBase, "g1.db").path
        val bakPath = "$dbPath.bak"
        val opts = options(autoRecoverFromBackup = true)

        CkvDb.open(dbPath, opts).use { db ->
            db.putString("", "version", "v1")
            db.compact()                       // 第一代轮转进 `.bak`
            db.putString("", "version", "v2")  // 第二代留在日志里
        }
        assertTrue(File(bakPath).exists(), "压实必须产生 `.bak`")

        // 撕掉恢复源的尾部
        val tornLen = File(bakPath).length() - 4
        RandomAccessFile(bakPath, "rw").use { raf ->
            raf.setLength(tornLen)
            raf.fd.sync()
        }

        // 破坏主库，使恢复路径被走到
        val primaryLen = File(dbPath).length()
        flipByteAt(dbPath, primaryLen - 2)

        assertThrows(CorruptedError::class.java) { CkvDb.open(dbPath, opts) }
        assertEquals(tornLen, File(bakPath).length(), "`.bak` 作为恢复源不得被截断（G1）")
        assertEquals(primaryLen, File(dbPath).length(), "主库必须原样保留")
    }

    /** D1-9：只读打开损坏库必须失败且不写盘，并忽略 autoRecoverFromBackup。 */
    @Test
    fun readOnlyNeverWritesOnCorruption() {
        val dbPath = File(tempBase, "ro.db").path
        val bakPath = "$dbPath.bak"

        writeTwoGenerationsAndCorruptPrimary(dbPath, defaultModeOptions())

        val primaryLen = File(dbPath).length()
        val bakLen = File(bakPath).length()

        assertThrows(CorruptedError::class.java) {
            CkvDb.open(dbPath, defaultModeOptions(readOnly = true))
        }
        assertThrows(CorruptedError::class.java) {
            CkvDb.open(dbPath, defaultModeOptions(readOnly = true, autoRecoverFromBackup = true))
        }

        assertEquals(primaryLen, File(dbPath).length(), "只读不得写主库")
        assertEquals(bakLen, File(bakPath).length(), "只读不得写 `.bak`")
    }

    /** C12 / T12：恢复期校验定长类型与保留类型。 */
    @Test
    fun t12RecoveryRejectsInvalidValueTypeOrLength() {
        data class TestCase(val name: String, val vType: Byte, val valBytes: ByteArray)
        val cases = listOf(
            TestCase("reserved_vtype", 0x05.toByte(), "val".toByteArray(Charsets.UTF_8)),
            TestCase("bad_int64_len", Constants.VAL_TYPE_INT64, byteArrayOf(1, 2, 3, 4, 5, 6, 7)),
            TestCase("bad_bool_len", Constants.VAL_TYPE_BOOL, byteArrayOf(1, 0, 1))
        )

        for (tc in cases) {
            val dbPath = File(tempBase, "${tc.name}.db").path
            val file = File(dbPath)
            java.io.FileOutputStream(file).use { fos ->
                fos.write(FileHeader.createBytes())
                val entry = RawOpEntry(
                    Constants.OP_PUT,
                    tc.vType,
                    "ns".toByteArray(Charsets.UTF_8),
                    "k".toByteArray(Charsets.UTF_8),
                    tc.valBytes
                )
                BatchFrameWriter.writeFrame(fos, 1, listOf(entry))
            }
            assertThrows(CorruptedError::class.java) {
                CkvDb.open(dbPath)
            }
        }
    }

    /** C17：重复 open 选项冲突告警。 */
    @Test
    fun t17ReentrantOpenWarnsOnOptionsConflict() {
        val dbPath = File(tempBase, "c17_conflict.db").path
        val db1 = CkvDb.open(dbPath, defaultModeOptions(readOnly = false))
        db1.putString("", "k", "v")

        val warnings = mutableListOf<String>()
        val opts2 = DbOptions(
            readOnly = true,
            syncMode = SyncMode.MANUAL,
            onWarning = { code, msg -> warnings.add("$code: $msg") }
        )

        val db2 = CkvDb.open(dbPath, opts2)
        assertSame(db1, db2)
        assertEquals(1, warnings.size)
        assertTrue(warnings[0].startsWith("onOptionsConflict"))
        assertTrue(warnings[0].contains("readOnly"))

        db1.close()
    }
}

