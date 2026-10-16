using System;
using System.IO;
using System.Text;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 自动压实写路径与日志损坏分类的回归测试，与 Rust
    /// （tests/wal_auto_compaction_regression_test.rs）及 Go
    /// （wal_auto_compaction_regression_test.go）套件一一对应。
    ///
    /// 这些路径此前**零覆盖**：既有 WAL 测试要么使用默认 4MiB 阈值（永远不触发），
    /// 要么手工调用 Compact()。
    ///
    /// 覆盖缺陷：
    ///   - C1：压实发生在内存应用之前，序列化出的快照不含本次写入，导致刚追加的帧被覆盖丢失；
    ///   - C2：压实嵌套获取仍然被调用方持有的 `.db` 锁，永远无法成功且每次越线写阻塞到锁超时；
    ///   - C4：日志中间损坏被当作断电残尾静默截断，连带丢弃损坏点之后的全部完整帧。
    ///
    /// 每个断言都在**重开库之后校验全量键**，损坏通过翻转真实日志文件中的字节注入。
    /// </summary>
    public class WalAutoCompactionRegressionTest
    {
        private const ulong TinyThreshold = 512;
        private const ulong NoCompaction = ulong.MaxValue;

        private static DbOptions WalOptions(ulong threshold)
        {
            return new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Immediate,
                CompactionThresholdBytes = threshold
            };
        }

        private static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_wal_reg_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void FlipByteAt(string path, long offset)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            int original = fs.ReadByte();
            Assert.True(original >= 0, $"offset {offset} must be inside {path}");
            fs.Seek(offset, SeekOrigin.Begin);
            fs.WriteByte((byte)(original ^ 0xFF));
            fs.Flush(true);
        }

        /// <summary>单个 PutString 所产生的帧长度，实测取得，不依赖帧布局常量。</summary>
        private static long ProbeFrameLen(string dir)
        {
            string probe = Path.Combine(dir, "probe_frame_len.db");
            using (var db = CkvDb.Open(probe, WalOptions(NoCompaction)))
            {
                db.PutString("", "a", "1");
            }

            long total = new FileInfo(probe).Length;
            Assert.True(total > Constants.FileHeaderLen, $"probe database must contain one frame, got {total} bytes");
            return total - Constants.FileHeaderLen;
        }

        /// <summary>C1：跨越压实阈值的那次写必须与其余写入一并存活到重开之后。</summary>
        [Fact]
        public void TestAutoCompactionMustNotLoseWrites()
        {
            string dir = NewTempDir("t1");
            try
            {
                string dbPath = Path.Combine(dir, "t1.db");
                var options = WalOptions(TinyThreshold);
                const int keys = 300;

                using (var db = CkvDb.Open(dbPath, options))
                {
                    for (int i = 0; i < keys; i++)
                    {
                        string key = $"key{i:D4}";
                        db.PutString("", key, "value" + key);
                    }
                }

                // 防止空转通过：压缩期间的轮转会产出 `.bak`
                Assert.True(File.Exists(dbPath + ".bak"),
                    $"auto compaction never ran (threshold {TinyThreshold}) -- test is vacuous");

                var broken = new System.Collections.Generic.List<string>();
                using (var db = CkvDb.Open(dbPath, options))
                {
                    for (int i = 0; i < keys; i++)
                    {
                        string key = $"key{i:D4}";
                        string want = "value" + key;
                        string? got = db.GetString("", key);
                        if (got != want)
                        {
                            broken.Add($"{key}: want {want}, got {(got ?? "<null>")}");
                        }
                    }
                }

                Assert.True(broken.Count == 0,
                    $"{broken.Count} of {keys} keys did not survive auto compaction: " +
                    string.Join(", ", broken.GetRange(0, Math.Min(5, broken.Count))));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>C2：阈值触发的压实必须真正完成、回收空间，且不得让单次写阻塞一个锁超时。</summary>
        [Fact]
        public void TestAutoCompactionMustRunWithoutLockStalls()
        {
            string dir = NewTempDir("t2");
            try
            {
                string dbPath = Path.Combine(dir, "t2.db");
                var options = WalOptions(TinyThreshold);
                string value = new string('x', 64);

                long peak = 0;
                TimeSpan worst = TimeSpan.Zero;

                using (var db = CkvDb.Open(dbPath, options))
                {
                    for (int i = 0; i < 200; i++)
                    {
                        var started = System.Diagnostics.Stopwatch.StartNew();
                        db.PutString("", "hot", value);
                        started.Stop();
                        if (started.Elapsed > worst) worst = started.Elapsed;
                        long len = new FileInfo(dbPath).Length;
                        if (len > peak) peak = len;
                    }
                }

                // 200 次 64 字节值的写入追加量远超 8 倍阈值
                Assert.True(peak <= 8 * (long)TinyThreshold,
                    $"file grew to {peak} bytes: compaction never reclaimed space (threshold {TinyThreshold})");
                Assert.True(File.Exists(dbPath + ".bak"), "compaction never produced a `.bak` rotation");
                Assert.False(File.Exists(dbPath + ".tmp"), "compaction left a `.tmp` file behind");
                // 嵌套取锁会阻塞 LockTimeoutMs（默认 10s）
                Assert.True(worst < TimeSpan.FromSeconds(2),
                    $"a single write took {worst}: compaction is blocking the write path");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>C4：损坏记录之后仍有完整记录，这在只追加文件上不可能是断电残尾，必须报损坏。</summary>
        [Fact]
        public void TestMidLogDamageMustBeReportedAsCorruption()
        {
            string dir = NewTempDir("t4");
            try
            {
                string dbPath = Path.Combine(dir, "t4.db");
                var options = WalOptions(NoCompaction);
                long frameLen = ProbeFrameLen(dir);

                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "a", "1");
                    db.PutString("", "b", "2");
                    db.PutString("", "c", "3");
                }

                long healthyLen = Constants.FileHeaderLen + 3 * frameLen;
                // 翻转第二帧内的一个字节，第三帧保持完整
                FlipByteAt(dbPath, Constants.FileHeaderLen + frameLen + 8);

                Assert.Throws<CorruptedError>(() => CkvDb.Open(dbPath, options));

                // 被拒开后日志必须原样保留，供离线抢救
                Assert.Equal(healthyLen, new FileInfo(dbPath).Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>C4 反向对照：真正的残尾（文件在最后一帧内结束）必须继续自愈。</summary>
        [Fact]
        public void TestTornTailMustStillHeal()
        {
            string dir = NewTempDir("t5");
            try
            {
                string dbPath = Path.Combine(dir, "t5.db");
                var options = WalOptions(NoCompaction);
                long frameLen = ProbeFrameLen(dir);

                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "a", "1");
                    db.PutString("", "b", "2");
                    db.PutString("", "c", "3");
                }

                // 模拟写第三帧途中崩溃：把文件截短到不足一帧
                long healthyLen = new FileInfo(dbPath).Length;
                using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Write))
                {
                    fs.SetLength(healthyLen - frameLen / 2);
                    fs.Flush(true);
                }

                using (var db = CkvDb.Open(dbPath, options))
                {
                    Assert.Equal("1", db.GetString("", "a"));
                    Assert.Equal("2", db.GetString("", "b"));
                    Assert.Null(db.GetString("", "c")); // 残帧必须整体丢弃
                }

                Assert.Equal(Constants.FileHeaderLen + 2 * frameLen, new FileInfo(dbPath).Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
