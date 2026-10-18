using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// N-X1 回归：<c>IncrementInt64</c> 的 read-modify-write 必须整体处在同一个写临界区内。
    ///
    /// 批次四把"读值"与"写入"拆成两把锁（读锁读旧值 → 锁外算 newVal → 写锁存预计算值），
    /// 两个并发累加者会算出同一个结果，静默丢掉一次累加。规范
    /// KV-DB-DESIGN-SPEC.md §4.1.1 把该操作定义为 P0 原子累加器，因此两种引擎都要覆盖。
    ///
    /// Rust / Go / Kotlin / Dart 均在同构测试中覆盖同一语义。
    /// </summary>
    public class IncrementAtomicityTest
    {
        private const int Threads = 8;
        private const int OpsPerThread = 200;

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_inc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void RunConcurrentIncrements(DbOptions options)
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "inc.db");
                long expected = (long)Threads * OpsPerThread;

                using (var db = CkvDb.Open(dbPath, options))
                {
                    var errors = new List<Exception>();
                    var start = new ManualResetEventSlim(false);
                    var threads = new Thread[Threads];
                    for (int i = 0; i < Threads; i++)
                    {
                        threads[i] = new Thread(() =>
                        {
                            try
                            {
                                start.Wait();
                                for (int n = 0; n < OpsPerThread; n++)
                                {
                                    db.IncrementInt64("counter", 1);
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (errors) errors.Add(ex);
                            }
                        });
                        threads[i].Start();
                    }

                    start.Set();
                    foreach (var t in threads) t.Join();

                    Assert.Empty(errors);
                    Assert.Equal(expected, db.GetInt64("counter") ?? 0);
                }

                // 持久化复核：重开后累加结果必须一致（防止"内存对、磁盘错"）。
                using (var reopened = CkvDb.Open(dbPath))
                {
                    Assert.Equal(expected, reopened.GetInt64("counter") ?? 0);
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        [Fact]
        public void SnapshotAtomicImmediate_ConcurrentIncrementsAreAtomic()
        {
            RunConcurrentIncrements(
                new DbOptions
                {
                    EngineMode = StorageEngineMode.SnapshotAtomic,
                    SyncMode = SyncMode.Immediate
                });
        }

        [Fact]
        public void AppendOnlyLogImmediate_ConcurrentIncrementsAreAtomic()
        {
            // AOL 走同一段写锁内的 read-modify-write；用 Deferred 避免 1600 次逐写 fsync
            // 把用例拖到分钟级（落盘一致性由末尾 Flush 与重开复核覆盖）。
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "aol_inc.db");
                long expected = (long)Threads * OpsPerThread;
                var opts = new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    SyncMode = SyncMode.Deferred,
                    CompactionThresholdBytes = ulong.MaxValue
                };

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    var errors = new List<Exception>();
                    var start = new ManualResetEventSlim(false);
                    var threads = new Thread[Threads];
                    for (int i = 0; i < Threads; i++)
                    {
                        threads[i] = new Thread(() =>
                        {
                            try
                            {
                                start.Wait();
                                for (int n = 0; n < OpsPerThread; n++)
                                {
                                    db.IncrementInt64("counter", 1);
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (errors) errors.Add(ex);
                            }
                        });
                        threads[i].Start();
                    }

                    start.Set();
                    foreach (var t in threads) t.Join();

                    Assert.Empty(errors);
                    Assert.Equal(expected, db.GetInt64("counter") ?? 0);
                    db.Flush();
                }

                using (var reopened = CkvDb.Open(dbPath))
                {
                    Assert.Equal(expected, reopened.GetInt64("counter") ?? 0);
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // 类型不匹配必须在写锁内判定：并发把 string 写成 int64 时，要么累加成功，
        // 要么抛 TypeMismatchError，绝不能静默覆盖成 int64。
        [Fact]
        public void IncrementOnStringKey_ThrowsTypeMismatchAndKeepsValue()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "type.db");
                using var db = CkvDb.Open(dbPath);
                db.PutString("k", "text");

                Assert.Throws<TypeMismatchError>(() => db.IncrementInt64("k", 1));
                Assert.Equal("text", db.GetString("k"));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // AOL 模式下 applyMem 失败不得留下与内存不符的帧：重开后值必须还是原值。
        [Fact]
        public void AppendOnlyLog_FailedMutationLeavesNoStrayFrame()
        {
            string dir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(dir, "aol_type.db");
                var opts = new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    SyncMode = SyncMode.Immediate,
                    CompactionThresholdBytes = ulong.MaxValue
                };
                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.PutString("k", "text");
                    Assert.Throws<TypeMismatchError>(() => db.IncrementInt64("k", 1));
                }

                using (var reopened = CkvDb.Open(dbPath))
                {
                    Assert.Equal("text", reopened.GetString("k"));
                }
            }
            finally
            {
                DeleteTree(dir);
            }
        }
    }
}
