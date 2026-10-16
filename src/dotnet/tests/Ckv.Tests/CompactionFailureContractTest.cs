using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// D2：压实失败契约。
    ///
    /// 自动压实运行时，本次变更已经 append + fsync 落盘；此时向调用方报错等于让它重试一个
    /// 已经成功的写，而 <c>Increment</c> 是 read-modify-write，重试会重复累加 delta。
    /// 因此压实失败改为：`onCompactionFailed` 告警 + 置待重试 + 退避重试（`Close`/`Dispose`
    /// 再兜底一次）；开启 `StrictCompaction` 才向上抛出。
    ///
    /// 失败用**真实文件系统条件**注入：`CommitFlush` 轮转前会写 `&lt;db&gt;.tmp`，
    /// 在该路径放一个**非空目录**即可让它打开失败，而对 `&lt;db&gt;` 的追加写入照常成功。
    /// </summary>
    public class CompactionFailureContractTest
    {
        private const ulong TinyThreshold = 512;

        private static DbOptions Options(Action onCompactionFailed)
        {
            return new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Immediate,
                CompactionThresholdBytes = TinyThreshold,
                OnWarning = (code, _) =>
                {
                    if (code == "onCompactionFailed") onCompactionFailed();
                }
            };
        }

        private static string TmpPath(string dbPath) => dbPath + ".tmp";

        private static void BlockCompaction(string dbPath)
        {
            string tmp = TmpPath(dbPath);
            Directory.CreateDirectory(tmp);
            // 目录必须非空：恢复流程会尝试清理残留 `.tmp`，空目录会被删掉。
            File.WriteAllText(Path.Combine(tmp, "keep"), "x");
        }

        private static void UnblockCompaction(string dbPath)
        {
            string tmp = TmpPath(dbPath);
            try { File.Delete(Path.Combine(tmp, "keep")); } catch { }
            try { Directory.Delete(tmp); } catch { }
        }

        private static void Seed(string dbPath, DbOptions opts)
        {
            using var db = CkvDb.Open(dbPath, opts);
            db.PutString("", "seed", "seed_value");
        }

        private static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_d2_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>D2-1：写仍返回成功、失败有告警、期间写入的数据重开后完整。</summary>
        [Fact]
        public void CompactionFailureMustNotFailTheWrite()
        {
            string dir = NewTempDir("write");
            try
            {
                string dbPath = Path.Combine(dir, "t6a.db");
                int failures = 0;
                var opts = Options(() => failures++);

                Seed(dbPath, opts);
                BlockCompaction(dbPath);

                const int keys = 30;
                using (var db = CkvDb.Open(dbPath, opts))
                {
                    for (int i = 0; i < keys; i++)
                    {
                        try
                        {
                            db.PutString("", $"k{i:D3}", $"v{i:D3}");
                        }
                        catch (Exception ex)
                        {
                            Assert.Fail($"a failed compaction must not fail the write (D2): {ex.Message}");
                        }
                    }

                    Assert.True(failures >= 1, "the compaction failure must be reported through onCompactionFailed");
                    Assert.False(File.Exists(dbPath + ".bak"),
                        "the test must actually have blocked compaction");
                }

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    Assert.Equal("seed_value", db.GetString("", "seed"));
                    for (int i = 0; i < keys; i++)
                    {
                        Assert.Equal($"v{i:D3}", db.GetString("", $"k{i:D3}"));
                    }
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>D2-4：退避节流 —— 窗口内不重试，跨过窗口恰好一次。</summary>
        [Fact]
        public void CompactionBackoffMustThrottleRetries()
        {
            string dir = NewTempDir("backoff");
            try
            {
                string dbPath = Path.Combine(dir, "t6b.db");
                int failures = 0;
                var opts = Options(() => failures++);

                Seed(dbPath, opts);
                BlockCompaction(dbPath);

                using var db = CkvDb.Open(dbPath, opts);

                int written = 0;
                while (failures == 0)
                {
                    db.PutString("", $"a{written:D3}", "v");
                    written++;
                    Assert.True(written < 500, "the compaction failure was never triggered");
                }
                int first = failures;
                Assert.Equal(1, first);

                // 首次失败后的退避窗口为 64 次写
                for (int i = 0; i < 30; i++)
                {
                    db.PutString("", $"b{written:D3}", "v");
                    written++;
                }
                Assert.Equal(first, failures);

                for (int i = 0; i < 40; i++)
                {
                    db.PutString("", $"c{written:D3}", "v");
                    written++;
                }
                Assert.Equal(first + 1, failures);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>D2-3：Dispose 兜底重试，使瞬时故障不跟随实例整个生命周期。</summary>
        [Fact]
        public void DisposeMustRetryAPendingCompaction()
        {
            string dir = NewTempDir("close");
            try
            {
                string dbPath = Path.Combine(dir, "t6c.db");
                int failures = 0;
                var opts = Options(() => failures++);

                Seed(dbPath, opts);
                BlockCompaction(dbPath);

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    for (int i = 0; i < 30; i++)
                    {
                        db.PutString("", $"k{i:D3}", "v");
                    }
                    Assert.True(failures >= 1);
                    Assert.False(File.Exists(dbPath + ".bak"));

                    UnblockCompaction(dbPath);
                }

                Assert.True(File.Exists(dbPath + ".bak"),
                    "Dispose must retry a pending compaction");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// D2-5：StrictCompaction 向上抛错；而报错的那次写依然已持久化——这正是默认不上抛的原因。
        /// </summary>
        [Fact]
        public void StrictCompactionMustSurfaceTheError()
        {
            string dir = NewTempDir("strict");
            try
            {
                string dbPath = Path.Combine(dir, "t6d.db");
                int failures = 0;
                var opts = Options(() => failures++);
                opts.StrictCompaction = true;

                Seed(dbPath, opts);
                BlockCompaction(dbPath);

                string failingKey = "";
                using (var db = CkvDb.Open(dbPath, opts))
                {
                    for (int i = 0; i < 30; i++)
                    {
                        string key = $"k{i:D3}";
                        try
                        {
                            db.PutString("", key, "v");
                        }
                        catch
                        {
                            failingKey = key;
                            break;
                        }
                    }
                }

                Assert.NotEqual("", failingKey);
                UnblockCompaction(dbPath);

                // 错误只表示"压实失败"，不表示"写丢失"：帧早已 append + fsync。
                using var reopened = CkvDb.Open(dbPath, opts);
                Assert.Equal("v", reopened.GetString("", failingKey));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
