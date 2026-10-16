using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 连接锁的回归测试（C9，门槛 T10）。
    ///
    /// 自 C9 起锁是 <c>&lt;db&gt;.lock</c> 上的**内核锁**（Windows 上是排他/共享共享模式的句柄，
    /// .NET 在 Unix 上把它映射为 flock），由 <c>Open</c> 获取、持有整个连接生命周期：
    ///
    ///   - 锁文件的*内容*不再参与协议：旧的 32B <c>PID | UUID | CreateTimeMs</c> 记录、
    ///     基于时钟的判活、以及整套"窃取陈旧锁"的花招全部移除，因此遗留或畸形的锁文件
    ///     必须零代价；
    ///   - 活着的持有者永远不会被误判为死的（那曾经让同一份库上出现两个写者）；
    ///   - 崩溃的持有者靠进程死亡释放锁，因为锁归内核所有。
    ///
    /// 可写连接取排他锁，只读连接取共享锁：读者之间互不排斥，只排斥写者
    /// （bbolt 的 <c>flock(db, !readOnly)</c>）。
    ///
    /// T10 跨进程的一半（另一个进程持锁后被杀掉）由 Rust 套件覆盖；这里通过锁原语本身
    /// （平台相关代码）+ <c>Open</c> 的拒绝路径验证同一组性质。
    /// </summary>
    public class LockProtocolTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static DbOptions ShortTimeout()
        {
            return new DbOptions { LockTimeoutMs = 150 };
        }

        [Fact]
        public void LockFileContentsAreIrrelevant()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "garbage_lock.db");
                string lockPath = dbPath + ".lock";

                byte[] garbage = System.Text.Encoding.ASCII.GetBytes(
                    "not a 32-byte lock record: no pid, no uuid, no timestamp");
                File.WriteAllBytes(lockPath, garbage);

                var sw = Stopwatch.StartNew();
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");
                }
                Assert.True(sw.ElapsedMilliseconds < 2000,
                    $"the open took {sw.ElapsedMilliseconds}ms: the lock file's contents are still being interpreted");

                // 标记文件既不删除也不改写（删掉它会让第二个连接锁住一个新的文件句柄，
                // 而第一个仍锁着旧的，双方都不会报错）
                Assert.True(File.Exists(lockPath), "the lock file must not be unlinked");
                Assert.Equal(garbage, File.ReadAllBytes(lockPath));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ExclusiveLockExcludesWritersAndReaders()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "exclusive.db");
                string lockPath = dbPath + ".lock";

                using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Throws<LockedError>(() => CkvDb.Open(dbPath, ShortTimeout()));

                    var readerOptions = new DbOptions { LockTimeoutMs = 150, ReadOnly = true };
                    Assert.Throws<LockedError>(() => CkvDb.Open(dbPath, readerOptions));
                }

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");
                    Assert.Equal("v", db.GetString("", "k"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void SharedLockAllowsReadersButNotWriters()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "shared.db");
                string lockPath = dbPath + ".lock";

                using (var seed = CkvDb.Open(dbPath))
                {
                    seed.PutString("", "k", "v");
                }

                // 只读连接：access=GENERIC_READ + share=FILE_SHARE_READ
                using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
                {
                    using (var reader = CkvDb.Open(dbPath, new DbOptions { ReadOnly = true }))
                    {
                        // 共享锁之间共存：读者能打开并读到最后落盘的值
                        Assert.Equal("v", reader.GetString("", "k"));
                    }

                    Assert.Throws<LockedError>(() => CkvDb.Open(dbPath, ShortTimeout()));
                }

                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.Equal("v", db.GetString("", "k"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ConnectionHoldsTheLockUntilDispose()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "lifetime.db");
                string lockPath = dbPath + ".lock";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");

                    // 实例存活期间，竞争者拿不到锁
                    try
                    {
                        using var probe = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        Assert.Fail("the connection must hold the lock for its whole lifetime");
                    }
                    catch (IOException)
                    {
                        // 预期：被连接的排他锁挡住
                    }
                }

                // Dispose 之后锁被释放（锁文件保留，不需要任何删除动作）
                using (var probe = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}