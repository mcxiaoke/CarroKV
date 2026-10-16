using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 共享黄金向量测试（testdata/*.db）。
    ///
    /// 这些文件由 tools/gen_testdata.py 按 v3.2 规范**独立于三种实现**生成。
    /// 本测试做两件事：
    ///   1. 读取外部生成的文件 → 断言解析出的内容正确（证明互操作性）
    ///   2. 用本实现重新生成相同内容 → 断言字节与外部文件**完全一致**
    ///      （证明确定性序列化，字节级兼容）
    ///
    /// Rust / Go 有同构测试，三者必须通过同一套 testdata。
    /// testdata 目录通过 Ckv.Tests.csproj 的 None/Content 项复制到输出目录。
    /// </summary>
    public class GoldenTestdataTest
    {
        private static string TestdataDir()
        {
            // 优先使用随构建复制到输出目录的副本
            string local = Path.Combine(AppContext.BaseDirectory, "testdata");
            if (Directory.Exists(local)) return local;

            // 回退：从输出目录向上找仓库根的 testdata
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "testdata");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("testdata directory not found");
        }

        private static byte[] ReadVector(string name) =>
            File.ReadAllBytes(Path.Combine(TestdataDir(), name));

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private const string EmptySha =
            "06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328";
        private const string SinglePutSha =
            "f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a";

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_gd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // 空库：32 字节、SHA-256 与规范一致、可被打开且为空
        [Fact]
        public void GoldenTestdata_EmptyV3()
        {
            byte[] data = ReadVector("empty_v3.db");
            Assert.Equal(32, data.Length);
            Assert.Equal(EmptySha, Sha256Hex(data));

            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "copy.db");
                File.WriteAllBytes(p, data);
                using (var db = CkvDb.Open(p))
                {
                    Assert.Equal(0, db.Count(""));
                    Assert.Empty(db.Namespaces());
                    Assert.Equal(0UL, db.Stats().CurrentMaxTxId);
                    // 再落盘一次必须还原同样的 32 字节
                    db.Flush();
                }
                Assert.Equal(data, File.ReadAllBytes(p));
            }
            finally { DeleteTree(dir); }
        }

        // 单条 put：80 字节、SHA-256 与规范一致、内容与本地生成字节完全相同
        [Fact]
        public void GoldenTestdata_SinglePutV3()
        {
            byte[] data = ReadVector("single_put_v3.db");
            Assert.Equal(80, data.Length);
            Assert.Equal(SinglePutSha, Sha256Hex(data));

            string dir = NewTempDir();
            try
            {
                // 1) 解析外部文件
                string p = Path.Combine(dir, "single.db");
                File.WriteAllBytes(p, data);
                using (var db = CkvDb.Open(p))
                {
                    Assert.Equal("world", db.GetString("", "hello"));
                    Assert.Equal(1, db.Count(""));
                }

                // 2) 本地重新生成，字节必须相同
                string p2 = Path.Combine(dir, "regen.db");
                using (var db = CkvDb.Open(p2))
                {
                    db.PutString("", "hello", "world");
                }
                Assert.Equal(data, File.ReadAllBytes(p2));
            }
            finally { DeleteTree(dir); }
        }

        // 5 种 ValueType + 多命名空间：解析正确且本地重新生成字节相同
        [Fact]
        public void GoldenTestdata_MultiTypesV3()
        {
            byte[] data = ReadVector("multi_types_v3.db");
            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "multi.db");
                File.WriteAllBytes(p, data);
                using (var db = CkvDb.Open(p))
                {
                    Assert.Equal(new byte[] { 0x00, 0x01, 0xFE, 0xFF }, db.GetBytes("", "bytes"));
                    Assert.Equal("你好", db.GetString("", "str"));
                    Assert.Equal(-1234567890123456789L, db.GetInt64("", "i64"));
                    Assert.Equal(3.141592653589793, db.GetFloat64("", "f64"));
                    double? nan = db.GetFloat64("", "f64nan");
                    Assert.NotNull(nan);
                    Assert.True(double.IsNaN(nan!.Value));
                    Assert.Equal(0x7FF8000000000000UL,
                        BinaryHelper.ReadUInt64LE(BinaryHelper.EncodeFloat64(nan.Value)));
                    Assert.True(db.GetBool("", "bool_t"));
                    Assert.False(db.GetBool("", "bool_f"));
                    Assert.True(db.GetBool("cfg", "enabled"));
                    Assert.Equal(1000L, db.GetInt64("cfg", "limit"));
                    Assert.Equal("alice", db.GetString("users", "u1"));
                    Assert.Equal(new[] { "", "cfg", "users" }, db.Namespaces());
                    Assert.Equal(10, db.Stats().TotalEntries);
                }

                // 本地重新生成
                string p2 = Path.Combine(dir, "multi_regen.db");
                using (var db = CkvDb.Open(p2))
                {
                    db.Batch(b =>
                    {
                        b.PutBytes("", "bytes", new byte[] { 0x00, 0x01, 0xFE, 0xFF });
                        b.PutString("", "str", "你好");
                        b.PutInt64("", "i64", -1234567890123456789L);
                        b.PutFloat64("", "f64", 3.141592653589793);
                        b.PutFloat64("", "f64nan", double.NaN);
                        b.PutBool("", "bool_t", true);
                        b.PutBool("", "bool_f", false);
                        b.PutBool("cfg", "enabled", true);
                        b.PutInt64("cfg", "limit", 1000L);
                        b.PutString("users", "u1", "alice");
                    });
                }
                Assert.Equal(data, File.ReadAllBytes(p2));
            }
            finally { DeleteTree(dir); }
        }

        // 非 ASCII 命名空间/键：锁定 UTF-8 无符号字节序（而非 UTF-16 码元序）
        [Fact]
        public void GoldenTestdata_UnicodeOrderV3()
        {
            byte[] data = ReadVector("unicode_order_v3.db");
            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "uni.db");
                File.WriteAllBytes(p, data);
                using (var db = CkvDb.Open(p))
                {
                    Assert.Equal(new[] { "", "cf", "é", "中文" }, db.Namespaces());
                    Assert.Equal(
                        new[] { "a", "z", "\uE000", "\U0001F600" },
                        db.Keys(""));
                }

                // 本地重新生成
                string p2 = Path.Combine(dir, "uni_regen.db");
                using (var db = CkvDb.Open(p2))
                {
                    db.Batch(b =>
                    {
                        foreach (var kv in new[]
                        {
                            new[] { "", "a", "1" }, new[] { "", "z", "2" },
                            new[] { "", "\uE000", "3" }, new[] { "", "\U0001F600", "4" },
                            new[] { "é", "k", "5" }, new[] { "中文", "k", "6" },
                            new[] { "cf", "k", "7" },
                        })
                        {
                            b.PutString(kv[0], kv[1], kv[2]);
                        }
                    });
                }
                Assert.Equal(data, File.ReadAllBytes(p2));
            }
            finally { DeleteTree(dir); }
        }

        // 直接解码帧头字段核对（不经过 DB 实现）
        [Fact]
        public void GoldenTestdata_FrameHeaderLayout()
        {
            byte[] data = ReadVector("single_put_v3.db");
            Assert.Equal("UKVDB003", Encoding.ASCII.GetString(data, 0, 8));
            Assert.Equal(3, BinaryHelper.ReadUInt16LE(data.AsSpan(8, 2)));
            Assert.Equal(3, BinaryHelper.ReadUInt16LE(data.AsSpan(10, 2)));
            Assert.Equal(Constants.FrameMagic, BinaryHelper.ReadUInt32LE(data.AsSpan(32, 4)));
            Assert.Equal(1UL, BinaryHelper.ReadUInt64LE(data.AsSpan(40, 8)));
            Assert.Equal(1U, BinaryHelper.ReadUInt32LE(data.AsSpan(48, 4)));
            Assert.Equal(20U, BinaryHelper.ReadUInt32LE(data.AsSpan(52, 4)));
            Assert.Equal(0x01, data[60]); // OpPut
            Assert.Equal(0x01, data[61]); // Utf8String
            Assert.Equal(0, BinaryHelper.ReadUInt16LE(data.AsSpan(62, 2)));
            Assert.Equal(5, BinaryHelper.ReadUInt16LE(data.AsSpan(64, 2)));
            Assert.Equal(5U, BinaryHelper.ReadUInt32LE(data.AsSpan(66, 4)));
            Assert.Equal("hello", Encoding.ASCII.GetString(data, 70, 5));
            Assert.Equal("world", Encoding.ASCII.GetString(data, 75, 5));
        }

        // MANIFEST.txt 必须与磁盘上的实际文件一致
        [Fact]
        public void GoldenTestdata_ManifestMatches()
        {
            string manifest = File.ReadAllText(Path.Combine(TestdataDir(), "MANIFEST.txt"));
            foreach (var name in new[]
            {
                "empty_v3.db", "single_put_v3.db", "multi_types_v3.db", "unicode_order_v3.db",
            })
            {
                string digest = Sha256Hex(ReadVector(name));
                Assert.Contains(digest, manifest);
            }
        }
    }
}
