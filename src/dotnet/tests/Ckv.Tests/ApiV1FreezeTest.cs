using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// API v1.0 冻结契约回归测试（.NET）。
    /// 覆盖 docs/API-V1-FREEZE.md 中属于 .NET 的条目：6 个新 API +
    /// §3.1 错误命名、§3.3 只读错误类型、§3.4 Value 超限错误类型、
    /// §3.5 BatchWriter 长度校验、§3.6 Watcher 异常传播、§3.7 keys() 跳过非 UTF-8、
    /// §3.8 快照关闭后返回空。
    /// Rust / Go 有同构测试，三者必须一致。
    /// </summary>
    public class ApiV1FreezeTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_v1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // §2.1 Namespaces()：按 UTF-8 字节序、含空命名空间、默认 ns 为 ""
        [Fact]
        public void Namespaces_ListsAllIncludingEmptyAndDefault()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "ns.db"));
                db.PutString("zeta", "k", "v");
                db.PutString("alpha", "k", "v");
                db.PutString("", "k", "v");
                db.PutString("mid", "k", "v");
                Assert.True(db.ClearNamespace("mid")); // emptied but still present

                var ns = db.Namespaces();
                Assert.Equal(new[] { "", "alpha", "mid", "zeta" }, ns);

                // 快照同构
                using var snap = db.CreateSnapshot();
                Assert.Equal(ns, snap.Namespaces());

                Assert.True(db.DropNamespace("zeta"));
                Assert.Equal(new[] { "", "alpha", "mid" }, db.Namespaces());
            }
            finally { DeleteTree(dir); }
        }

        // §2.2 Unwatch()：显式注销；单纯丢弃句柄不注销
        [Fact]
        public void Unwatch_IsExplicit_AndDroppingTheHandleKeepsTheSubscription()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "watch.db"));
                int a = 0, b = 0;

                var ha = db.Watch(_ => a++);
                var hb = db.Watch(_ => b++);
                GC.KeepAlive(hb); // 刻意不 Dispose：订阅必须仍然有效

                db.PutString("", "k", "1");
                Assert.Equal(1, a);
                Assert.Equal(1, b);

                ha.Dispose();
                db.PutString("", "k", "2");
                Assert.Equal(1, a); // 已注销
                Assert.Equal(2, b);

                // 幂等
                ha.Dispose();
                db.PutString("", "k", "3");
                Assert.Equal(1, a);
            }
            finally { DeleteTree(dir); }
        }

        // §2.3 Flush()：Compact 的别名；只读模式下抛 LockedError
        [Fact]
        public void Flush_IsAnAliasOfCompact_AndRespectsReadOnly()
        {
            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "flush.db");
                using (var db = CkvDb.Open(p))
                {
                    db.PutString("", "k", "v");
                    db.Flush();
                    db.Compact();
                }

                using var ro = CkvDb.Open(p, new DbOptions { ReadOnly = true });
                Assert.Throws<LockedError>(() => ro.Flush());
                Assert.Throws<LockedError>(() => ro.Compact());
            }
            finally { DeleteTree(dir); }
        }

        // §2.4 FileSize() / Stats()
        [Fact]
        public void FileSizeAndStats_ReportLiveState()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "stats.db"));

                Assert.Equal(0, db.FileSize());
                var s0 = db.Stats();
                Assert.Equal(0, s0.FileSize);
                Assert.Equal(0, s0.NamespaceCount);
                Assert.Equal(0, s0.TotalEntries);
                Assert.Equal(0UL, s0.CurrentMaxTxId);

                db.PutString("a", "k1", "v");
                db.PutString("a", "k2", "v");
                db.PutString("b", "k3", "v");

                var s1 = db.Stats();
                Assert.True(s1.FileSize > 32);
                Assert.Equal(db.FileSize(), s1.FileSize);
                Assert.Equal(2, s1.NamespaceCount);
                Assert.Equal(3, s1.TotalEntries);
                Assert.True(s1.CurrentMaxTxId >= 1);

                // stats 不得改变状态（DbStats 是 class，逐字段比较）
                var s1b = db.Stats();
                Assert.Equal(s1.FileSize, s1b.FileSize);
                Assert.Equal(s1.NamespaceCount, s1b.NamespaceCount);
                Assert.Equal(s1.TotalEntries, s1b.TotalEntries);
                Assert.Equal(s1.CurrentMaxTxId, s1b.CurrentMaxTxId);

                // 空命名空间也计入
                db.PutString("empty", "x", "y");
                Assert.True(db.ClearNamespace("empty"));
                Assert.Equal(3, db.Stats().NamespaceCount);
                Assert.Equal(3, db.Stats().TotalEntries);
            }
            finally { DeleteTree(dir); }
        }

        // §2.5 Backup()：产出可独立打开的文件，且不动源库
        [Fact]
        public void Backup_ProducesAStandaloneOpenableFile()
        {
            string dir = NewTempDir();
            try
            {
                string src = Path.Combine(dir, "src.db");
                string dest = Path.Combine(dir, "backup.db");

                using (var db = CkvDb.Open(src))
                {
                    db.PutString("ns", "a", "1");
                    db.PutString("ns", "b", "2");
                    db.PutInt64("ns", "c", 42);

                    db.Backup(dest);
                }

                Assert.True(File.Exists(dest));
                Assert.False(File.Exists(dest + ".tmp"));

                using (var b = CkvDb.Open(dest))
                {
                    Assert.Equal("1", b.GetString("ns", "a"));
                    Assert.Equal("2", b.GetString("ns", "b"));
                    Assert.Equal(42L, b.GetInt64("ns", "c"));
                    Assert.Equal(3, b.Count("ns"));
                }

                using (var db = CkvDb.Open(src))
                {
                    Assert.Equal(3, db.Count("ns"));

                    // 覆盖已存在的目标
                    db.PutString("ns", "d", "4");
                    db.Backup(dest);
                }
                using (var b2 = CkvDb.Open(dest))
                {
                    Assert.Equal(4, b2.Count("ns"));
                }

                // 只读模式下允许备份
                using var ro = CkvDb.Open(src, new DbOptions { ReadOnly = true });
                string dest2 = Path.Combine(dir, "backup_ro.db");
                ro.Backup(dest2);
                using var b3 = CkvDb.Open(dest2);
                Assert.Equal(4, b3.Count("ns"));
            }
            finally { DeleteTree(dir); }
        }

        // §2.6 IterPrefix()：与 keys(prefix) 语义一致
        [Fact]
        public void IterPrefix_MatchesKeysWithPrefix()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "iter.db"));
                foreach (var k in new[] { "user:1", "user:2", "user:10", "order:1", "other" })
                    db.PutString("ns", k, "v");

                var entries = db.IterPrefix("ns", "user:");
                var keys = db.Keys("ns", "user:");
                Assert.Equal(keys, entries.Select(e => e.Key).ToList());

                Assert.Equal(5, db.IterPrefix("ns").Count);
                Assert.Empty(db.IterPrefix("nope", "x"));

                using var snap = db.CreateSnapshot();
                Assert.Equal(3, snap.IterPrefix("ns", "user:").Count);
            }
            finally { DeleteTree(dir); }
        }

        // §3.1 错误命名：新名为规范名，旧名为 [Obsolete] 别名且 catch 仍生效
        [Fact]
        public void ErrorNames_FollowSpec_AndObsoleteAliasesStillCatch()
        {
            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "err.db");
                using (var db = CkvDb.Open(p))
                {
                    db.PutString("", "s", "text");

                    // 新名（规范 §5.1.2）
                    Assert.Throws<TypeMismatchError>(() => db.GetInt64("", "s"));

                    // 旧名被设计为新名的**基类**，因此 catch (旧名) 能捕获新名实例，
                    // 既有代码无需改动即可继续工作（源码兼容一个弃用周期）。
#pragma warning disable CS0618
                    bool caughtByObsoleteName = false;
                    try { db.GetInt64("", "s"); }
                    catch (TypeMismatchException) { caughtByObsoleteName = true; }
#pragma warning restore CS0618
                    Assert.True(caughtByObsoleteName,
                        "catch (TypeMismatchException) must still compile and catch");

                    // DbError 是所有错误的基类（基类必须用 ThrowsAny：精确匹配只认具体类型）
                    Assert.ThrowsAny<DbError>(() => db.GetInt64("", "s"));
                }

                // 继承链：DbError → CkvException（旧基类）→ 旧具体名 → 新规范名
                Assert.True(typeof(DbError).IsAssignableFrom(typeof(TypeMismatchError)));
