package ckv

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 跨语言夹具（Kotlin 侧）：写出 `temp/interop/kotlin_data.db` 供 Rust / .NET / Go 读取，
 * 并在其他语言的夹具存在时反向读取。
 *
 * 约定：所有语言写同一组键，`hello` 的值带语言名，二进制键用固定字节，类型键覆盖
 * i64 边界、NaN 位模式与布尔。
 */
class InteropTest {

    private val interopDir = File("../../temp/interop").canonicalFile

    private fun fixture(name: String): File = File(interopDir, name)

    private fun cleanFixture(name: String) {
        for (suffix in listOf("", ".bak", ".tmp", ".lock")) {
            File(interopDir, "$name$suffix").delete()
        }
    }

    @Test
    fun `writes kotlin fixture and reads the others when present`() {
        interopDir.mkdirs()
        cleanFixture("kotlin_data.db")

        CkvDb.open(fixture("kotlin_data.db").path).use { db ->
            db.putString("", "hello", "world from kotlin")
            db.putBytes("bin_ns", "binary_key", byteArrayOf(0x01, 0x02, 0x03, 0xFF.toByte()))
            db.putString("user_ns", "name", "Carol")
            db.putString("user_ns", "city", "Shanghai")
            db.putString("", "😀", "happy")

            db.putInt64("types", "int_min", Long.MIN_VALUE)
            db.putInt64("types", "int_max", Long.MAX_VALUE)
            db.putFloat64("types", "float_pi", Math.PI)
            db.putFloat64("types", "float_nan", Double.NaN)
            db.putBool("types", "bool_true", true)
            db.putBool("types", "bool_false", false)
        }

        val rust = fixture("rust_data.db")
        if (rust.exists()) {
            CkvDb.open(rust.path).use { db ->
                assertEquals("world from rust", db.getString("", "hello"))
                assertEquals("Alice", db.getString("user_ns", "name"))
                assertEquals("Beijing", db.getString("user_ns", "city"))
                assertEquals("happy", db.getString("", "😀"))
                assertEquals(Long.MIN_VALUE, db.getInt64("types", "int_min"))
                assertEquals(Long.MAX_VALUE, db.getInt64("types", "int_max"))
                assertEquals(Math.PI, db.getFloat64("types", "float_pi"))
                assertTrue(db.getFloat64("types", "float_nan")!!.isNaN())
                assertTrue(db.getBool("types", "bool_true")!!)
                assertTrue(!db.getBool("types", "bool_false")!!)
            }
        }

        val dart = fixture("dart_data.db")
        if (dart.exists()) {
            CkvDb.open(dart.path).use { db ->
                assertEquals("world from dart", db.getString("", "hello"))
                assertEquals("Dave", db.getString("user_ns", "name"))
            }
        }
    }
}
