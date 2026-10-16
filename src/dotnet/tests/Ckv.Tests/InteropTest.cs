using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    public class InteropTest
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
        public void TestDotnetReadsRustAndGoDataAndWritesDotnetData()
        {
            string interopDir = GetInteropDir();

            string rustDbPath = Path.Combine(interopDir, "rust_data.db");
            string goDbPath = Path.Combine(interopDir, "go_data.db");
            string dotnetDbPath = Path.Combine(interopDir, "dotnet_data.db");

            // 1. Read Rust written DB if present
            if (File.Exists(rustDbPath))
            {
                using var db = CkvDb.Open(rustDbPath);
                Assert.Equal("world from rust", db.GetString("", "hello"));
                Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0xFF }, db.GetBytes("bin_ns", "binary_key"));
                Assert.Equal("Alice", db.GetString("user_ns", "name"));
                Assert.Equal("Beijing", db.GetString("user_ns", "city"));
                Assert.Equal("happy", db.GetString("", "😀"));

                // Types written by Rust
                Assert.Equal(long.MinValue, db.GetInt64("types", "int_min"));
                Assert.Equal(long.MaxValue, db.GetInt64("types", "int_max"));
                Assert.Equal(3.141592653589793, db.GetFloat64("types", "float_pi"));
                double nanVal = db.GetFloat64("types", "float_nan")!.Value;
                Assert.True(double.IsNaN(nanVal));
                Assert.Equal(0x7FF8000000000000UL, (ulong)BitConverter.DoubleToInt64Bits(nanVal));
                Assert.True(db.GetBool("types", "bool_true"));
                Assert.False(db.GetBool("types", "bool_false"));
            }

            // 2. Read Go written DB if present
            if (File.Exists(goDbPath))
            {
                using var db = CkvDb.Open(goDbPath);
                Assert.Equal("world from go", db.GetString("", "hello"));
                Assert.Equal("Bob", db.GetString("user_ns", "name"));
                Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, db.GetBytes("bin_ns", "binary_key"));

                // Types written by Go
                Assert.Equal(-1234567890987654321L, db.GetInt64("types", "go_int"));
                Assert.Equal(2.718281828459045, db.GetFloat64("types", "go_float"));
                double goNan = db.GetFloat64("types", "go_nan")!.Value;
                Assert.True(double.IsNaN(goNan));
                Assert.Equal(0x7FF8000000000000UL, (ulong)BitConverter.DoubleToInt64Bits(goNan));
                Assert.True(db.GetBool("types", "go_bool"));
            }

            // 3. Write dotnet_data.db for Rust and Go to read
            //
            // Start from a clean fixture: the primary *and* its sidecars. Removing
            // only the primary would leave a previous generation behind, which is
            // refused by default since D1-8 (a missing primary next to a `.bak` is
            // never a fresh database).
            foreach (string suffix in new[] { "", ".bak", ".tmp", ".lock" })
            {
                try { File.Delete(dotnetDbPath + suffix); } catch { }
            }

            using (var db = CkvDb.Open(dotnetDbPath))
            {
                db.PutString("", "hello", "world from dotnet");
                db.PutString("user_ns", "name", "Charlie");
                db.PutBytes("bin_ns", "binary_key", new byte[] { 0x11, 0x22, 0x33, 0x44 });

                db.PutInt64("types", "dotnet_int", 987654321012345678L);
                db.PutFloat64("types", "dotnet_float", 1.4142135623730951);
                db.PutFloat64("types", "dotnet_nan", double.NaN);
                db.PutBool("types", "dotnet_bool", true);
            }
        }
    }
}
