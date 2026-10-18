using System;
using System.IO;
using System.Text;
using Xunit;

namespace Ckv.Tests
{
    public class ConvenienceApiTest : IDisposable
    {
        private readonly string _testDir;

        public ConvenienceApiTest()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "ckv_test_convenience_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
        }

        [Fact]
        public void TestDbDefaultNamespaceOverloadsAndIndexer()
        {
            string dbPath = Path.Combine(_testDir, "test_db_convenience.db");
            using var db = CkvDb.Open(dbPath);

            // 1. 基本 CRUD 重载
            db.PutString("strKey", "strVal");
            Assert.Equal("strVal", db.GetString("strKey"));
            Assert.Equal("strVal", db.GetString("", "strKey")); // 验证归属于默认命名空间

            db.PutBytes("byteKey", Encoding.UTF8.GetBytes("byteVal"));
            Assert.Equal("byteVal", Encoding.UTF8.GetString(db.GetBytes("byteKey")!));

            db.PutInt64("intKey", 42L);
            Assert.Equal(42L, db.GetInt64("intKey"));
            Assert.Equal(50L, db.IncrementInt64("intKey", 8));
            Assert.Equal(50L, db.GetInt64("intKey"));

            db.PutFloat64("floatKey", 3.14159);
            Assert.Equal(3.14159, db.GetFloat64("floatKey")!.Value, precision: 5);

            db.PutBool("boolKey", true);
            Assert.True(db.GetBool("boolKey"));

            Assert.True(db.ContainsKey("strKey"));
            Assert.True(db.ContainsKey("intKey"));
            Assert.False(db.ContainsKey("non_existing"));

            Assert.Equal(5, db.Count());
            var keys = db.Keys();
            Assert.Contains("strKey", keys);
            Assert.Contains("intKey", keys);

            // 2. 索引器语法糖测试
            db["indexedKey"] = "indexedValue";
            Assert.Equal("indexedValue", db["indexedKey"]);
            Assert.Equal("indexedValue", db.GetString("indexedKey"));

            db["indexedKey"] = null; // 删除
            Assert.Null(db["indexedKey"]);
            Assert.False(db.ContainsKey("indexedKey"));

            // 3. 迭代与扫描
            var allPrefixItems = db.IterPrefix();
            Assert.NotEmpty(allPrefixItems);

            var prefixItems = db.Default.IterPrefix("str");
            Assert.Single(prefixItems);
            Assert.Equal("strKey", prefixItems[0].Key);

            var allRangeItems = db.ScanRange();
            Assert.NotEmpty(allRangeItems);

            var rangeItems = db.Default.ScanRange("a", "z");
            Assert.NotEmpty(rangeItems);

            // 4. Delete & Clear
            Assert.True(db.Delete("strKey"));
            Assert.False(db.ContainsKey("strKey"));

            db.ClearNamespace();
            Assert.Equal(0, db.Count());
        }

        [Fact]
        public void TestNamespaceScopeFacade()
        {
            string dbPath = Path.Combine(_testDir, "test_db_scope.db");
            using var db = CkvDb.Open(dbPath);

            var userScope = db.NS("users");
            userScope["user:1"] = "Alice";
            userScope.PutInt64("age:1", 30);

            Assert.Equal("Alice", userScope["user:1"]);
            Assert.Equal("Alice", db.GetString("users", "user:1"));
            Assert.Equal(30, userScope.GetInt64("age:1"));
            Assert.Equal(2, userScope.Count());

            var defaultScope = db.Default;
            defaultScope["site"] = "github";
            Assert.Equal("github", db.GetString("", "site"));
            Assert.Equal("github", db["site"]);

            Assert.True(userScope.Delete("user:1"));
            Assert.False(userScope.ContainsKey("user:1"));
        }

        [Fact]
        public void TestSnapshotAndBatchConvenience()
        {
            string dbPath = Path.Combine(_testDir, "test_db_snap_batch.db");
            using var db = CkvDb.Open(dbPath);

            // BatchWriter 便利接口
            db.Batch(b =>
            {
                b["batchKey"] = "batchVal";
                b.PutInt64("batchInt", 999);
                b.NS("audit")["action"] = "login";

                // 读己所写
                Assert.Equal("batchVal", b["batchKey"]);
                Assert.Equal("login", b.NS("audit")["action"]);
            });

            Assert.Equal("batchVal", db["batchKey"]);
            Assert.Equal(999, db.GetInt64("batchInt"));
            Assert.Equal("login", db.GetString("audit", "action"));

            // Snapshot 便利接口
            using var snap = db.Snapshot();
            Assert.Equal("batchVal", snap["batchKey"]);
            Assert.Equal(999, snap.GetInt64("batchInt"));
            Assert.True(snap.ContainsKey("batchKey"));
            Assert.Equal("login", snap.NS("audit")["action"]);
            Assert.Equal(2, snap.Count());
        }
    }
}
