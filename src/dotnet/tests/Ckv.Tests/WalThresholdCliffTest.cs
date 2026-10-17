using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// FIX-21 回归：自动压实必须按“垃圾量”触发，而不是按文件绝对大小触发。
    ///
    /// 旧条件 `newFileSize >= CompactionThresholdBytes` 在压实后（文件恰等于存活大小）
    /// 会对每一次写都成立：6MB 存活时实测 26ms/写（38 ops/s），而正常摊销路径约 5000 ops/s。
    /// 断言全部基于文件大小增量，不使用时间阈值（CI runner 抖动会让时间断言不稳定）。
    /// 对应 Rust 的 tests/wal_threshold_cliff_test.rs。
    /// </summary>
    public class WalThresholdCliffTest
    {
        private const ulong Threshold = 4UL * 1024 * 1024;

        private static DbOptions Wal(ulong threshold)
        {
            return new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Deferred,
                SyncIntervalMs = 3_600_000,
                CompactionThresholdBytes = threshold
            };
        }

        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_cliff_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>关闭压实装载 6000×1KB（约 6MB 存活），保证重开时日志本身没有垃圾。</summary>
        private static void LoadFixture(string path)
        {
            string value = new string('v', 1000);
            using (var db = CkvDb.Open(path, Wal(0)))
            {
                for (int i = 0; i < 6000; i++)
                {
                    db.PutString("ns", $"k{i:000000}", value);
                }
                db.Flush();
            }
        }

        [Fact]
        public void OverwritesBelowThresholdDoNotRewriteTheWholeDatabase()
        {
            string dir = NewDir("grow");
            string path = Path.Combine(dir, "cliff.db");
            try
            {
                LoadFixture(path);
                string value = new string('v', 1000);

                using (var db = CkvDb.Open(path, Wal(Threshold)))
                {
                    long before = db.Stats().FileSize;
                    Assert.True(before >= 5_500_000, $"fixture unexpectedly small: {before} bytes");

                    // 50 次覆写 = 约 50KB 纯垃圾，比 4MB 阈值低 80 倍：日志应当单纯增长。
                    for (int i = 0; i < 50; i++)
                    {
                        db.PutString("ns", $"k{i:000000}", value);
                    }

                    long after = db.Stats().FileSize;
                    Assert.True(
                        after >= before + 40_000,
                        $"file did not grow ({before} -> {after}): every write is still " +
                        "triggering a full-database rewrite");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void GarbagePastTheThresholdStillCompacts()
        {
            string dir = NewDir("reclaim");
            string path = Path.Combine(dir, "reclaim.db");
            try
            {
                LoadFixture(path);
                string value = new string('v', 1000);

                using (var db = CkvDb.Open(path, Wal(Threshold)))
                {
                    long before = db.Stats().FileSize;

                    // 约 4.5MB 纯垃圾：必须被回收一次，文件回落到接近存活大小（约 6MB）。
                    for (int i = 0; i < 4500; i++)
                    {
                        db.PutString("ns", $"k{i:000000}", value);
                    }

                    long after = db.Stats().FileSize;
                    Assert.True(
                        after < before + 2_000_000,
                        $"compaction did not reclaim {before} -> {after} bytes of garbage");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
