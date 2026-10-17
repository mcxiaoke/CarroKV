using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 跨语言夹具（.NET 侧）：读取 Kotlin / Dart 写出的夹具。
    ///
    /// Rust / Go / .NET 三方互读早已由 <see cref="InteropTest"/> 覆盖；Kotlin 与 Dart 此前
    /// 只有黄金向量校验，没有任何"真实写出的库文件被别人读"的闭环。夹具缺失时跳过
    /// （各语言套件按需生成），存在时逐字段断言。
    /// </summary>
    public class InteropFiveWayTest
    {
        private static string GetInteropDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                dir = dir.Parent;
            }
            string root = dir != null ? dir.FullName : Path.GetFullPath("../../../../../..");
            string interopDir = Path.Combine(root, "temp", "interop");
            Directory.CreateDirectory(interopDir);
            return interopDir;
        }

        [Fact]
        public void ReadKotlinWritten()
        {
            string path = Path.Combine(GetInteropDir(), "kotlin_data.db");
            if (!File.Exists(path)) return;

            using var db = CkvDb.Open(path);
            Assert.Equal("world from kotlin", db.GetString("", "hello"));
            Assert.Equal("Carol", db.GetString("user_ns", "name"));
            Assert.Equal("Shanghai", db.GetString("user_ns", "city"));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0xFF }, db.GetBytes("bin_ns", "binary_key"));
            Assert.Equal("happy", db.GetString("", "😀"));
            Assert.Equal(long.MinValue, db.GetInt64("types", "int_min"));
            Assert.Equal(long.MaxValue, db.GetInt64("types", "int_max"));
            Assert.Equal(3.141592653589793, db.GetFloat64("types", "float_pi"));
            double nan = db.GetFloat64("types", "float_nan")!.Value;
            Assert.True(double.IsNaN(nan));
            Assert.Equal(0x7FF8000000000000UL, (ulong)BitConverter.DoubleToInt64Bits(nan));
            Assert.True(db.GetBool("types", "bool_true"));
            Assert.False(db.GetBool("types", "bool_false"));
        }

        [Fact]
        public void ReadDartWritten()
        {
            string path = Path.Combine(GetInteropDir(), "dart_data.db");
            if (!File.Exists(path)) return;

            using var db = CkvDb.Open(path);
            Assert.Equal("world from dart", db.GetString("", "hello"));
            Assert.Equal("Dave", db.GetString("user_ns", "name"));
            Assert.Equal("Shenzhen", db.GetString("user_ns", "city"));
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, db.GetBytes("bin_ns", "binary_key"));
            Assert.Equal(long.MinValue, db.GetInt64("types", "int_min"));
            Assert.Equal(long.MaxValue, db.GetInt64("types", "int_max"));
            Assert.Equal(3.141592653589793, db.GetFloat64("types", "float_pi"));
            double nan = db.GetFloat64("types", "float_nan")!.Value;
            Assert.True(double.IsNaN(nan));
            Assert.Equal(0x7FF8000000000000UL, (ulong)BitConverter.DoubleToInt64Bits(nan));
            Assert.True(db.GetBool("types", "bool_true"));
            Assert.False(db.GetBool("types", "bool_false"));
        }
    }
}
