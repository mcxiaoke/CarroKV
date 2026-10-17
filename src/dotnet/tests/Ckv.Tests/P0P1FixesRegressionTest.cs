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

        // ---------- C12 / T12：恢复期校验定长类型与保留类型 ----------

        [Fact]
        public void T12_RecoveryRejectsInvalidValueTypeOrLength()
        {
            string tempDir = NewTempDir();
            try
            {
                // 1. 保留 value_type = 0x05
                string pVtype = Path.Combine(tempDir, "reserved_vtype.db");
                var fVtype = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(Constants.OpPut, 0x05, "ns", Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("val")),
                });
                File.WriteAllBytes(pVtype, BuildDb(fVtype));
                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(pVtype); });

                // 2. int64 value 长度为 7 字节 (!= 8)
                string pI64 = Path.Combine(tempDir, "bad_i64.db");
                var fI64 = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(Constants.OpPut, Constants.ValTypeInt64, "ns", Encoding.UTF8.GetBytes("k"), new byte[] { 1, 2, 3, 4, 5, 6, 7 }),
                });
                File.WriteAllBytes(pI64, BuildDb(fI64));
                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(pI64); });

                // 3. bool value 长度为 3 字节 (!= 1)
                string pBool = Path.Combine(tempDir, "bad_bool.db");
                var fBool = BuildFrame(1, new List<byte[]>
                {
                    BuildEntry(Constants.OpPut, Constants.ValTypeBool, "ns", Encoding.UTF8.GetBytes("k"), new byte[] { 1, 0, 1 }),
                });
                File.WriteAllBytes(pBool, BuildDb(fBool));
                Assert.Throws<CorruptedError>(() => { using var db = CkvDb.Open(pBool); });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------- P0-8：锁行为 ----------
        // 自 C9 起锁是"由 Open 持有整个连接生命周期的内核锁"，以下三条旧用例随判活+窃取
        // 启发式一并删除：
        //   - StaleLockIsStolenByLayoutAgedCreateTime（陈旧锁窃取）
        //   - LiveLockHolderBlocksWithTimeout（按锁文件年龄判活后的阻塞）
        //   - ForceUnlockClearsStaleLock（强制清除遗留锁）
        // 新的门槛用例见 LockProtocolTest.cs；WAL 残尾自愈必须持锁的用例见本文件的
        // TailRepairRequiresTheFileLock。

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
                    // 决策 D1：默认不再自动回退 `.bak`，告警只在显式开启后出现
                    AutoRecoverFromBackup = true,
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

        /// <summary>
        /// D1-7：主库缺失 + `.bak` 完好不再是"全新库"，默认拒绝打开而不是静默晋升
        /// 上一代（晋升即静默回滚到旧一代）。
        /// </summary>
        [Fact]
        public void MissingPrimaryWithHealthyBackupIsRefusedByDefault()
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
                byte[] bakBefore = File.ReadAllBytes(bakPath);

                File.Delete(dbPath); // 主库丢失，仅剩 .bak
                Assert.False(File.Exists(dbPath));

                var warnings = new List<string>();
                var ex = Assert.Throws<CorruptedError>(() => CkvDb.Open(dbPath, new DbOptions
                {
                    OnWarning = (type, msg) => warnings.Add(type)
                }));

                Assert.Contains(".bak", ex.Message);
                Assert.False(File.Exists(dbPath)); // 拒绝打开不得创建空库
                Assert.Equal(bakBefore, File.ReadAllBytes(bakPath)); // 离线副本不得被改写
                Assert.Empty(warnings);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>显式开启开关后恢复原有晋升行为，并沿用 onRecoveredFromBackup 告警码。</summary>
        [Fact]
        public void MissingPrimaryWithHealthyBackupIsPromotedWhenExplicitlyEnabled()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "promote_opt_in.db");
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

                File.Delete(dbPath);

                var warnings = new List<string>();
                using (var db = CkvDb.Open(dbPath, new DbOptions
                {
                    AutoRecoverFromBackup = true,
                    OnWarning = (type, msg) => warnings.Add(type)
                }))
                {
                    Assert.True(File.Exists(dbPath)); // `.bak` 被晋升为主库
                    Assert.Equal("v1", db.GetString("", "k1"));
                }
                Assert.Contains("onRecoveredFromBackup", warnings);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// D1-8（第 5 处静默数据丢失）：主库缺失且 `.bak` 损坏时，旧实现静默按空库打开，
        /// 调用方首次写入即生成全新 `.db`，"数据丢了"被伪装成"打开成功"。
        /// </summary>
        [Fact]
        public void MissingPrimaryWithDamagedBackupIsNotOpenedAsAnEmptyDatabase()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "damaged_bak.db");
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

                // 破坏 `.bak` 中的一个帧字节（跳过 32B 文件头）
                byte[] bytes = File.ReadAllBytes(bakPath);
                bytes[32 + 12] ^= 0xFF;
                File.WriteAllBytes(bakPath, bytes);

                File.Delete(dbPath);

                var ex = Assert.Throws<CorruptedError>(() => CkvDb.Open(dbPath));
                Assert.Contains("could not be loaded", ex.Message);
                Assert.False(File.Exists(dbPath)); // 不得留下一个全新的空库
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// C10 / T11：open 期的残尾自愈必须持有跨进程文件锁，否则会截掉另一个进程
        /// 刚追加的帧。持锁进程存活时，打开必须失败为 LockedError，且**不得**改动文件。
        /// </summary>
        [Fact]
        public void TailRepairRequiresTheFileLock()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "t11.db");
                string lockPath = dbPath + ".lock";

                var walOptions = new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    SyncMode = SyncMode.Immediate,
                    CompactionThresholdBytes = ulong.MaxValue - 1
                };

                using (var db = CkvDb.Open(dbPath, walOptions))
                {
                    db.PutString("", "a", "1");
                    db.PutString("", "b", "2");
                    db.PutString("", "c", "3");
                }

                long healthyLen = new FileInfo(dbPath).Length;

                // 追加一个不完整的帧（少于 28 字节帧头）：文件在最后一帧中间结束
                byte[] tornTail = new byte[] { 0x4B, 0x56, 0x46, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22 };
                using (var fs = new FileStream(dbPath, FileMode.Append, FileAccess.Write))
                {
                    fs.Write(tornTail, 0, tornTail.Length);
                    fs.Flush(true);
                }
                long damagedLen = new FileInfo(dbPath).Length;
                Assert.Equal(healthyLen + tornTail.Length, damagedLen);

                // 伪造"另一个进程"持有的锁：时间戳新鲜（不会因超时被窃取），并保持句柄打开
                // （Windows 上文件被占用时 rename 窃取必然失败）。
                // 自 C9 起锁是内核锁：持锁者必须真的独占持有锁文件，伪造锁文件内容不再有任何作用。
                using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var lockedOptions = new DbOptions
                    {
                        EngineMode = StorageEngineMode.AppendOnlyLog,
                        SyncMode = SyncMode.Immediate,
                        CompactionThresholdBytes = ulong.MaxValue - 1,
                        LockTimeoutMs = 100
                    };
                    Assert.Throws<LockedError>(() => CkvDb.Open(dbPath, lockedOptions));
                    Assert.Equal(damagedLen, new FileInfo(dbPath).Length); // 拒绝打开不得截断日志

                    GC.KeepAlive(holder);
                }

                File.Delete(lockPath);

                using (var db = CkvDb.Open(dbPath, walOptions))
                {
                    Assert.Equal("1", db.GetString("", "a"));
                    Assert.Equal("2", db.GetString("", "b"));
                    Assert.Equal("3", db.GetString("", "c"));
                }
                Assert.Equal(healthyLen, new FileInfo(dbPath).Length); // 自愈到最后一帧边界
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>反向对照：没有任何 `.bak` 时，同一路径是全新库，必须照常打开。</summary>
        [Fact]
        public void MissingPrimaryWithoutBackupStillOpensAsANewDatabase()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "fresh.db");
                Assert.False(File.Exists(dbPath + ".bak"));

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");
                    Assert.Equal("v", db.GetString("", "k"));
                }
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

        /// <summary>
        /// C17：重复 open 传入冲突 options 时的行为。
        ///
        /// 修订（批次一 · 条目 11）：原用例同时传了 <c>ReadOnly = true</c> 并断言
        /// "返回同一单例 + 只发告警"。那等于把"以只读意图打开却静默拿到读写实例"
        /// （静默提权，且 <c>OnWarning</c> 默认为 null 时连告警都看不见）固化为正常行为。
        /// 现在 <c>ReadOnly</c> 冲突抛 <see cref="ConflictingOptionsError"/>；
        /// 非权限类冲突（engineMode / syncMode）维持告警 + 返回同一单例。
        /// </summary>
        [Fact]
        public void T17_ReentrantOpenWarnsOnNonPermissionOptionsConflict()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "c17_conflict.db");
                var db1 = CkvDb.Open(dbPath, new DbOptions { ReadOnly = false, EngineMode = StorageEngineMode.SnapshotAtomic });
                db1.PutString("", "k", "v");

                var warnings = new List<(string code, string msg)>();
                var opts2 = new DbOptions
                {
                    ReadOnly = false,
                    SyncMode = SyncMode.Manual,
                    OnWarning = (code, msg) => warnings.Add((code, msg))
                };

                var db2 = CkvDb.Open(dbPath, opts2);
                Assert.Same(db1.Root, db2.Root);
                Assert.NotSame(db1, db2);
                Assert.Single(warnings);
                Assert.Equal("onOptionsConflict", warnings[0].code);
                Assert.Contains("syncMode", warnings[0].msg);

                // ReadOnly 冲突必须抛错，不得静默升权（回归覆盖见 BatchOneDataSafetyTest）。
                Assert.Throws<ConflictingOptionsError>(
                    () => CkvDb.Open(dbPath, new DbOptions { ReadOnly = true }));

                // 句柄包装模型（ADR-20261017）：db1 与 db2 均为独立句柄，各自独立 Dispose
                db2.Dispose();
                db1.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
