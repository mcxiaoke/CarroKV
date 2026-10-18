using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 规范 §3.4 回归：AOL 尾部自愈截断前必须把整库留证到 <c>&lt;db&gt;.corrupt.&lt;unix_ms&gt;</c>。
    /// </summary>
    public class CorruptEvidenceTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void TornTailLeavesEvidenceBeforeTruncation()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "a.db");
                var opts = new DbOptions { EngineMode = StorageEngineMode.AppendOnlyLog };

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.PutString("k", "v");
                }

                byte[] garbage = Enumerable.Repeat((byte)0xFF, 10).ToArray();
                using (var fs = new FileStream(dbPath, FileMode.Append, FileAccess.Write))
                {
                    fs.Write(garbage, 0, garbage.Length);
                    fs.Flush(true);
                }
                byte[] damaged = File.ReadAllBytes(dbPath);

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    Assert.Equal("v", db.GetString("k"));
                }

                var backups = Directory.GetFiles(dir, "a.db.corrupt.*");
                Assert.Single(backups);
                Assert.Equal(damaged, File.ReadAllBytes(backups[0]));
                Assert.True(new FileInfo(dbPath).Length < damaged.Length,
                    "the live file must have been truncated");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
