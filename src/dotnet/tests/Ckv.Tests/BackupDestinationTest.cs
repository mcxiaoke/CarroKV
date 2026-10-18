using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// M3-11 回归：<c>Backup</c> 不得写到数据库自身或其 <c>.tmp</c>/<c>.bak</c>。
    /// </summary>
    public class BackupDestinationTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_bk_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void BackupRejectsTheDatabaseFileAndItsSiblings()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "self.db");
                using var db = CkvDb.Open(dbPath);
                db.PutString("k", "v");

                Assert.Throws<IoError>(() => db.Backup(dbPath));
                Assert.Throws<IoError>(() => db.Backup(dbPath + ".tmp"));
                Assert.Throws<IoError>(() => db.Backup(dbPath + ".bak"));

                string dest = Path.Combine(dir, "copy.db");
                db.Backup(dest);
                using var copy = CkvDb.Open(dest);
                Assert.Equal("v", copy.GetString("k"));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
