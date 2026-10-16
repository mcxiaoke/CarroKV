using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// C3：`Batch { Put(ns, k); ClearNamespace(ns); }` 原先会在重启后让 `k` 复活。
    ///
    /// 构建日志条目时，ClearNamespace 是按**批处理运行之前**的内存状态展开的；
    /// 同一批中更早 Put 的键此时还不在该状态里，于是它的 OpPut 已经进了日志，
    /// 却没有对应的删除条目。内存侧随后被真的清空，因此分歧只在**重放日志**时暴露，
    /// 也就是重开之后——而这正是既有 batch 测试从未做过的事。
    ///
    /// 同一场景在两种引擎模式下都跑一遍：两种模式必须对同一写序列给出相同结果，
    /// 只有追加日志模式会在此处分歧。
    /// </summary>
    public class BatchClearNamespaceRegressionTest
    {
        private static DbOptions OptionsFor(StorageEngineMode mode) => new DbOptions
        {
            EngineMode = mode,
            SyncMode = SyncMode.Immediate
        };

        private static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_c3_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>同批内先 Put 再 ClearNamespace：内存中与重放后都必须不存在。</summary>
        private static void AssertPutThenClearDoesNotResurrect(StorageEngineMode mode)
        {
            string dir = NewTempDir("putclear");
            try
            {
                string dbPath = Path.Combine(dir, "c3_put_clear.db");
                var opts = OptionsFor(mode);

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.PutString("ns", "old", "old_value");
                    db.PutString("other", "keep", "keep_value");
                }

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.Batch(b =>
                    {
                        b.PutString("ns", "fresh", "fresh_value");
                        b.ClearNamespace("ns");
                        // 另一个命名空间不得被 clear 影响
                        b.PutString("other", "keep2", "keep2_value");
                    });

                    Assert.Null(db.GetString("ns", "fresh"));
                    Assert.Null(db.GetString("ns", "old"));
                }

                // 重放磁盘日志：这里是原先键复活的位置
                using (var db = CkvDb.Open(dbPath, opts))
                {
                    Assert.Null(db.GetString("ns", "fresh"));
                    Assert.Null(db.GetString("ns", "old"));
                    Assert.Equal("keep_value", db.GetString("other", "keep"));
                    Assert.Equal("keep2_value", db.GetString("other", "keep2"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>镜像用例：clear 之后 Put 的键必须存活，否则"修复"就成了丢弃真实写入。</summary>
        private static void AssertClearThenPutSurvives(StorageEngineMode mode)
        {
            string dir = NewTempDir("clearput");
            try
            {
                string dbPath = Path.Combine(dir, "c3_clear_put.db");
                var opts = OptionsFor(mode);

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.PutString("ns", "old", "old_value");
                }

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    db.Batch(b =>
                    {
                        b.ClearNamespace("ns");
                        b.PutString("ns", "fresh", "fresh_value");
                    });
                }

                using (var db = CkvDb.Open(dbPath, opts))
                {
                    Assert.Null(db.GetString("ns", "old"));
                    Assert.Equal("fresh_value", db.GetString("ns", "fresh"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void BatchPutThenClearMustNotResurrectInWalMode()
        {
            AssertPutThenClearDoesNotResurrect(StorageEngineMode.AppendOnlyLog);
        }

        [Fact]
        public void BatchPutThenClearMustNotResurrectInSnapshotMode()
        {
            AssertPutThenClearDoesNotResurrect(StorageEngineMode.SnapshotAtomic);
        }

        [Fact]
        public void BatchClearThenPutMustSurviveReplayInWalMode()
        {
            AssertClearThenPutSurvives(StorageEngineMode.AppendOnlyLog);
        }

        [Fact]
        public void BatchClearThenPutMustSurviveReplayInSnapshotMode()
        {
            AssertClearThenPutSurvives(StorageEngineMode.SnapshotAtomic);
        }
    }
}
