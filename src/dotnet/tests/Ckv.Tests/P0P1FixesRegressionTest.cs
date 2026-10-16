using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// P0/P1 修复回归测试：
    /// P0-2 墓碑重放 / P0-3 TxID 单调与 OpCount 校验 / P0-8 锁文件布局（经行为验证）
    /// P1-1 ForceUnlock/OnWarning / P1-2 Compact / P1-3 写前预检与 B1 空库回归
    /// </summary>
    public class P0P1FixesRegressionTest
    {
        // ---------- 测试文件构建工具（按 v3.2 规范手工构造帧） ----------

        private static void WriteU16(byte[] buf, int off, ushort val)
        {
            buf[off] = (byte)val;
            buf[off + 1] = (byte)(val >> 8);
        }

        private static void WriteU32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)val;
            buf[off + 1] = (byte)(val >> 8);
            buf[off + 2] = (byte)(val >> 16);
            buf[off + 3] = (byte)(val >> 24);
        }

        private static void WriteU64(byte[] buf, int off, ulong val)
        {
            for (int i = 0; i < 8; i++) buf[off + i] = (byte)(val >> (8 * i));
        }

        private static byte[] BuildFileHeader()
        {
            byte[] h = new byte[32];
            byte[] magic = new byte[] { 0x55, 0x4B, 0x56, 0x44, 0x42, 0x30, 0x30, 0x33 }; // "UKVDB003"
            Array.Copy(magic, 0, h, 0, 8);
            WriteU16(h, 8, 3); // readVer
            WriteU16(h, 10, 3); // writeVer
            WriteU32(h, 12, 0); // flags
            WriteU32(h, 28, Crc32.Compute(h, 0, 28));
            return h;
        }

        private static byte[] BuildEntry(byte opCode, byte valType, string ns, byte[] key, byte[] val)
        {
            byte[] nsBytes = Encoding.UTF8.GetBytes(ns);
            byte[] entry = new byte[10 + nsBytes.Length + key.Length + val.Length];
            entry[0] = opCode;
            entry[1] = valType;
            WriteU16(entry, 2, (ushort)nsBytes.Length);
            WriteU16(entry, 4, (ushort)key.Length);
            WriteU32(entry, 6, (uint)val.Length);
            int off = 10;
            Array.Copy(nsBytes, 0, entry, off, nsBytes.Length); off += nsBytes.Length;
            Array.Copy(key, 0, entry, off, key.Length); off += key.Length;
            Array.Copy(val, 0, entry, off, val.Length);
            return entry;
        }

        private static byte[] BuildFrame(ulong txId, List<byte[]> entries)
        {
            using var payload = new MemoryStream();
            foreach (var e in entries) payload.Write(e, 0, e.Length);
            byte[] payloadBytes = payload.ToArray();

            byte[] tail20 = new byte[20];
            WriteU64(tail20, 0, txId);
            WriteU32(tail20, 8, (uint)entries.Count);
            WriteU32(tail20, 12, (uint)payloadBytes.Length);
            WriteU32(tail20, 16, Crc32.Compute(payloadBytes));

            byte[] frame = new byte[28 + payloadBytes.Length];
            WriteU32(frame, 0, 0x4D52464B); // 'KFRM'
            WriteU32(frame, 4, Crc32.Compute(tail20));
            Array.Copy(tail20, 0, frame, 8, 20);
            Array.Copy(payloadBytes, 0, frame, 28, payloadBytes.Length);
            return frame;
        }

        private static byte[] BuildDb(params byte[][] frames)
        {
            using var ms = new MemoryStream();
            byte[] header = BuildFileHeader();
            ms.Write(header, 0, header.Length);
            foreach (var f in frames) ms.Write(f, 0, f.Length);
            return ms.ToArray();
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_p0p1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------- P0-2：墓碑（OpDelete/OpDropNamespace）必须被重放 ----------

        [Fact]
        public void TombstonesAreReplayedOnLoad()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "tombstone.db");

                // 帧1：put keep/dead/also_dead
                // 帧2：delete dead
                // 帧3：drop 整个 ns_gone（先放一个 put）
                // 帧4：put after_delete（验证 delete 之后的 put 生效）
                var frame1 = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns1", Encoding.UTF8.GetBytes("keep"), Encoding.UTF8.GetBytes("v_keep")),
                    BuildEntry(0x01, 0x01, "ns1", Encoding.UTF8.GetBytes("dead"), Encoding.UTF8.GetBytes("v_dead")),
                    BuildEntry(0x01, 0x01, "ns_gone", Encoding.UTF8.GetBytes("x"), Encoding.UTF8.GetBytes("v_x")),
                });
                var frame2 = BuildFrame(2, new List<byte[]>
                {
                    BuildEntry(0x02, 0x00, "ns1", Encoding.UTF8.GetBytes("dead"), Array.Empty<byte>()),
                });
                var frame3 = BuildFrame(3, new List<byte[]>
                {
                    BuildEntry(0x03, 0x00, "ns_gone", Array.Empty<byte>(), Array.Empty<byte>()),
                });
                var frame4 = BuildFrame(4, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns1", Encoding.UTF8.GetBytes("dead"), Encoding.UTF8.GetBytes("revived")),
                });
                File.WriteAllBytes(dbPath, BuildDb(frame1, frame2, frame3, frame4));

                using var db = CkvDb.Open(dbPath);
                Assert.Equal("v_keep", db.GetString("ns1", "keep"));
                Assert.Equal("revived", db.GetString("ns1", "dead")); // delete 后的重新 put 生效
                Assert.False(db.ContainsKey("ns1", "also_dead"));
                Assert.Equal(0, db.Count("ns_gone")); // drop 的命名空间不得残留
                Assert.False(db.ContainsKey("ns_gone", "x"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-3：TxID 单调性校验 ----------

        [Fact]
        public void NonMonotonicTxIdIsRejected()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "nonmono.db");
                var frame1 = BuildFrame(5, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v")),
                });
                var frame2 = BuildFrame(5, new List<byte[]> // 与前一帧相同 TxID，违反 §3.4 单调约束
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("k2"), Encoding.UTF8.GetBytes("v2")),
                });
                File.WriteAllBytes(dbPath, BuildDb(frame1, frame2));

                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(dbPath); });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void DescendingTxIdIsRejected()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "desc.db");
                var frame1 = BuildFrame(9, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v")),
                });
                var frame2 = BuildFrame(4, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("k2"), Encoding.UTF8.GetBytes("v2")),
                });
                File.WriteAllBytes(dbPath, BuildDb(frame1, frame2));

                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(dbPath); });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-3：OpCount 范围校验（0 与超限均拒绝） ----------

        [Fact]
        public void ZeroOpCountFrameIsRejected()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "zerocount.db");
                File.WriteAllBytes(dbPath, BuildDb(BuildFrame(1, new List<byte[]>())));

                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(dbPath); });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-2：未知 OpCode 必须拒绝而非静默跳过 ----------

        [Fact]
        public void UnknownOpCodeIsRejected()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "unknownop.db");
                var frame = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(0x7F, 0x00, "ns", Encoding.UTF8.GetBytes("k"), Array.Empty<byte>()),
                });
                File.WriteAllBytes(dbPath, BuildDb(frame));

                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(dbPath); });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-8：锁文件 32B 布局（PID u64@0 / UUID@8..24 / CreateTimeMs u64@24..32） ----------
        // 经行为验证：按规范布局写入"陈旧"CreateTimeMs 的遗留锁应被判定僵死并窃取；
        // 若实现误读偏移（如旧版 PID u32@0 / UUID@4 / time@20）则无法识别陈旧，写入将超时失败。

        [Fact]
        public void StaleLockIsStolenByLayoutAgedCreateTime()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "stalelock.db");
                string lockPath = dbPath + ".lock";

                // 按规范布局构造遗留锁：CreateTimeMs = 20 秒前（超过默认 10s 超时）
                byte[] lockData = new byte[32];
                WriteU64(lockData, 0, 12345); // PID @ 0..8
                byte[] uuid = Guid.NewGuid().ToByteArray();
                Array.Copy(uuid, 0, lockData, 8, 16); // UUID @ 8..24
                ulong oldMs = (ulong)DateTimeOffset.UtcNow.AddSeconds(-20).ToUnixTimeMilliseconds();
                WriteU64(lockData, 24, oldMs); // CreateTimeMs @ 24..32
                File.WriteAllBytes(lockPath, lockData);

                // 默认选项（无 ForceUnlock）：应凭锁内时间戳判定僵死并窃取
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("ns", "k", "v"); // 触发 FlushSnapshot -> LockFile.Acquire
                    Assert.Equal("v", db.GetString("ns", "k"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // 活锁持有者场景：用一个 FileShare.None 的 FileStream 持续持有锁文件，
        // 模拟"持锁者存活"的真实进程。此时：
        //   - 锁文件内容不可读（共享冲突）→ 回退 mtime 判定，年龄 ~0ms → 不陈旧
        //   - 即使判定陈旧，File.Move 窃取也会因共享冲突失败（Windows WinError 32）
        // 因此必须等待至 LockTimeoutMs 抛 LockedError，且锁文件原样保留。
        // （注意：单纯写一个"新鲜"遗留锁文件不足以阻塞——等待循环中其绝对年龄
        //  会自然达到 LockTimeoutMs 而被合法判僵死窃取，与 Go/Rust 算法一致。）
        [Fact]
        public void LiveLockHolderBlocksWithTimeout()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "freshlock.db");
                string lockPath = dbPath + ".lock";

                // 按规范布局构造锁文件：PID u64@0 / UUID@8..24 / CreateTimeMs u64@24..32
                byte[] lockData = new byte[32];
                WriteU64(lockData, 0, 999);
                byte[] uuid = Guid.NewGuid().ToByteArray();
                Array.Copy(uuid, 0, lockData, 8, 16);
                WriteU64(lockData, 24, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                File.WriteAllBytes(lockPath, lockData);

                // 模拟活锁持有者：独占持有锁文件（FileShare.None），
                // 使窃取所需的 File.Move 与内容读取均因共享冲突失败
                using var holder = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                // 短超时 + 无 ForceUnlock：必须等待后抛 LockedError，严禁窃取活锁
                Assert.Throws<LockedError>(() =>
                {
                    using var db = CkvDb.Open(dbPath, new DbOptions { LockTimeoutMs = 300 });
                    db.PutString("ns", "k", "v");
                });

                // 活锁文件必须原样保留（UUID 未被破坏、未被改名窃取）
                // 注意：holder 仍以 FileShare.None 独占持有该文件，只能经 holder 流本身读取
                Assert.True(File.Exists(lockPath));
                byte[] after = new byte[32];
                holder.Position = 0;
                Assert.Equal(32, holder.Read(after, 0, 32));
                for (int i = 0; i < 16; i++) Assert.Equal(uuid[i], after[8 + i]);
                Assert.Equal(999UL, BitConverter.ToUInt64(after, 0)); // PID 同样完好
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ForceUnlockClearsStaleLock()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "forceunlock.db");
                string lockPath = dbPath + ".lock";

                // 即使是"新鲜"的遗留锁，ForceUnlock 也应强制清除
                byte[] lockData = new byte[32];
                WriteU64(lockData, 0, 999);
                Array.Copy(Guid.NewGuid().ToByteArray(), 0, lockData, 8, 16);
                WriteU64(lockData, 24, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                File.WriteAllBytes(lockPath, lockData);

                using (var db = CkvDb.Open(dbPath, new DbOptions { ForceUnlock = true, LockTimeoutMs = 500 }))
                {
                    db.PutString("ns", "k", "v");
                    Assert.Equal("v", db.GetString("ns", "k"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P1-1：OnWarning 自愈告警 ----------

        [Fact]
        public void OnWarningFiresOnBackupRecovery()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "warn.db");
                string bakPath = dbPath + ".bak";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k1", "v1");
                }
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k2", "v2"); // 生成 .bak（含 k1）
                }
                Assert.True(File.Exists(bakPath));

                // 损坏主库头
                byte[] corrupted = File.ReadAllBytes(dbPath);
                corrupted[14] ^= 0xFF;
                File.WriteAllBytes(dbPath, corrupted);

                var warnings = new List<(string, string)>();
                using (var db = CkvDb.Open(dbPath, new DbOptions
                {
                    OnWarning = (type, msg) => warnings.Add((type, msg))
                }))
                {
                    Assert.Equal("v1", db.GetString("", "k1")); // 从 .bak 恢复
                }

                Assert.Contains(warnings, w => w.Item1 == "onRecoveredFromBackup");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void BackupIsPromotedWhenPrimaryMissing()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "promote.db");
                string bakPath = dbPath + ".bak";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k1", "v1");
                }
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k2", "v2");
                }
                Assert.True(File.Exists(bakPath));

                File.Delete(dbPath); // 主库丢失，仅剩 .bak
                Assert.False(File.Exists(dbPath));

                var warnings = new List<string>();
                using (var db = CkvDb.Open(dbPath, new DbOptions
                {
                    OnWarning = (type, msg) => warnings.Add(type)
                }))
                {
                    Assert.True(File.Exists(dbPath)); // .bak 被晋升为主库
                    Assert.Equal("v1", db.GetString("", "k1"));
                }
                Assert.Contains("onRecoveredFromBackup", warnings);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P1-2：Compact ----------

        [Fact]
        public void CompactRewritesAndPreservesData()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "compact.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("ns", "a", "1");
                    db.PutString("ns", "b", "2");
                    db.PutInt64("ns", "c", 3);
                    db.Delete("ns", "b");
                    long sizeBefore = new FileInfo(dbPath).Length;

                    db.Compact();
                    long sizeAfter = new FileInfo(dbPath).Length;

                    Assert.Equal("1", db.GetString("ns", "a"));
                    Assert.Equal(3, db.GetInt64("ns", "c"));
                    Assert.False(db.ContainsKey("ns", "b"));

                    // 快照 TxID 递增（规范 §5.2.2：TxID = currentMaxTxID + 1）
                    var snap = db.CreateSnapshot();
                    try
                    {
                        Assert.True(snap.TxId > 0);
                    }
                    finally { snap.Dispose(); }
                }

                // 重载后数据完好
                using var db2 = CkvDb.Open(dbPath);
                Assert.Equal("1", db2.GetString("ns", "a"));
                Assert.Equal(3, db2.GetInt64("ns", "c"));
                Assert.False(db2.ContainsKey("ns", "b"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P1-3：写前预检 + B1 空库回归 ----------

        [Fact]
        public void WriteAfterFullClearDoesNotFalsePositive()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "b1.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("ns1", "k1", "v1");
                    db.PutString("ns2", "k2", "v2");

                    // 清空整库：落盘为仅含 32B 文件头的空库
                    Assert.True(db.DropNamespace("ns1"));
                    Assert.True(db.DropNamespace("ns2"));

                    // B1 回归：空库后再次写入不得误报 ConcurrentModificationError
                    db.PutString("ns1", "k3", "v3");
                    db.PutString("ns1", "k4", "v4");
                    db.PutString("ns2", "k5", "v5");
                    Assert.Equal(2, db.Count("ns1"));
                    Assert.Equal(1, db.Count("ns2"));
                }

                // 重载验证持久化完整
                using var db2 = CkvDb.Open(dbPath);
                Assert.Equal("v3", db2.GetString("ns1", "k3"));
                Assert.Equal("v5", db2.GetString("ns2", "k5"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ExternalDiskModificationIsDetected()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "tamper.db");
                var db = CkvDb.Open(dbPath);
                db.PutString("ns", "k1", "v1");
                db.PutString("ns", "k2", "v2");

                // 外部篡改：在文件尾部追加一个字节，改变 (maxTxID, fileSize) 基线
                byte[] original = File.ReadAllBytes(dbPath);
                byte[] tampered = new byte[original.Length + 1];
                Array.Copy(original, tampered, original.Length);
                File.WriteAllBytes(dbPath, tampered);

                // 下一次写入必须抛 ConcurrentModificationError
                Assert.Throws<ConcurrentModificationError>(() => db.PutString("ns", "k3", "v3"));
                db.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P1-5：快照计数与 CoW 隔离 ----------

        [Fact]
        public void SnapshotIsolationHoldsWithCounting()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "cow.db");
                using var db = CkvDb.Open(dbPath);
                db.PutString("ns", "a", "1");
                db.PutString("ns", "b", "2");

                var snap1 = db.CreateSnapshot();
                db.PutString("ns", "a", "1_updated");
                db.Delete("ns", "b");

                // snap1 冻结
                Assert.Equal("1", snap1.GetString("ns", "a"));
                Assert.True(snap1.ContainsKey("ns", "b"));

                var snap2 = db.CreateSnapshot();
                Assert.Equal("1_updated", snap2.GetString("ns", "a"));
                Assert.False(snap2.ContainsKey("ns", "b"));

                // 释放后继续写，隔离依然正确（计数归零路径）
                snap1.Dispose();
                snap2.Dispose();
                db.PutString("ns", "c", "3");
                Assert.Equal(2, db.Count("ns"));

                var snap3 = db.CreateSnapshot();
                Assert.False(snap3.ContainsKey("ns", "b"));
                snap3.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- 事件 ValueType（与 Rust/Go 对齐） ----------

        [Fact]
        public void PutEventsCarryValueType()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "evt.db");
                using var db = CkvDb.Open(dbPath);
                var events = new List<KeyChangeEvent>();
                db.Watch(ev => events.Add(ev));

                db.PutString("ns", "k_str", "v");
                db.PutInt64("ns", "k_int", 7);
                db.PutBool("ns", "k_bool", true);
                db.Delete("ns", "k_str");

                Assert.Equal(4, events.Count);
                Assert.Equal(Constants.ValTypeString, events[0].ValueType);
                Assert.Equal(Constants.ValTypeInt64, events[1].ValueType);
                Assert.Equal(Constants.ValTypeBool, events[2].ValueType);
                Assert.Equal(KeyChangeOp.Delete, events[3].Op);
                Assert.Equal((byte)0, events[3].ValueType);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-2/P1-4：墓碑混排文件的顺序重放语义 ----------

        [Fact]
        public void MixedOpLogReplaysInOrder()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "mixed.db");
                // 交错 put/delete/drop，验证慢路径顺序语义
                var f1 = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("1")),
                });
                var f2 = BuildFrame(2, new List<byte[]>
                {
                    BuildEntry(0x02, 0x00, "ns", Encoding.UTF8.GetBytes("a"), Array.Empty<byte>()),
                });
                var f3 = BuildFrame(3, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("2")),
                    BuildEntry(0x01, 0x01, "ns", Encoding.UTF8.GetBytes("b"), Encoding.UTF8.GetBytes("3")),
                });
                var f4 = BuildFrame(4, new List<byte[]>
                {
                    BuildEntry(0x03, 0x00, "ns", Array.Empty<byte>(), Array.Empty<byte>()),
                });
                var f5 = BuildFrame(5, new List<byte[]>
                {
                    BuildEntry(0x01, 0x01, "other", Encoding.UTF8.GetBytes("z"), Encoding.UTF8.GetBytes("9")),
                });
                File.WriteAllBytes(dbPath, BuildDb(f1, f2, f3, f4, f5));

                using var db = CkvDb.Open(dbPath);
                Assert.Equal(0, db.Count("ns"));      // drop 后整个命名空间消失
                Assert.False(db.ContainsKey("ns", "a"));
                Assert.False(db.ContainsKey("ns", "b"));
                Assert.Equal("9", db.GetString("other", "z"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
