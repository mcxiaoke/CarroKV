using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// FIX-01 回归：后台定时刷盘与显式 Flush 必须互斥，且后台快照陈旧时不得轮转。
    ///
    /// 两个缺陷都通过可观测后果覆盖：已显式落盘的写入在 Dispose + 重开之后消失。
    /// 大值用来把后台序列化窗口拉长到几十毫秒，从而在普通机器上稳定复现交错；
    /// 删掉大值是为了让显式 Flush 写出一个极小的快照，它必然先于后台的 15MB 写完成，
    /// 于是“陈旧快照后轮转”的时序是确定的，而不是靠运气。
    /// 对应 Rust 的 tests/deferred_flush_race_test.rs。
    /// </summary>
    public class DeferredFlushRaceTest
    {
        private static DbOptions Deferred(int intervalMs)
        {
            return new DbOptions
            {
                SyncMode = SyncMode.Deferred,
                SyncIntervalMs = (uint)intervalMs
            };
        }

        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_flushrace_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void ExplicitFlushDuringBackgroundFlushKeepsBothGenerations()
        {
            for (int round = 0; round < 5; round++)
            {
                string dir = NewDir("stale" + round);
                string path = Path.Combine(dir, "race.db");
                try
                {
                    string big = new string('x', 15 * 1024 * 1024);
                    using (var db = CkvDb.Open(path, Deferred(1)))
                    {
                        db.PutString("", "big", big);

                        // 让后台克隆快照并开始序列化。
                        Thread.Sleep(5);

                        // 大值删除后，显式 Flush 的快照极小，会先于后台的 15MB 写完成。
                        db.Delete("", "big");
                        db.PutString("", "w", "after");
                        db.Flush();

                        // 给陈旧的后台快照充足时间完成轮转。
                        Thread.Sleep(150);
                    }

                    using (var reopened = CkvDb.Open(path))
                    {
                        Assert.Equal("after", reopened.GetString("", "w"));
                        Assert.Null(reopened.GetString("", "big"));
                    }
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        [Fact]
        public void RapidWritesWithBackgroundFlushAreNotSilentlyDropped()
        {
            for (int round = 0; round < 3; round++)
            {
                string dir = NewDir("rapid" + round);
                string path = Path.Combine(dir, "rapid.db");
                try
                {
                    using (var db = CkvDb.Open(path, Deferred(1)))
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            db.PutString("ns", $"k{i:0000}", $"value-{i}");
                        }
                    }

                    using (var reopened = CkvDb.Open(path))
                    {
                        Assert.Equal(200, reopened.Count("ns"));
                        Assert.Equal("value-87", reopened.GetString("ns", "k0087"));
                        Assert.Equal("value-199", reopened.GetString("ns", "k0199"));
                    }
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }
    }
}
