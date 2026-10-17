package ckv

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import java.io.File
import java.io.IOException

/**
 * Fuzz-lite：对真实数据库文件做确定性变异。
 *
 * 不是覆盖率引导的 fuzzer。固定、可复现的变异集覆盖黄金向量 + 一个生成的 AOL 文件，
 * 断言两件事：
 *   1. 不崩溃、不挂死——被变异过的文件要么被干净拒绝，要么能完整读出；
 *   2. 结构性损坏必须被检测，不能被当成空库静默接受。
 *
 * 与 Rust / Go / .NET / Dart 的同源套件一致。
 */
class FuzzLiteTest {

    companion object {
        private val testdata = File("../../testdata").canonicalFile
        private val workBase = File("../../temp/kotlin_fuzz_lite").canonicalFile
        private var seq = 0

        @BeforeAll
        @JvmStatic
        fun setup() {
            if (workBase.exists()) workBase.deleteRecursively()
            workBase.mkdirs()
        }

        @AfterAll
        @JvmStatic
        fun teardown() {
            if (workBase.exists()) workBase.deleteRecursively()
        }
    }

    private data class Seed(val name: String, val bytes: ByteArray)

    private fun seeds(): List<Seed> {
        val out = mutableListOf<Seed>()
        for (name in listOf("empty_v3.db", "single_put_v3.db", "multi_types_v3.db", "unicode_order_v3.db")) {
            val f = File(testdata, name)
            if (f.exists()) out += Seed(name, f.readBytes())
        }

        val dir = File(workBase, "seed_${seq++}").apply { mkdirs() }
        val path = File(dir, "generated.db")
        try {
            CkvDb.open(
                path.path,
                DbOptions(
                    engineMode = StorageEngineMode.APPEND_ONLY_LOG,
                    syncMode = SyncMode.MANUAL
                )
            ).use { db ->
                db.putString("ns", "alpha", "1")
                db.putString("ns", "beta", "22")
                db.putInt64("ns2", "gamma", Long.MIN_VALUE)
            }
            out += Seed("generated_aol", path.readBytes())
        } finally {
            dir.deleteRecursively()
        }
        return out
    }

    private fun accepted(err: Throwable): Boolean = when (err) {
        is CorruptedError, is UnsupportedVersionError, is InvalidUtf8Error,
        is KeyTooLongError, is ValueTooLongError, is PayloadTooLargeError, is IOException -> true
        else -> false
    }

    /** 返回 "opened" / "rejected"；非预期异常直接抛出。 */
    private fun exercise(dir: File, tag: Int, data: ByteArray): String {
        val path = File(dir, "case_$tag.db")
        path.writeBytes(data)
        return try {
            CkvDb.open(path.path).use { db ->
                for (ns in db.namespaces()) {
                    db.count(ns)
                    for (key in db.keys(ns)) {
                        db.getBytes(ns, key)
                    }
                    db.scanRange(ns, null, null)
                }
                db.fileSize()
            }
            "opened"
        } catch (err: Throwable) {
            if (accepted(err)) "rejected" else throw err
        }
    }

    @Test
    fun `mutated files never panic`() {
        val dir = File(workBase, "cases_${seq++}").apply { mkdirs() }
        var tag = 0
        var cases = 0
        var rejected = 0

        fun check(seed: String, what: String, data: ByteArray) {
            tag++
            cases++
            try {
                if (exercise(dir, tag, data) == "rejected") rejected++
            } catch (err: Throwable) {
                throw AssertionError("$seed $what: unexpected ${err::class.simpleName}: ${err.message}", err)
            }
        }

        try {
            for (seed in seeds()) {
                val original = seed.bytes
                val n = original.size

                val cuts = mutableListOf(0, 1, 15, 31, 32, 33)
                if (n > 1) cuts += n - 1
                for (cut in cuts) {
                    if (cut > n) continue
                    check(seed.name, "truncated to $cut bytes", original.copyOfRange(0, cut))
                }

                for (offset in 0 until n) {
                    for (mask in byteArrayOf(0xFF.toByte(), 0x01, 0x80.toByte())) {
                        val mutated = original.copyOf()
                        mutated[offset] = (mutated[offset].toInt() xor mask.toInt()).toByte()
                        check(seed.name, "flip at $offset", mutated)
                    }
                }

                for (extra in listOf(byteArrayOf(0x00), byteArrayOf(0xFF.toByte()),
                                     byteArrayOf(0x00, 0x01, 0x02, 0x03))) {
                    check(seed.name, "append ${extra.size} bytes", original + extra)
                }

                // 结构性损坏必须被检测。
                tag++
                cases++
                val short = File(dir, "case_$tag.db")
                short.writeBytes(original.copyOfRange(0, minOf(31, n)))
                val shortOpened = try {
                    CkvDb.open(short.path).use { }
                    true
                } catch (err: Throwable) {
                    if (accepted(err)) false else throw err
                }
                assertTrue(!shortOpened, "${seed.name} truncated below the header was accepted")

                if (n > 0) {
                    tag++
                    cases++
                    val magic = original.copyOf()
                    magic[0] = (magic[0].toInt() xor 0xFF).toByte()
                    val magicFile = File(dir, "case_$tag.db")
                    magicFile.writeBytes(magic)
                    val magicOpened = try {
                        CkvDb.open(magicFile.path).use { }
                        true
                    } catch (err: Throwable) {
                        if (accepted(err)) false else throw err
                    }
                    assertTrue(!magicOpened, "${seed.name} with corrupted magic was accepted")
                }
            }
        } finally {
            dir.deleteRecursively()
        }

        assertTrue(cases > 1000, "mutation set unexpectedly small: $cases")
        assertTrue(
            rejected > cases / 10,
            "only $rejected of $cases mutations were rejected - corruption detection looks broken"
        )
    }
}
