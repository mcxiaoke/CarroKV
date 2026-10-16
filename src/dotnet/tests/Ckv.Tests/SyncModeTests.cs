using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    public class SyncModeTests
    {
        [Fact]
        public void Test_SyncMode_Manual_Flush_And_Close()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "manual.db");
                var opts = new DbOptions { SyncMode = SyncMode.Manual };

                // 1. Manual 模式写入
                using (var db = CkvDb.Open(p, opts))
                {
                    db.PutString("", "k1", "v1");
                    db.PutString("", "k2", "v2");

                    // 内存立即可读
                    Assert.Equal("v1", db.GetString("", "k1"));
                    Assert.Equal("v2", db.GetString("", "k2"));

                    // 显式 flush
                    db.Flush();
                    Assert.True(File.Exists(p));
                }

                // 重新打开验证
                using (var db2 = CkvDb.Open(p))
                {
                    Assert.Equal("v1", db2.GetString("", "k1"));
                    Assert.Equal("v2", db2.GetString("", "k2"));
                }

                // 2. 测试无需显式 flush，Close / Dispose 自动刷盘
                string p2 = Path.Combine(dir, "manual_close.db");
                using (var db3 = CkvDb.Open(p2, opts))
                {
                    db3.PutString("", "k_auto", "v_auto");
                } // 自动 Dispose

                using (var db4 = CkvDb.Open(p2))
                {
                    Assert.Equal("v_auto", db4.GetString("", "k_auto"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Test_SyncMode_Deferred_Background_Flush()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "deferred.db");
                var opts = new DbOptions
                {
                    SyncMode = SyncMode.Deferred,
                    SyncIntervalMs = 50
                };

                using (var db = CkvDb.Open(p, opts))
                {
                    db.PutString("", "key_async", "val_async");
                    Assert.Equal("val_async", db.GetString("", "key_async"));

                    // 等待后台定时器刷盘 (至少 120ms)
                    Thread.Sleep(150);

                    Assert.True(File.Exists(p));
                }

                using (var db2 = CkvDb.Open(p))
                {
                    Assert.Equal("val_async", db2.GetString("", "key_async"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Test_SyncMode_High_Frequency_Write_Coalescing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "coalesce.db");
                var opts = new DbOptions { SyncMode = SyncMode.Manual };

                using (var db = CkvDb.Open(p, opts))
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        db.PutInt64("", $"num_{i}", i);
                    }

                    Assert.Equal(1000, db.Count(""));

                    db.Flush();
                }

                using (var db2 = CkvDb.Open(p))
                {
                    Assert.Equal(1000, db2.Count(""));
                    Assert.Equal(999, db2.GetInt64("", "num_999"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
