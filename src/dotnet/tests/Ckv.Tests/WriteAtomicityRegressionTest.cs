using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 写路径原子性回归测试（B1 / B2 / B3）。
    ///
    /// 覆盖 2026-10-16 修复的三个同根因缺陷：写路径原本"先改内存 → 再 flush → flush
    /// 内部才做只读/锁/磁盘预检"，导致被拒绝的写仍会留下已变更的内存状态。
    ///
    ///   - B1：只读实例的写操作返回错误后，内存必须保持原值
    ///   - B2：磁盘被外部修改而预检失败时，内存必须保持原值
    ///   - B3：batch 中对不存在/已空的键与命名空间不得派发幻影 Watcher 事件
    ///
    /// Rust / Go 有同构测试，三者必须保持一致。
    /// </summary>
    public class WriteAtomicityRegressionTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_wa_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // B1：只读实例执行任何写操作都必须返回错误，且**不得污染内存**。
        [Fact]
        public void B1_ReadOnlyWriteMustNotMutateMemory()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "ro.db");
                using (var w = CkvDb.Open(dbPath))
                {
                    w.PutString("", "k", "old");
                }

                using var ro = CkvDb.Open(dbPath, new DbOptions { ReadOnly = true });

                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.PutString("", "k", "HACKED"));
                Assert.Equal("old", ro.GetString("", "k"));
                Assert.Equal(1, ro.Count(""));
                Assert.False(ro.ContainsKey("", "brand_new"));

                // 其它写入口同样不得污染
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.PutBytes("", "raw", new byte[] { 1 }));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.PutInt64("", "n", 1));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.PutFloat64("", "f", 1.0));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.PutBool("", "b", true));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.IncrementInt64("", "n", 1));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.Delete("", "k"));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.DropNamespace(""));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.ClearNamespace(""));
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() => ro.Compact());
                // §3.3（API v1.0 冻结）：只读写操作统一抛 LockedError，与 Rust/Go 一致
                Assert.Throws<LockedError>(() =>
                    ro.Batch(b => b.PutString("", "k", "HACKED")));

                Assert.Equal("old", ro.GetString("", "k"));
                Assert.Equal(1, ro.Count(""));

                // 重新打开验证磁盘也未被改动
                using var check = CkvDb.Open(dbPath);
                Assert.Equal("old", check.GetString("", "k"));
                Assert.Equal(1, check.Count(""));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // B2：磁盘被外部进程改写后，预检必须失败且不得污染内存。
        [Fact]
        public void B2_ExternalModificationMustNotMutateMemory()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "ext.db");
                string otherPath = Path.Combine(dir, "other.db");

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "a", "1");

                    // 用另一个数据库实例构造一份"外部写入"的合法文件
                    using (var o = CkvDb.Open(otherPath))
                    {
                        o.PutString("", "a", "1");
                        o.PutString("", "b", "2");
                        o.PutString("", "c", "3");
                    }

                    // 外部进程直接覆盖磁盘（CKV 平时不持有 .db 句柄，这完全可以发生）
                    File.Copy(otherPath, dbPath, true);

                    Assert.Throws<ConcurrentModificationError>(
                        () => db.PutString("", "x", "y"));

                    // 关键断言：写被拒绝，内存不得残留 "x"
                    Assert.False(db.ContainsKey("", "x"));
                    Assert.Equal(1, db.Count(""));
                    Assert.Null(db.GetString("", "x"));
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // B2 补充：锁被占用时同样不得污染内存。
        //
        // C9 起锁是"连接生命周期持有的内核锁"，因此"另一个连接正在使用数据库"表现为
        // **Open 被拒绝**（旧协议下表现为写被拒绝）：持锁者必须真的以 FileShare.None
        // 独占持有锁文件，而不是伪造锁文件内容。
        [Fact]
        public void B2_LockedMustNotMutateMemory()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "locked.db");
                using (var w = CkvDb.Open(dbPath))
                {
                    w.PutString("", "k", "old");
                }

                string lockPath = dbPath + ".lock";

                // 模拟活锁持有者：独占持有锁文件
                using var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                Assert.Throws<LockedError>(
                    () => CkvDb.Open(dbPath, new DbOptions { LockTimeoutMs = 300 }));

                holder.Dispose();

                // 持锁者离开后必须能正常打开并写入
                using var db = CkvDb.Open(dbPath);
                db.PutString("", "k", "HACKED");
                Assert.Equal("HACKED", db.GetString("", "k"));
                Assert.Equal(1, db.Count(""));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        private static void WriteU64(byte[] buf, int offset, ulong val)
        {
            for (int i = 0; i < 8; i++)
            {
                buf[offset + i] = (byte)(val >> (8 * i));
            }
        }

        // B3：batch 中对不存在的键/命名空间不得派发幻影事件；对真实生效的操作正常派发。
        [Fact]
        public void B3_BatchMustNotDispatchPhantomEvents()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "batch.db");
                using var db = CkvDb.Open(dbPath);

                int put = 0, del = 0, drop = 0, clear = 0;
                db.Watch(ev =>
                {
                    switch (ev.Op)
                    {
                        case KeyChangeOp.Put: put++; break;
                        case KeyChangeOp.Delete: del++; break;
                        case KeyChangeOp.DropNamespace: drop++; break;
                        case KeyChangeOp.Clear: clear++; break;
                    }
                });

                // 准备：一个含键的 ns、一个已清空的 ns、一个已删除的 ns
                db.PutString("live", "k", "v");
                db.PutString("emptied", "k", "v");
                Assert.True(db.ClearNamespace("emptied"));
                db.PutString("gone", "k", "v");
                Assert.True(db.DropNamespace("gone"));

                int bPut = put, bDel = del, bDrop = drop, bClear = clear;

                // batch 里全部是 no-op：不得产生任何事件
                db.Batch(b =>
                {
                    b.Delete("live", "does_not_exist");
                    b.ClearNamespace("emptied");   // 已空
                    b.DropNamespace("gone");       // 已删
                    b.ClearNamespace("never_existed");
                    b.DropNamespace("never_existed");
                    b.Delete("never_existed", "k");
                });

                Assert.Equal(bPut, put);
                Assert.Equal(bDel, del);
                Assert.Equal(bDrop, drop);
                Assert.Equal(bClear, clear);

                // 数据未被破坏
                Assert.True(db.ContainsKey("live", "k"));
                Assert.Equal(0, db.Count("emptied"));
                Assert.Equal(0, db.Count("gone"));

                // 真实生效的操作必须正常派发
                db.Batch(b => b.Delete("live", "k"));
                db.PutString("emptied", "k2", "v");
                db.Batch(b => b.ClearNamespace("emptied"));

                Assert.Equal(bDel + 1, del);
                Assert.Equal(bClear + 1, clear);
                Assert.Equal(bDrop, drop);
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // B4/B5：两代轮转必须是原子覆盖，不得出现"两代备份同时不存在"的窗口。
        // 通过连续写入后校验 .bak 始终等于上一代内容来锁定语义。
        [Fact]
        public void B4_BakGenerationMustAlwaysBeThePreviousContent()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "rot.db");
                string bakPath = dbPath + ".bak";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "gen", "1");
                    db.PutString("", "gen", "2");
                    db.PutString("", "gen", "3");
                }

                // .bak 必须存在且是上一代（"2"）
                Assert.True(File.Exists(bakPath), ".bak must exist after rotation");
                using (var bak = CkvDb.Open(bakPath))
                {
                    Assert.Equal("2", bak.GetString("", "gen"));
                }

                // 再写一次：.bak 应刷新为 "3"
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "gen", "4");
                }
                using (var bak2 = CkvDb.Open(bakPath))
                {
                    Assert.Equal("3", bak2.GetString("", "gen"));
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // B6：主库存在时，残留的 .db.tmp 必须被清理（旧实现只在主库缺失时检查）。
        [Fact]
        public void B6_StaleTmpMustBeCleanedWhenPrimaryExists()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "tmp.db");
                string tmpPath = dbPath + ".tmp";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");
                }

                // 伪造一个残留 tmp（崩溃场景）
                File.WriteAllBytes(tmpPath, new byte[32]);

                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.False(File.Exists(tmpPath),
                        "stale .db.tmp must be removed when the primary database is intact");
                    Assert.Equal("v", db.GetString("", "k"));
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }
    }
}
