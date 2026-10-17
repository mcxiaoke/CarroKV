using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// FIX-22 回归：Dispose 之后继续写必须失败。
    ///
    /// Dispose 已释放跨进程连接锁（C9），此时写入会落到一个可能已被另一个进程打开的库上，
    /// 正是锁要阻止的双写者场景。Rust（.closed）/ Go / Kotlin / Dart 均已拒绝；
    /// .NET 修复前会静默写成功。
    /// </summary>
    public class ClosedHandleTest
    {
        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_closed_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void WritesAfterDisposeAreRefusedAndTheFileIsUntouched()
        {
            string dir = NewDir("dotnet");
            string path = Path.Combine(dir, "closed.db");
            try
            {
                var db = CkvDb.Open(path);
                db.PutString("", "before", "1");
                db.Dispose();

                long before = new FileInfo(path).Length;

                Assert.Throws<LockedError>(() => db.PutString("", "after", "2"));
                Assert.Throws<LockedError>(() => db.Delete("", "before"));
                Assert.Throws<LockedError>(() => db.Flush());
                Assert.Throws<LockedError>(() => db.Compact());

                Assert.Equal(before, new FileInfo(path).Length);

                // Dispose is idempotent.
                db.Dispose();

                using (var reopened = CkvDb.Open(path))
                {
                    Assert.Null(reopened.GetString("", "after"));
                    Assert.Equal("1", reopened.GetString("", "before"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
