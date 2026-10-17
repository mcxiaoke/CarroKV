using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    // Adaptive compaction: reclaim a WAL whose live payload has collapsed, even
    // when the file never crosses CompactionThresholdBytes.
    //
    // Mirrors the Rust, Go, Kotlin and Dart suites. Three properties are pinned:
    //   1. dead-ratio trigger compacts an under-threshold log without losing keys;
    //   2. the 2 MB baseline keeps small logs from being rewritten for being dead;
    //   3. CompactionThresholdBytes == 0 still disables every automatic path.
    public class AdaptiveCompactionTests
    {
        private const long Baseline = 2L * 1024 * 1024;
        private const ulong NoSizeCompaction = ulong.MaxValue / 2;

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_adaptive_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static DbOptions AdaptiveOptions(ulong threshold) => new DbOptions
        {
            EngineMode = StorageEngineMode.AppendOnlyLog,
            SyncMode = SyncMode.Deferred,
            SyncIntervalMs = 3600000, // keep the background flush out of the way
            CompactionThresholdBytes = threshold,
        };

        private static void Fill(CkvDb db, string ns, int count, int valueLen)
        {
            string value = new string('v', valueLen);
            for (int i = 0; i < count; i++)
            {
                db.PutString(ns, $"k{i:00000}", value);
            }
        }

        [Fact]
        public void Test_Dead_Ratio_Compacts_Below_Size_Threshold()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "adaptive.db");
                long grown;
                using (var db = CkvDb.Open(path, AdaptiveOptions(NoSizeCompaction)))
                {
                    Fill(db, "ns", 300, 10 * 1024);
                    grown = new FileInfo(path).Length;
                    Assert.True(grown > Baseline, $"log should exceed the baseline, got {grown} bytes");

                    // The trigger fires as soon as the dead ratio crosses 50%, i.e.
                    // part-way through this loop, so the file ends up near live size.
                    for (int i = 0; i < 250; i++)
                    {
                        Assert.True(db.Delete("ns", $"k{i:00000}"));
                    }

                    long after = new FileInfo(path).Length;
                    Assert.True(after < Baseline,
                        $"dead-ratio compaction should have reclaimed the log: {grown} -> {after} bytes");
                }

                using (var reopened = CkvDb.Open(path, AdaptiveOptions(NoSizeCompaction)))
                {
                    Assert.Equal(50, reopened.Count("ns"));
                    for (int i = 250; i < 300; i++)
                    {
                        string? v = reopened.GetString("ns", $"k{i:00000}");
                        Assert.NotNull(v);
                        Assert.Equal(10 * 1024, v!.Length);
                    }
                    for (int i = 0; i < 250; i++)
                    {
                        Assert.Null(reopened.GetString("ns", $"k{i:00000}"));
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Test_Small_Logs_Are_Not_Rewritten_For_Being_Dead()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "small.db");
                using var db = CkvDb.Open(path, AdaptiveOptions(NoSizeCompaction));

                Fill(db, "ns", 50, 1024);
                long grown = new FileInfo(path).Length;
                Assert.True(grown < Baseline, $"log must stay under the baseline, got {grown} bytes");

                for (int i = 0; i < 45; i++)
                {
                    db.Delete("ns", $"k{i:00000}");
                }

                long after = new FileInfo(path).Length;
                Assert.True(after >= grown && after < Baseline,
                    $"a sub-baseline log must not be rewritten for being mostly dead: {grown} -> {after} bytes");
                Assert.Equal(5, db.Count("ns"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Test_Zero_Threshold_Disables_Dead_Ratio_Trigger()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "disabled.db");
                using var db = CkvDb.Open(path, AdaptiveOptions(0));

                Fill(db, "ns", 300, 10 * 1024);
                long grown = new FileInfo(path).Length;

                for (int i = 0; i < 270; i++)
                {
                    db.Delete("ns", $"k{i:00000}");
                }

                long after = new FileInfo(path).Length;
                Assert.True(after > grown && after > Baseline,
                    $"threshold 0 must disable automatic compaction entirely: {grown} -> {after} bytes");
                Assert.Equal(30, db.Count("ns"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
