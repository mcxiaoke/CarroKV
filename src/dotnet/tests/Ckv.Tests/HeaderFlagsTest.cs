using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// M1-01 回归：非零 <c>HeaderFlags</c> 必须拒绝打开。
    ///
    /// 规范 KV-DB-DESIGN-SPEC.md:79：「v3.2 固定填 0；若读取端遇到未知非零必选位则
    /// 拒绝打开」。本实现此前只校验 magic / readVer / CRC，12..16 从未读取。
    /// </summary>
    public class HeaderFlagsTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_flags_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void NonZeroHeaderFlagsAreRejected()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "flags.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("k", "v");
                }

                byte[] bytes = File.ReadAllBytes(dbPath);
                bytes[12] = 1; // HeaderFlags 低字节
                // 重算头部 CRC：模拟"写入方有意设置未知必选位"；不重算会被判成 Corrupted。
                uint crc = Crc32.Compute(bytes, 0, 28);
                BitConverter.GetBytes(crc).CopyTo(bytes, 28);
                File.WriteAllBytes(dbPath, bytes);

                var ex = Assert.Throws<UnsupportedVersionError>(() => CkvDb.Open(dbPath));
                Assert.Contains("flags", ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void ZeroHeaderFlagsStillOpen()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "ok.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("k", "v");
                }
                using var reopened = CkvDb.Open(dbPath);
                Assert.Equal("v", reopened.GetString("k"));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
