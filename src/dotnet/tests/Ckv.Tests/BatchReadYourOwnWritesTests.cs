using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    // Read-your-own-writes inside Batch: staged writes win, staged tombstones
    // hide committed values, untouched keys fall through to the committed state.
    // Mirrors the Rust / Go / Kotlin / Dart suites.
    public class BatchReadYourOwnWritesTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_ryow_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Test_Staged_Writes_Win_And_Tombstones_Hide()
        {
            string dir = TempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "ryow.db"), new DbOptions());
                db.PutString("", "a", "committed");
                db.PutString("", "b", "committed-b");

                db.Batch(b =>
                {
                    // Untouched keys fall through to the committed state.
                    Assert.Equal("committed", b.GetString("", "a"));
                    Assert.Equal("committed-b", b.GetString("", "b"));

                    b.PutString("", "a", "staged");
                    Assert.Equal("staged", b.GetString("", "a"));

                    // The most recent staged operation wins.
                    b.PutString("", "a", "staged-2");
                    Assert.Equal("staged-2", b.GetString("", "a"));

                    // A staged delete hides the committed value.
                    b.Delete("", "b");
                    Assert.Null(b.GetString("", "b"));
                    Assert.Null(b.GetBytes("", "b"));

                    // A brand new key is visible too.
                    b.PutInt64("", "n", 42);
                    Assert.Equal(8, b.GetBytes("", "n")!.Length);

                    // Type rules are enforced inside staging as well.
                    Assert.Throws<TypeMismatchError>(() => b.GetString("", "n"));
                });

                Assert.Equal("staged-2", db.GetString("", "a"));
                Assert.Null(db.GetString("", "b"));
                Assert.Equal(42, db.GetInt64("", "n"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Test_Clear_And_Drop_Hide_Staged_Writes()
        {
            string dir = TempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "ryow_ns.db"), new DbOptions());
                db.PutString("users", "u1", "alice");

                db.Batch(b =>
                {
                    b.PutString("users", "u2", "bob");
                    Assert.Equal("bob", b.GetString("users", "u2"));

                    b.ClearNamespace("users");
                    Assert.Null(b.GetString("users", "u1"));
                    Assert.Null(b.GetString("users", "u2"));

                    // A put staged after the clear becomes visible again.
                    b.PutString("users", "u3", "carol");
                    Assert.Equal("carol", b.GetString("users", "u3"));

                    // Other namespaces are unaffected.
                    b.PutString("other", "k", "v");
                    Assert.Equal("v", b.GetString("other", "k"));

                    // Drop hides everything staged for that namespace.
                    b.DropNamespace("users");
                    Assert.Null(b.GetString("users", "u3"));
                });

                Assert.Equal(0, db.Count("users"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Test_Read_Your_Own_Writes_In_WAL_Mode()
        {
            string dir = TempDir();
            try
            {
                var options = new DbOptions { EngineMode = StorageEngineMode.AppendOnlyLog };
                using var db = CkvDb.Open(Path.Combine(dir, "ryow_wal.db"), options);
                db.PutString("", "counter", "1");

                db.Batch(b =>
                {
                    Assert.Equal("1", b.GetString("", "counter"));
                    b.PutString("", "counter", "2");
                    Assert.Equal("2", b.GetString("", "counter"));
                });

                Assert.Equal("2", db.GetString("", "counter"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
