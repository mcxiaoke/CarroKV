using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Ckv.Tests
{
    public class IndustrialSuiteTest
    {
        // 1. fjall & rocksdb 范式：同一 Batch 内对同一 Key 连续多次覆写、删除并重新赋值
        [Fact]
        public void TestBatchSameKeyOverwritesAndTombstones()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_ind_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "batch_overwrite.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("ns1", "target_key", "initial_v0");
                    db.PutString("ns1", "stay_key", "stay_v0");

                    db.Batch(b =>
                    {
                        b.PutString("ns1", "target_key", "temp_v1");
                        b.PutString("ns1", "target_key", "temp_v2");
                        b.Delete("ns1", "target_key");
                        b.PutString("ns1", "target_key", "final_v3");

                        // 跨命名空间
                        b.PutString("ns2", "target_key", "ns2_v1");
                        b.Delete("ns1", "stay_key");
                    });

                    Assert.Equal("final_v3", db.GetString("ns1", "target_key"));
                    Assert.Null(db.GetString("ns1", "stay_key"));
                    Assert.Equal("ns2_v1", db.GetString("ns2", "target_key"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // 2. hive_ce 范式：极端 Unicode 字符（带 ZWJ 复合 Emoji、多语言文本）、空 Key、空 Value、大 Blob (512KB)
        [Fact]
        public void TestUnicodeComplexAndZeroByteKeys()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_uni_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "unicode_niche.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    // 空字符串作为 Key
                    db.PutString("", "", "empty_key_val");
                    Assert.Equal("empty_key_val", db.GetString("", ""));

                    // 空字节作为 Value
                    db.PutBytes("", "empty_val_key", Array.Empty<byte>());
                    var emptyVal = db.GetBytes("", "empty_val_key");
                    Assert.NotNull(emptyVal);
                    Assert.Empty(emptyVal!);

                    // 复杂的各种语言文本与带零宽连字 (ZWJ) 复合 Emoji
                    var cases = new (string k, string v)[]
                    {
                        ("zwj_family", "👨‍👨‍👧‍👦"),
                        ("arabic", " ﻬ ﻭ ﻮ ﻯ ﻰ ﻱ"),
                        ("malayalam", "അ ആ ഇ "),
                        ("katakana", "ｩ ｪ ｫ ｬ ｭ ｮ ｯ ｰ "),
                        ("special_math", "∀x ∈ ℝ: x² ≥ 0"),
                        ("surrogate_pairs", "𠮷野家"),
                    };

                    foreach (var tc in cases)
                    {
                        db.PutString("i18n", tc.k, tc.v);
                        Assert.Equal(tc.v, db.GetString("i18n", tc.k));
                    }

                    // 512KB 大 Blob
                    byte[] largeBlob = new byte[512 * 1024];
                    for (int i = 0; i < largeBlob.Length; i++) largeBlob[i] = 0xAB;
                    db.PutBytes("blob_ns", "large_blob_key", largeBlob);
                    byte[]? readBlob = db.GetBytes("blob_ns", "large_blob_key");
                    Assert.NotNull(readBlob);
                    Assert.True(largeBlob.AsSpan().SequenceEqual(readBlob!.AsSpan()));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // 3. rocksdb 列族 / redb 表范式：命名空间完全隔离与 Drop 重建
        [Fact]
        public void TestNamespaceIsolationAndDropRecreate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_ns_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "ns_isolation.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "shared_id", "default_val");
                    db.PutString("default", "shared_id", "default_val_alias");
                    db.PutString("ns_alpha", "shared_id", "alpha_val");
                    db.PutString("ns_beta", "shared_id", "beta_val");

                    Assert.Equal("default_val_alias", db.GetString("", "shared_id"));
                    Assert.Equal("default_val_alias", db.GetString("default", "shared_id"));

                    Assert.Equal("alpha_val", db.GetString("ns_alpha", "shared_id"));
                    Assert.Equal("beta_val", db.GetString("ns_beta", "shared_id"));

                    // Drop ns_alpha
                    Assert.True(db.DropNamespace("ns_alpha"));
                    Assert.Null(db.GetString("ns_alpha", "shared_id"));

                    // 其他命名空间完好
                    Assert.Equal("default_val_alias", db.GetString("", "shared_id"));
                    Assert.Equal("beta_val", db.GetString("ns_beta", "shared_id"));

                    // 重建 ns_alpha
                    db.PutString("ns_alpha", "shared_id", "recreated_alpha_val");
                    Assert.Equal("recreated_alpha_val", db.GetString("ns_alpha", "shared_id"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // 4. rust-rocksdb 范式：范围扫描极限边界与 Seek 逻辑
        [Fact]
        public void TestScanRangeAdvancedBoundaries()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_scan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "scan_bounds.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    string[] keys = new[] { "apple", "banana", "cherry", "date", "fig", "grape" };
                    foreach (var k in keys) db.PutString("fruit", k, "ok");

                    // 1. start > end 空扫描
                    var emptyScan = db.ScanRange("fruit", "fig", "banana");
                    Assert.Empty(emptyScan);

                    // 2. start 和 end 完全一致
                    var exactEmpty = db.ScanRange("fruit", "cherry", "cherry");
                    Assert.Empty(exactEmpty);

                    // 3. 不存在的 start 定位 (seek_ge)
                    var seekScan = db.ScanRange("fruit", "blueberry", "date");
                    Assert.Single(seekScan);
                    Assert.Equal("cherry", seekScan[0].Key);

                    // 4. 超出范围的 end
                    var allFromCherry = db.ScanRange("fruit", "cherry", "zebra");
                    var expected = new[] { "cherry", "date", "fig", "grape" };
                    Assert.Equal(expected, allFromCherry.Select(e => e.Key));

                    // 5. 空命名空间
                    var nonExistent = db.ScanRange("non_existent", null, null);
                    Assert.Empty(nonExistent);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // 5. redb abort_tests 范式：事务批处理异常中断与暂存区丢弃
        [Fact]
        public void TestBatchAbortStagingRollback()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_abort_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "batch_abort.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("state", "status", "clean_v1");

                    // 在 Batch 内部抛出异常
                    Assert.Throws<InvalidOperationException>(() =>
                    {
                        db.Batch(b =>
                        {
                            b.PutString("state", "status", "dirty_v2");
                            b.PutString("state", "new_leak", "should_not_exist");
                            b.Delete("state", "status");
                            throw new InvalidOperationException("simulated business error in batch");
                        });
                    });

                    // 数据库状态保持完好
                    Assert.Equal("clean_v1", db.GetString("state", "status"));
                    Assert.Null(db.GetString("state", "new_leak"));

                    // 后续写入正常
                    db.PutString("state", "status", "clean_v2");
                    Assert.Equal("clean_v2", db.GetString("state", "status"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
