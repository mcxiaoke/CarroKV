using System;
using System.IO;
using System.Text;
using System.Threading;
using Ckv;
using Xunit;

namespace Ckv.Tests
{
    public class WalModeTests : IDisposable
    {
        private readonly string _tempDir;

        public WalModeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ckv_wal_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void TestWalAppendAndPersistence()
        {
            string dbPath = Path.Combine(_tempDir, "wal_test.db");
            var opts = new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Immediate
            };

            using (var db = CkvDb.Open(dbPath, opts))
            {
                db.PutString("", "key1", "val1");
                db.PutString("", "key2", "val2");
                db.PutString("", "key1", "val1_updated");
                db.Delete("", "key2");
            }

            // Reopen and verify final state
            using (var db = CkvDb.Open(dbPath, opts))
            {
                Assert.Equal("val1_updated", db.GetString("", "key1"));
                Assert.Null(db.GetString("", "key2"));
            }
        }

        [Fact]
        public void TestWalTailTruncationRecovery()
        {
            string dbPath = Path.Combine(_tempDir, "wal_trunc_test.db");
            int warningCount = 0;

            var opts = new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Immediate,
                OnWarning = (code, msg) =>
                {
                    if (code == "onTailTruncated")
                    {
                        Interlocked.Increment(ref warningCount);
                    }
                }
            };

            // Step 1: Write valid entries
            using (var db = CkvDb.Open(dbPath, opts))
            {
                db.PutString("", "healthy_k1", "healthy_v1");
                db.PutString("", "healthy_k2", "healthy_v2");
            }

            long healthyLen = new FileInfo(dbPath).Length;
            Assert.True(healthyLen > 32);

            // Step 2: Inject corrupt partial trailing bytes (simulating power failure)
            using (var fs = new FileStream(dbPath, FileMode.Append, FileAccess.Write))
            {
                byte[] garbage = Encoding.UTF8.GetBytes("CORRUPT_TRAIL!!");
                fs.Write(garbage, 0, garbage.Length);
                fs.Flush();
            }

            long corruptedLen = new FileInfo(dbPath).Length;
            Assert.Equal(healthyLen + 15, corruptedLen);

            // Step 3: Reopen database - should auto-truncate the corrupted tail and heal
            using (var db = CkvDb.Open(dbPath, opts))
            {
                Assert.True(warningCount > 0);
                Assert.Equal("healthy_v1", db.GetString("", "healthy_k1"));
                Assert.Equal("healthy_v2", db.GetString("", "healthy_k2"));

                // Verify file was physically truncated back to healthy size
                long healedLen = new FileInfo(dbPath).Length;
                Assert.Equal(healthyLen, healedLen);

                // Verify we can continue writing new entries after truncation
                db.PutString("", "healthy_k3", "healthy_v3");
                Assert.Equal("healthy_v3", db.GetString("", "healthy_k3"));
            }
        }

        [Fact]
        public void TestWalCompactionAndBakRotation()
        {
            string dbPath = Path.Combine(_tempDir, "wal_compact_test.db");
            var opts = new DbOptions
            {
                EngineMode = StorageEngineMode.AppendOnlyLog,
                SyncMode = SyncMode.Immediate
            };

            using (var db = CkvDb.Open(dbPath, opts))
            {
                // Repeated overwrites generate append frames
                for (int i = 0; i < 100; i++)
                {
                    db.PutString("", "heavy_key", $"value_generation_{i}");
                }

                long bloatedSize = new FileInfo(dbPath).Length;
                Assert.True(bloatedSize > 3000);

                // Compact
                db.Compact();

                long compactedSize = new FileInfo(dbPath).Length;
                Assert.True(compactedSize < bloatedSize);

                // .bak should exist and equal bloatedSize
                string bakPath = dbPath + ".bak";
                Assert.True(File.Exists(bakPath));
                Assert.Equal(bloatedSize, new FileInfo(bakPath).Length);

                // Verify data integrity after compaction
                Assert.Equal("value_generation_99", db.GetString("", "heavy_key"));
            }
        }
    }
}
