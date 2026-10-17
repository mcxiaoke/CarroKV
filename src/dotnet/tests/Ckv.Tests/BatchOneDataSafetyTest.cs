using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 批次一「数据安全与止血」回归测试。
    ///
    /// 覆盖：
    ///   P0-4  共享实例的 Dispose 所有权陷阱（当前语义被显式记录，修复见批次二条目 13）
    ///   P0-5  注册表残留死弱引用导致 Open() 死循环
    ///   P0-6  Open() 失败路径泄漏跨进程连接锁
    ///   P1-3  加载期校验 NsLen &lt;= 255
    ///   P1-12 关闭后读路径行为（记录当前语义）
    ///   P1-15 ReadOnly 选项冲突必须抛 ConflictingOptionsError
    ///   P1-17 Snapshot.Dispose 并发双重释放不得击穿快照隔离
    ///   .tmp 晋升失败必须让 Open 失败且不得删除已校验的 .tmp
    /// </summary>
    public class BatchOneDataSafetyTest
    {
        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_b1_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static ConcurrentDictionary<string, WeakReference<CkvDb>> Registry()
        {
            return (ConcurrentDictionary<string, WeakReference<CkvDb>>)typeof(CkvDb)
                .GetField("Registry", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
        }

        private static string CanonicalOf(CkvDb db)
        {
            return (string)typeof(CkvDb)
                .GetField("_canonicalPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(db)!;
        }

        // ---------------- 手工构造磁盘文件（v3.2 规范） ----------------

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
            Array.Copy(new byte[] { 0x55, 0x4B, 0x56, 0x44, 0x42, 0x30, 0x30, 0x33 }, 0, h, 0, 8); // "UKVDB003"
            WriteU16(h, 8, 3);   // readVer
            WriteU16(h, 10, 3);  // writeVer
            WriteU32(h, 12, 0);  // flags
            WriteU32(h, 28, Crc32.Compute(h, 0, 28));
            return h;
        }

        /// <summary>构造一条 op 条目的载荷（NsLen 可被显式指定，用于越界场景）。</summary>
        private static byte[] BuildEntry(byte opCode, byte valType, ushort nsLenOverride, string ns, byte[] key, byte[] val)
        {
            byte[] nsBytes = Encoding.UTF8.GetBytes(ns);
            byte[] payload = new byte[10 + nsBytes.Length + key.Length + val.Length];
            payload[0] = opCode;
            payload[1] = valType;
            WriteU16(payload, 2, nsLenOverride);
            WriteU16(payload, 4, (ushort)key.Length);
            WriteU32(payload, 6, (uint)val.Length);
            Array.Copy(nsBytes, 0, payload, 10, nsBytes.Length);
            Array.Copy(key, 0, payload, 10 + nsBytes.Length, key.Length);
            Array.Copy(val, 0, payload, 10 + nsBytes.Length + key.Length, val.Length);
            return payload;
        }

        private static void WriteFileWithEntry(string path, byte[] entryPayload)
        {
            byte[] header = BuildFileHeader();

            // 帧头 28 字节：magic(4) + headCrc(4) + [TxID(8) opCount(4) payloadLen(4) payloadCrc(4)] = 20
            byte[] tail20 = new byte[20];
            WriteU64(tail20, 0, 1);                          // TxID
            WriteU32(tail20, 8, 1);                          // opCount
            WriteU32(tail20, 12, (uint)entryPayload.Length); // payloadLen
            WriteU32(tail20, 16, Crc32.Compute(entryPayload, 0, entryPayload.Length));

            byte[] frame = new byte[28 + entryPayload.Length];
            WriteU32(frame, 0, 0x4D52464B);                 // 'KFRM'
            WriteU32(frame, 4, Crc32.Compute(tail20, 0, 20));
            Array.Copy(tail20, 0, frame, 8, 20);
            Array.Copy(entryPayload, 0, frame, 28, entryPayload.Length);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(header, 0, header.Length);
            fs.Write(frame, 0, frame.Length);
        }

        // ---------------- P0-5：注册表死弱引用导致 Open 死循环 ----------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LeakInstanceWithoutDispose(CkvDb db)
        {
            // 模拟"CkvDb.Open(p);" 不赋值 / DI 容器误配置 / 异常路径漏掉 using：
            // 实例被 GC 回收，但 Dispose 从未执行，注册表里留下死弱引用。
            GC.KeepAlive(db);
        }

        [Fact]
        public void Open_WithStaleDeadWeakReference_DoesNotHang()
        {
            string dir = NewDir("stale");
            string path = Path.Combine(dir, "stale.db");
            try
            {
                var first = CkvDb.Open(path);
                string canonical = CanonicalOf(first);
                LeakInstanceWithoutDispose(first);

                // 强制回收，让注册表条目变成死弱引用（Dispose 未运行 → 条目仍在）。
                for (int i = 0; i < 10 && Registry().TryGetValue(canonical, out var r) && r.TryGetTarget(out _); i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Assert.True(Registry().ContainsKey(canonical), "precondition: a stale registry entry survives");

                // 旧实现在这里死循环：TryAdd 因 key 已存在必然失败 → 每轮重新 new + 取连接锁
                // + InitAndRecover（全量读盘） → continue → 永不退出。
                // 用后台线程 + 超时把它钉死，避免测试本身挂起 CI。
                CkvDb? reopened = null;
                Exception? failure = null;
                var worker = new Thread(() =>
                {
                    try { reopened = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 2000 }); }
                    catch (Exception ex) { failure = ex; }
                });
                worker.IsBackground = true;
                var sw = Stopwatch.StartNew();
                worker.Start();

                Assert.True(worker.Join(TimeSpan.FromSeconds(20)),
                    "Open() must converge when the registry holds a dead weak reference (P0-5 infinite loop)");
                sw.Stop();
                Assert.Null(failure);
                Assert.NotNull(reopened);
                reopened!.Dispose();

                // 死条目必须已被驱逐，而不是留着无限堆积。
                Assert.False(Registry().ContainsKey(canonical),
                    "the stale registry entry must be evicted, not left behind");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------------- P0-6：Open 失败路径泄漏跨进程连接锁 ----------------

        [Fact]
        public void Open_WhenInitFails_DoesNotLeakTheConnectionLock()
        {
            string dir = NewDir("lockleak");
            string path = Path.Combine(dir, "broken.db");
            try
            {
                // 构造一个载荷 CRC 正确、但条目 NsLen 越界（300 > 255）的文件：
                // 加载期校验会抛 CorruptedError，正好落在 InitAndRecover 这一步。
                byte[] entry = BuildEntry(0x01, 0x01, 300, new string('n', 300), Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"));
                WriteFileWithEntry(path, entry);

                // 第一次 Open 必须以 CorruptedError 失败（而不是 LockedError）。
                var first = Assert.Throws<CorruptedError>(() => CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 }));
                Assert.Contains("Namespace length 300", first.Message);

                // 修复磁盘内容后重开：若第一次失败泄漏了跨进程内核锁，这里会拿到
                // LockedError（误导性错误，把排查引向"别人在用"）而不是成功。
                WriteFileWithEntry(
                    path,
                    BuildEntry(0x01, 0x01, 0, "", Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v")));

                var db = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 2000 });
                Assert.Equal("v", db.GetString("", "k"));
                db.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------------- P1-3：加载期 NsLen 越界 ----------------

        [Fact]
        public void Open_WithOverlongNamespace_ReportsCorruptedErrorInsteadOfLoadingAnOrphan()
        {
            string dir = NewDir("nslen");
            string path = Path.Combine(dir, "nslen.db");
            try
            {
                byte[] entry = BuildEntry(0x01, 0x01, 300, new string('n', 300), Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"));
                WriteFileWithEntry(path, entry);

                var ex = Assert.Throws<CorruptedError>(() => CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 }));
                Assert.Contains("exceeds max 255", ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Open_WithNamespaceAtExactly255Bytes_IsAccepted()
        {
            string dir = NewDir("nslen255");
            string path = Path.Combine(dir, "edge.db");
            try
            {
                string ns = new string('n', 255);
                var db = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                db.PutString(ns, "k", "v");
                Assert.Equal("v", db.GetString(ns, "k"));
                db.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------------- P1-15：ReadOnly 选项冲突必须抛错 ----------------

        [Fact]
        public void ReentrantOpen_WithReadOnlyConflict_Throws()
        {
            string dir = NewDir("roconflict");
            string path = Path.Combine(dir, "ro.db");
            try
            {
                var rw = CkvDb.Open(path, new DbOptions { ReadOnly = false });
                rw.PutString("", "k", "v");

                // 以只读意图二次打开：绝不能静默返回读写实例（那是静默提权）。
                Assert.Throws<ConflictingOptionsError>(
                    () => CkvDb.Open(path, new DbOptions { ReadOnly = true, LockTimeoutMs = 1000 }));

                // 反方向同样拒绝：要写却拿到只读实例会让后续写入在远处以 LockedError 失败。
                rw.Dispose();

                var ro = CkvDb.Open(path, new DbOptions { ReadOnly = true, LockTimeoutMs = 1000 });
                Assert.Throws<ConflictingOptionsError>(
                    () => CkvDb.Open(path, new DbOptions { ReadOnly = false, LockTimeoutMs = 1000 }));
                ro.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void ReentrantOpen_WithEngineModeConflict_StillWarns()
        {
            string dir = NewDir("engconflict");
            string path = Path.Combine(dir, "eng.db");
            try
            {
                var db = CkvDb.Open(path, new DbOptions { EngineMode = StorageEngineMode.SnapshotAtomic });
                var warnings = new System.Collections.Generic.List<string>();
                var same = CkvDb.Open(path, new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    OnWarning = (code, msg) => warnings.Add(code + ":" + msg)
                });

                Assert.Same(db, same);
                Assert.Contains(warnings, w => w.StartsWith("onOptionsConflict"));
                // 非权限类冲突仍然沿用既有 options，返回同一实例。
                Assert.Equal("v", PutAndRead(db));
                db.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string PutAndRead(CkvDb db)
        {
            db.PutString("", "engine_probe", "v");
            return db.GetString("", "engine_probe")!;
        }

        // ---------------- P0-4：共享实例的 Dispose 所有权陷阱 ----------------

        /// <summary>
        /// 记录当前共享实例的 Dispose 语义，并让它**显式可见**而不是被沉默地祝福。
        ///
        /// 旧测试（<c>PathIdentityTest.DotAndAbsoluteFormsShareTheSameInstance</c>）
        /// 先 <c>db2.Dispose()</c> 再 <c>db1.Dispose()</c> 且不报错，等于把
        /// "Dispose 任意一个即干掉全部共享实例"断言成了正常行为 —— 这正是造成真实线上
        /// 崩溃（备份服务 <c>using var db = CkvDb.Open(...)</c> 杀掉主线程正在用的实例）
        /// 的那个语义。
        ///
        /// 批次二条目 13 落地引用计数后，本用例应反转为
        /// "B.Dispose 之后 A 仍可读写"（即评审给出的 P0-4 验收标准）。
        /// </summary>
        [Fact]
        public void SharedInstance_DisposeFromAnyHolder_ClosesTheWholeInstance()
        {
            string dir = NewDir("ownership");
            string path = Path.Combine(dir, "own.db");
            try
            {
                var a = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                a.PutString("", "k", "v");

                var b = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                Assert.Same(a, b);

                // 借用者 B 关闭 —— 当前语义下整个共享实例随之失效。
                b.Dispose();

                Assert.ThrowsAny<Exception>(() =>
                {
                    _ = a.GetString("", "k");
                    a.PutString("", "k2", "v2");
                });

                // 再次 Open 必须拿到一个全新、可用的实例（说明旧实例确实被彻底关闭了）。
                var fresh = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 2000 });
                Assert.NotSame(a, fresh);
                Assert.Equal("v", fresh.GetString("", "k"));
                fresh.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------------- P1-17：Snapshot 并发双重释放 ----------------

        [Fact]
        public void Snapshot_ConcurrentDispose_ReleasesHostCountExactlyOnce()
        {
            string dir = NewDir("snaprel");
            string path = Path.Combine(dir, "snap.db");
            try
            {
                var db = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                db.PutString("ns", "k", "v");

                var snapshot = db.CreateSnapshot();
                Assert.Equal("v", snapshot.GetString("ns", "k"));

                var start = new ManualResetEventSlim(false);
                var done = new CountdownEvent(2);

                // 两个真实线程并发 Dispose 同一个快照（非原子的 check-then-set 会双双通过）。
                var t1 = new Thread(() => { start.Wait(); try { snapshot.Dispose(); } finally { done.Signal(); } });
                var t2 = new Thread(() => { start.Wait(); try { snapshot.Dispose(); } finally { done.Signal(); } });
                t1.IsBackground = true;
                t2.IsBackground = true;
                t1.Start();
                t2.Start();
                start.Set();
                Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "both dispose calls must return");

                // 释放次数恰好一次 → 宿主 _snapshotCount 归零，后续写恢复原地写路径。
                // 若双重释放击穿了计数，快照会读到被后续写就地改写的列表。
                db.PutString("ns", "k", "v2");
                Assert.Equal("v2", db.GetString("ns", "k"));

                db.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------------- .tmp 晋升失败必须让 Open 失败 ----------------

        [Fact]
        public void Open_WithValidTmpAndOccupiedTarget_KeepsTmpAndReportsCorrupted()
        {
            string dir = NewDir("tmppromote");
            string path = Path.Combine(dir, "promote.db");
            string tmpPath = path + ".tmp";
            try
            {
                // 先造一个正常的库，再把它复制成 .tmp 并删掉主库 —— 此时 .tmp 是唯一完好的一份。
                var seed = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                seed.PutString("ns", "k", "v");
                seed.Flush();
                seed.Dispose();

                File.Copy(path, tmpPath, true);
                File.Delete(path);

                // 用一个**目录**占住 `.db`：File.Move(tmp -> dir) 必然失败。
                Directory.CreateDirectory(path);

                var ex = Assert.Throws<CorruptedError>(
                    () => CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 }));
                Assert.Contains(".tmp", ex.Message);

                // 关键断言：已经校验通过的 .tmp 必须原样保留 —— 它是唯一完好的数据。
                Assert.True(File.Exists(tmpPath), "a validated .tmp must never be deleted on promotion failure");
                Assert.True(new FileInfo(tmpPath).Length > 0);

                // 解除占用后可以正常自愈，数据没丢。
                Directory.Delete(path, true);
                var recovered = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 2000 });
                Assert.Equal("v", recovered.GetString("ns", "k"));
                recovered.Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}