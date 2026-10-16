using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ckv.Tests
{
    public class SnapshotTest
    {
        [Fact]
        public void TestSnapshotPointInTimeIsolation()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_snap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "snap_iso.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    // 1. Initial data
                    db.PutString("users", "user:1", "Alice");
                    db.PutInt64("users", "user:1:score", 100);
                    db.PutString("users", "user:2", "Bob");
                    db.PutString("orders", "order:100", "Pending");

                    using (var snap1 = db.CreateSnapshot())
                    {
                        Assert.True(snap1.TxId > 0);

                        // 2. Extensively mutate primary DB
                        db.PutString("users", "user:1", "Alice_Updated");
                        db.IncrementInt64("users", "user:1:score", 899); // 100 + 899 = 999
                        db.Delete("users", "user:2");
                        db.PutString("users", "user:3", "Charlie");
                        db.DropNamespace("orders");

                        // Primary DB state
                        Assert.Equal("Alice_Updated", db.GetString("users", "user:1"));
                        Assert.Equal(999, db.GetInt64("users", "user:1:score"));
                        Assert.Null(db.GetString("users", "user:2"));
                        Assert.Equal("Charlie", db.GetString("users", "user:3"));
                        Assert.Equal(0, db.Count("orders"));

                        // 3. Verify snap1 remains completely frozen in time
                        Assert.Equal("Alice", snap1.GetString("users", "user:1"));
                        Assert.Equal(100, snap1.GetInt64("users", "user:1:score"));
                        Assert.Equal("Bob", snap1.GetString("users", "user:2"));
                        Assert.Null(snap1.GetString("users", "user:3"));
                        Assert.True(snap1.ContainsKey("users", "user:2"));
                        Assert.False(snap1.ContainsKey("users", "user:3"));
                        Assert.Equal(3, snap1.Count("users"));

                        Assert.Equal(1, snap1.Count("orders"));
                        Assert.Equal("Pending", snap1.GetString("orders", "order:100"));

                        // 4. Create snap2 and verify it sees latest mutations
                        using (var snap2 = db.CreateSnapshot())
                        {
                            Assert.True(snap2.TxId > snap1.TxId);
                            Assert.Equal("Alice_Updated", snap2.GetString("users", "user:1"));
                            Assert.Equal("Charlie", snap2.GetString("users", "user:3"));
                            Assert.Equal(0, snap2.Count("orders"));
                        }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestSnapshotConcurrentReadersAndWriters()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_snap_concur_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "concur.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    for (int i = 0; i < 100; i++)
                    {
                        db.PutString("data", $"key:{i:D4}", $"val:{i:D4}");
                    }

                    using (var snap = db.CreateSnapshot())
                    {
                        Assert.Equal(100, snap.Count("data"));

                        int stopFlag = 0;
                        var writerThread = new Thread(() =>
                        {
                            int counter = 0;
                            while (Volatile.Read(ref stopFlag) == 0)
                            {
                                string k = $"key:{(counter % 100):D4}";
                                db.PutString("data", k, $"mutated:{counter}");
                                counter++;
                            }
                        });
                        writerThread.Start();

                        // Reader repeatedly scans snapshot
                        for (int round = 0; round < 50; round++)
                        {
                            var range = snap.ScanRange("data", "key:0010", "key:0020");
                            Assert.Equal(10, range.Count);
                            for (int i = 0; i < range.Count; i++)
                            {
                                string expectedKey = $"key:{(10 + i):D4}";
                                string expectedVal = $"val:{(10 + i):D4}";
                                Assert.Equal(expectedKey, range[i].Key);
                                Assert.Equal(expectedVal, range[i].AsString());
                            }
                        }

                        Volatile.Write(ref stopFlag, 1);
                        writerThread.Join();

                        Assert.Equal(100, snap.Count("data"));
                        Assert.Equal("val:0000", snap.GetString("data", "key:0000"));
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestSnapshotTypeSafetyAndDisposal()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_snap_types_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "types.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutBytes("ns", "k_bytes", new byte[] { 1, 2, 3 });
                    db.PutString("ns", "k_str", "hello");
                    db.PutInt64("ns", "k_i64", -999);
                    db.PutFloat64("ns", "k_f64", 3.14159);
                    db.PutBool("ns", "k_bool", true);

                    var snap = db.CreateSnapshot();
                    Assert.Equal(new byte[] { 1, 2, 3 }, snap.GetBytes("ns", "k_bytes"));
                    Assert.Equal("hello", snap.GetString("ns", "k_str"));
                    Assert.Equal(-999, snap.GetInt64("ns", "k_i64"));
                    Assert.Equal(3.14159, snap.GetFloat64("ns", "k_f64"));
                    Assert.True(snap.GetBool("ns", "k_bool"));

                    // Type mismatch intercept
                    Assert.Throws<TypeMismatchError>(() => snap.GetString("ns", "k_i64"));
                    Assert.Throws<TypeMismatchError>(() => snap.GetInt64("ns", "k_str"));

                    // §3.8（API v1.0 冻结）：快照关闭后读操作返回「不存在」，不抛错。
                    // 旧实现抛 ObjectDisposedException；Rust（无 Close）/ Go（返回空）
                    // 本就不抛，三语言现已统一。
                    snap.Dispose();
                    Assert.Equal(0, snap.Count("ns"));
                    Assert.Null(snap.GetString("ns", "k_str"));
                    Assert.Null(snap.GetBytes("ns", "k_bytes"));
                    Assert.False(snap.ContainsKey("ns", "k_str"));
                    Assert.Empty(snap.Keys("ns"));
                    Assert.Empty(snap.ScanRange("ns"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
