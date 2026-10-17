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

        [Fact]
        public void ReadsAfterDisposeThrowObjectDisposedException()
        {
            string dir = NewDir("reads");
            string path = Path.Combine(dir, "closed_reads.db");
            try
            {
                var db = CkvDb.Open(path);
                db.PutString("ns1", "str", "val");
                db.PutInt64("ns1", "num", 42);
                db.PutBool("ns1", "flag", true);
                db.Dispose();

                // 条目 15（P1-12 统一关闭后读语义）：已关闭句柄读操作必须抛 ObjectDisposedException，
                // 绝不能静默返回 null / 0，避免业务误判为"数据不存在"。
                Assert.Throws<ObjectDisposedException>(() => db.GetString("ns1", "str"));
                Assert.Throws<ObjectDisposedException>(() => db.GetBytes("ns1", "str"));
                Assert.Throws<ObjectDisposedException>(() => db.GetInt64("ns1", "num"));
                Assert.Throws<ObjectDisposedException>(() => db.GetFloat64("ns1", "num"));
                Assert.Throws<ObjectDisposedException>(() => db.GetBool("ns1", "flag"));
                Assert.Throws<ObjectDisposedException>(() => db.ContainsKey("ns1", "str"));
                Assert.Throws<ObjectDisposedException>(() => db.Count("ns1"));
                Assert.Throws<ObjectDisposedException>(() => db.IterPrefix("ns1"));
                Assert.Throws<ObjectDisposedException>(() => db.Namespaces());
                Assert.Throws<ObjectDisposedException>(() => db.Keys("ns1"));
                Assert.Throws<ObjectDisposedException>(() => db.ScanRange("ns1"));
                Assert.Throws<ObjectDisposedException>(() => db.Stats());
                Assert.Throws<ObjectDisposedException>(() => db.CreateSnapshot());
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void BackupFile_StaticMethod_ExportsSafelyWithoutAffectingActiveInstance()
        {
            string dir = NewDir("backup");
            string srcPath = Path.Combine(dir, "source.db");
            string bakPath1 = Path.Combine(dir, "backup1.db");
            string bakPath2 = Path.Combine(dir, "backup2.db");
            try
            {
                // 1. 活跃实例存在时调用 BackupFile：复用实例导出，绝不关停当前实例
                using (var live = CkvDb.Open(srcPath))
                {
                    live.PutString("", "live_key", "live_val");

                    CkvDb.BackupFile(srcPath, bakPath1);

                    // 验证备份成功导出
                    using (var bakDb1 = CkvDb.Open(bakPath1))
                    {
                        Assert.Equal("live_val", bakDb1.GetString("", "live_key"));
                    }

                    // 验证 live 依然可用且未被 Dispose
                    Assert.False(live.IsClosed);
                    live.PutString("", "live_key2", "live_val2");
                    Assert.Equal("live_val2", live.GetString("", "live_key2"));
                }

                // 2. 无活跃实例存在时调用 BackupFile：独占打开导出后自闭环释放
                CkvDb.BackupFile(srcPath, bakPath2);

                using (var bakDb2 = CkvDb.Open(bakPath2))
                {
                    Assert.Equal("live_val", bakDb2.GetString("", "live_key"));
                    Assert.Equal("live_val2", bakDb2.GetString("", "live_key2"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
