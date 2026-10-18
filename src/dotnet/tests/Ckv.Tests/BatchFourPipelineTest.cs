using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ckv.Tests
{
    public class BatchFourPipelineTest : IDisposable
    {
        private readonly string _dir;

        public BatchFourPipelineTest()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ckv_batch4_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void P1_33_OnWarning_DoesNotDeadlock_WhenReadingDbInCallback()
        {
            string dbPath = Path.Combine(_dir, "warning_deadlock.db");
            var warned = new AutoResetEvent(false);
            bool readSuccess = false;
            CkvDb? dbInstance = null;

            var options = new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                CompactionThresholdBytes = 1, // 迫使压实
                StrictCompaction = false,
                OnWarning = (code, msg) =>
                {
                    // 在 warning 回调内部直接读取同一个 db 实例
                    // 如果持写锁回调，此处必死锁
                    if (code == "onCompactionFailed")
                    {
                        try
                        {
                            var val = dbInstance?.GetString("ns", "k");
                            if (val == "v")
                            {
                                readSuccess = true;
                            }
                        }
                        catch
                        {
                            // 忽略
                        }
                        warned.Set();
                    }
                }
            };

            // 占位 .tmp 文件制造压实失败
            string tmpPath = dbPath + ".tmp";
            Directory.CreateDirectory(tmpPath); // 目录占位导致打开 .tmp 抛异常

            try
            {
                using (var db = CkvDb.Open(dbPath, options))
                {
                    dbInstance = db;
                    db.PutString("ns", "k", "v");
                }
            }
            finally
            {
                try { Directory.Delete(tmpPath); } catch { }
            }

            Assert.True(warned.WaitOne(3000), "OnWarning must be fired without deadlock");
            Assert.True(readSuccess, "db read inside OnWarning must succeed outside write lock");
        }

        [Fact]
        public void P1_1_BatchCowDeduplication_OnlyClonesOncePerNamespace()
        {
            string dbPath = Path.Combine(_dir, "batch_cow.db");
            using var db = CkvDb.Open(dbPath);
            db.PutString("ns1", "init", "val");

            // 持有一个存活快照，激活 CoW
            using var snap = db.CreateSnapshot();

            // 批次内对同一命名空间写入 10,000 个键
            var sw = Stopwatch.StartNew();
            db.Batch(b =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    b.PutString("ns1", "k" + i, "v" + i);
                }
            });
            sw.Stop();

            // 如果每次循环都克隆整个 List，10000 次将拷贝 5000 万次对象；有去重则仅克隆 1 次
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Batch 10k with snapshot took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
            Assert.Equal("val", snap.GetString("ns1", "init"));
            Assert.Null(snap.GetString("ns1", "k0"));
            Assert.Equal("v0", db.GetString("ns1", "k0"));
        }

        [Fact]
        public void P0_7_P1_6_ImmediateCommitFailure_RollsBackMemoryAndPreventsDoubleIncrement()
        {
            string dbPath = Path.Combine(_dir, "immediate_rollback.db");
            var options = new DbOptions
            {
                EngineMode = StorageEngineMode.SnapshotAtomic,
                SyncMode = SyncMode.Immediate
            };

            using var db = CkvDb.Open(dbPath, options);
            db.PutInt64("counter", "hits", 10);

            // 制造落盘失败：把 <path>.tmp 创建为目录，导致写入抛出异常
            string tmpPath = dbPath + ".tmp";
            Directory.CreateDirectory(tmpPath);

            try
            {
                // 首次 increment 会在落盘阶段失败
                var ex = Assert.Throws<IoError>(() =>
                {
                    db.IncrementInt64("counter", "hits", 5);
                });
                Assert.NotNull(ex);

                // 验证内存回滚：hits 必须回滚为 10，绝不能污染为 15！
                Assert.Equal(10, db.GetInt64("counter", "hits"));

                // 移除占位障碍
                Directory.Delete(tmpPath);

                // 重试 increment：必须以干净的 10 为基准加 5 -> 15，绝不能变成双倍的 20！
                long retryVal = db.IncrementInt64("counter", "hits", 5);
                Assert.Equal(15, retryVal);
                Assert.Equal(15, db.GetInt64("counter", "hits"));
            }
            finally
            {
                try { if (Directory.Exists(tmpPath)) Directory.Delete(tmpPath); } catch { }
            }
        }

        [Fact]
        public async Task FlushAsync_And_BackupAsync_WorkCorrectly()
        {
            string dbPath = Path.Combine(_dir, "async_api.db");
            string backupPath = Path.Combine(_dir, "async_api.bak.export");

            using (var db = CkvDb.Open(dbPath))
            {
                db.PutString("ns", "key", "value");
                await db.FlushAsync();
                await db.BackupAsync(backupPath);
            }

            Assert.True(File.Exists(backupPath));
            using (var backupDb = CkvDb.Open(backupPath))
            {
                Assert.Equal("value", backupDb.GetString("ns", "key"));
            }
        }
    }
}