#pragma warning disable CS0618
                Assert.True(typeof(CkvException).IsAssignableFrom(typeof(TypeMismatchError)));
                Assert.True(typeof(CorruptedDatabaseException).IsAssignableFrom(typeof(CorruptedError)));
                Assert.True(typeof(DatabaseLockedException).IsAssignableFrom(typeof(LockedError)));
                Assert.True(typeof(TypeMismatchException).IsAssignableFrom(typeof(TypeMismatchError)));
#pragma warning restore CS0618
            }
            finally { DeleteTree(dir); }
        }

        // §3.4 Value 超限抛 KeyTooLargeError（旧实现抛通用 DbError）
        [Fact]
        public void ValueTooLarge_ThrowsKeyTooLargeError()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "big.db"));
                var big = new byte[16 * 1024 * 1024 + 1];
                Assert.Throws<KeyTooLargeError>(() => db.PutBytes("", "k", big));

                var bigKey = new string('k', 65536);
                Assert.Throws<KeyTooLargeError>(() => db.PutBytes("", bigKey, new byte[] { 1 }));
            }
            finally { DeleteTree(dir); }
        }

        // §3.5 BatchWriter 构造期即校验长度
        [Fact]
        public void BatchWriter_ValidatesLengthsAtConstruction()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "batch.db"));
                var big = new byte[16 * 1024 * 1024 + 1];

                Assert.Throws<KeyTooLargeError>(() =>
                    db.Batch(b => b.PutBytes("ns", "k", big)));
                Assert.Throws<KeyTooLargeError>(() =>
                    db.Batch(b => b.PutString("ns", new string('k', 65536), "v")));
                // 合法构造不受影响
                db.Batch(b => { b.PutString("ns", "k", "v"); b.Delete("ns", "k"); });
                Assert.Equal(0, db.Count("ns"));

            }
            finally { DeleteTree(dir); }
        }

        // §3.6 Watcher 回调异常不再被静默吞掉
        [Fact]
        public void WatcherCallbackException_Propagates()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "wex.db"));
                int secondCalled = 0;
                db.Watch(_ => throw new InvalidOperationException("boom"));
                db.Watch(_ => secondCalled++);

                var ex = Assert.Throws<InvalidOperationException>(() => db.PutString("", "k", "v"));
                Assert.Equal("boom", ex.Message);
                // 异常中断了后续监听器的派发（文档化语义）
                Assert.Equal(0, secondCalled);
            }
            finally { DeleteTree(dir); }
        }

        // §3.7 Keys() 跳过非 UTF-8 key
        [Fact]
        public void Keys_SkipsNonUtf8Keys()
        {
            string dir = NewTempDir();
            try
            {
                string p = Path.Combine(dir, "nonutf8.db");
                using (var db = CkvDb.Open(p))
                {
                    db.PutString("ns", "ok", "v");
                }

                // 手工在文件中注入一个非 UTF-8 key 的条目，再重新打开
                InjectNonUtf8Key(p);
                using var db2 = CkvDb.Open(p);
                Assert.Equal(new[] { "ok" }, db2.Keys("ns"));

                // ScanRange 按字节序扫描，不做 UTF-8 假设：默认命名空间下那条仍在
                Assert.Single(db2.ScanRange("ns"));           // 只有 "ok"
                Assert.Single(db2.ScanRange(""));             // 只有非 UTF-8 key
                Assert.Equal(2, db2.ScanRange("")[0].Key.Length + 0); // key 为 2 字节
            }
            finally { DeleteTree(dir); }
        }

        // 在既有 .db 中追加一个含非 UTF-8 key 的合法帧（OpCode=OpPut）
        private static void InjectNonUtf8Key(string dbPath)
        {
            byte[] raw = File.ReadAllBytes(dbPath);
            byte[] ns = Array.Empty<byte>();
            byte[] key = { 0xFF, 0xFE };
            byte[] val = { (byte)'x' };

            int entryLen = 10 + ns.Length + key.Length + val.Length;
            byte[] payload = new byte[entryLen];
            payload[0] = 0x01; // OpPut
            payload[1] = 0x00; // ValTypeBytes
            Array.Copy(BitConverter.GetBytes((ushort)ns.Length), 0, payload, 2, 2);
            Array.Copy(BitConverter.GetBytes((ushort)key.Length), 0, payload, 4, 2);
            Array.Copy(BitConverter.GetBytes((uint)val.Length), 0, payload, 6, 4);
            Array.Copy(key, 0, payload, 10 + ns.Length, key.Length);
            Array.Copy(val, 0, payload, 10 + ns.Length + key.Length, val.Length);

            uint pCrc = Ckv.Crc32.Compute(payload);

            byte[] frame = new byte[28 + payload.Length];
            Array.Copy(BitConverter.GetBytes(0x4D52464Bu), 0, frame, 0, 4); // FrameMagic LE

            byte[] tail20 = new byte[20];
            Array.Copy(BitConverter.GetBytes(2UL), 0, tail20, 0, 8);        // TxID = 2
            Array.Copy(BitConverter.GetBytes(1u), 0, tail20, 8, 4);          // OpCount = 1
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, tail20, 12, 4);
            Array.Copy(BitConverter.GetBytes(pCrc), 0, tail20, 16, 4);
            uint fHdrCrc = Ckv.Crc32.Compute(tail20);

            Array.Copy(BitConverter.GetBytes(fHdrCrc), 0, frame, 4, 4);
            Array.Copy(tail20, 0, frame, 8, 20);
            Array.Copy(payload, 0, frame, 28, payload.Length);

            File.WriteAllBytes(dbPath, raw.Concat(frame).ToArray());
        }

        // §3.8 快照关闭后读操作返回「存在」之外的空结果，不抛错
        [Fact]
        public void SnapshotDisposed_ReadsReturnEmpty_NotThrow()
        {
            string dir = NewTempDir();
            try
            {
                using var db = CkvDb.Open(Path.Combine(dir, "snap.db"));
                db.PutString("ns", "k", "v");
                var snap = db.CreateSnapshot();
                snap.Dispose();

                Assert.Equal(0, snap.Count("ns"));
                Assert.Null(snap.GetString("ns", "k"));
                Assert.Null(snap.GetBytes("ns", "k"));
                Assert.False(snap.ContainsKey("ns", "k"));
                Assert.Empty(snap.Keys("ns"));
                Assert.Empty(snap.ScanRange("ns"));
                Assert.Empty(snap.Namespaces());
                Assert.Empty(snap.IterPrefix("ns"));
            }
            finally { DeleteTree(dir); }
        }
    }
}
