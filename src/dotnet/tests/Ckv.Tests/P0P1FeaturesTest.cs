using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    public class P0P1FeaturesTest
    {
        [Fact]
        public void TestContainsKeyAndCount()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "p0_p1.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.False(db.ContainsKey("", "k1"));
                    Assert.Equal(0, db.Count(""));

                    db.PutString("", "k1", "v1");
                    db.PutInt64("", "k2", 42);
                    db.PutString("other", "k3", "v3");

                    Assert.True(db.ContainsKey("", "k1"));
                    Assert.True(db.ContainsKey("", "k2"));
                    Assert.False(db.ContainsKey("", "k3"));
                    Assert.Equal(2, db.Count(""));

                    Assert.True(db.ContainsKey("other", "k3"));
                    Assert.Equal(1, db.Count("other"));

                    db.Delete("", "k1");
                    Assert.False(db.ContainsKey("", "k1"));
                    Assert.Equal(1, db.Count(""));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestClearNamespace()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "clear_ns.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.False(db.ClearNamespace("ns1"));

                    db.PutString("ns1", "a", "1");
                    db.PutString("ns1", "b", "2");
                    db.PutString("ns2", "c", "3");

                    Assert.Equal(2, db.Count("ns1"));
                    Assert.Equal(1, db.Count("ns2"));

                    Assert.True(db.ClearNamespace("ns1"));
                    Assert.Equal(0, db.Count("ns1"));
                    Assert.Equal(1, db.Count("ns2"));

                    // Repeating clear on empty namespace returns false
                    Assert.False(db.ClearNamespace("ns1"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestIncrementInt64()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "inc.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    // 1. Initial increment initializes nonexistent key
                    long val = db.IncrementInt64("counter", "hits", 10);
                    Assert.Equal(10, val);
                    Assert.Equal(10, db.GetInt64("counter", "hits"));

                    // 2. Sequential increments
                    val = db.IncrementInt64("counter", "hits", 5);
                    Assert.Equal(15, val);
                    val = db.IncrementInt64("counter", "hits", -20);
                    Assert.Equal(-5, val);
                    Assert.Equal(-5, db.GetInt64("counter", "hits"));

                    // 3. Type mismatch intercept
                    db.PutString("counter", "text_key", "hello");
                    Assert.Throws<TypeMismatchException>(() => db.IncrementInt64("counter", "text_key", 1));

                    db.PutFloat64("counter", "f_key", 3.14);
                    Assert.Throws<TypeMismatchException>(() => db.IncrementInt64("counter", "f_key", 1));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestWatchEventsAndDeadlockSafety()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "watch.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    var events = new List<KeyChangeEvent>();
                    db.Watch(ev =>
                    {
                        events.Add(ev);
                        // Re-entrant reading inside callback to ensure no deadlocks occur
                        if (ev.Op == KeyChangeOp.Put)
                        {
                            _ = db.ContainsKey(ev.Namespace, ev.Key);
                        }
                    });

                    // 1. Put
                    db.PutString("ns1", "k1", "v1");
                    Assert.Single(events);
                    Assert.Equal(KeyChangeOp.Put, events[0].Op);
                    Assert.Equal("ns1", events[0].Namespace);
                    Assert.Equal("k1", events[0].Key);

                    // 2. Increment
                    db.IncrementInt64("ns1", "hits", 1);
                    Assert.Equal(2, events.Count);
                    Assert.Equal(KeyChangeOp.Put, events[1].Op);
                    Assert.Equal("ns1", events[1].Namespace);
                    Assert.Equal("hits", events[1].Key);

                    // 3. Delete
                    db.Delete("ns1", "k1");
                    Assert.Equal(3, events.Count);
                    Assert.Equal(KeyChangeOp.Delete, events[2].Op);
                    Assert.Equal("k1", events[2].Key);

                    // 4. Batch
                    db.Batch(b =>
                    {
                        b.PutString("ns1", "batch_k", "batch_v");
                        b.Delete("ns1", "hits");
                        b.ClearNamespace("ns1");
                    });

                    Assert.Equal(6, events.Count);
                    Assert.Equal(KeyChangeOp.Put, events[3].Op);
                    Assert.Equal("batch_k", events[3].Key);
                    Assert.Equal(KeyChangeOp.Delete, events[4].Op);
                    Assert.Equal("hits", events[4].Key);
                    Assert.Equal(KeyChangeOp.Clear, events[5].Op);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
