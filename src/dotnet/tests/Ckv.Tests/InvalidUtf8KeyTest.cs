using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// API-SPEC §3.3.3 回归：非法 UTF-8 键在扫描接口里必须跳过，不得返回 U+FFFD 伪键。
    ///
    /// 写 API 的 key 是 <see cref="string"/>，非法键只能来自损坏或外部构造的文件；
    /// 这里直接把合法库中 1 字节的键改成 0xFF 并重算两级 CRC。
    /// </summary>
    public class InvalidUtf8KeyTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_utf8_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void ScansSkipKeysThatAreNotValidUtf8()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "bad_key.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("k", "v");
                }

                byte[] b = File.ReadAllBytes(dbPath);
                b[70] = 0xFF; // 32B 头 + 28B 帧头 + 10B 条目定长头；ns 空、key 长 1
                BitConverter.GetBytes(Crc32.Compute(b, 60, b.Length - 60)).CopyTo(b, 56);
                BitConverter.GetBytes(Crc32.Compute(b, 40, 20)).CopyTo(b, 36);
                File.WriteAllBytes(dbPath, b);

                using var reopened = CkvDb.Open(dbPath);
                Assert.Empty(reopened.Keys(""));
                Assert.Empty(reopened.IterPrefix(""));
                // §3.3.3 只约束 Keys / IterPrefix；ScanRange 按字节序枚举，v1.0 冻结语义
                // 是条目仍在列表里（见 ApiV1FreezeTest），这里只锁住条数。
                Assert.Single(reopened.ScanRange(""));
                Assert.Equal(1, reopened.Count(""));

                // 快照是另一套实现，同一份数据必须给出同一结论。
                using var snap = reopened.Snapshot();
                Assert.Empty(snap.Keys(""));
                Assert.Empty(snap.IterPrefix(""));
                Assert.Single(snap.ScanRange(""));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
