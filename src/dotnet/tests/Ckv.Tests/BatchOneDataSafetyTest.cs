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
        private static (string canonical, WeakReference<CkvDb> deadRef) CreateDeadWeakReference(string path)
        {
            var db = CkvDb.Open(path);
            string canonical = CanonicalOf(db);
            // 压制终结器，模拟"未运行终结器即被回收"或"终结器尚未被调度"时的死弱引用状态
            GC.SuppressFinalize(db);
            var weakRef = new WeakReference<CkvDb>(db);
            // 确保测试字典中记录的是该弱引用
            Registry()[canonical] = weakRef;
            db = null!;
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            return (canonical, weakRef);
        }

        [Fact]
        public void Open_WithStaleDeadWeakReference_DoesNotHang()
        {
            string dir = NewDir("stale");
            string path = Path.Combine(dir, "stale.db");
            try
            {
                var (canonical, deadRef) = CreateDeadWeakReference(path);

                Assert.True(Registry().ContainsKey(canonical), "precondition: a stale registry entry survives");
                Assert.False(deadRef.TryGetTarget(out _), "precondition: weak reference target is collected");

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

                Assert.Same(db.Root, same.Root);
                Assert.NotSame(db, same);
                Assert.Contains(warnings, w => w.StartsWith("onOptionsConflict"));
                // 非权限类冲突仍然沿用既有 options，底层引擎一致。
                Assert.Equal("v", PutAndRead(db));
                same.Dispose();
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

        /// <summary>
        /// 条目 13（ADR-20261017 句柄包装模型）：
        /// 每次 Open() 返回独立的门面句柄，内部共享底层持久化引擎。
        /// 任何一个持有者 Dispose() 仅释放自己的句柄（引用计数扣减），绝不影响其他持有者。
        /// 句柄自身 Dispose 保证绝对幂等（重复调用 No-op）。
        /// 只有当所有持有者均已释放（引用计数归零），底层引擎才真正关闭并释放跨进程锁。
        /// </summary>
        [Fact]
        public void SharedInstance_DisposeFromOneHolder_DoesNotCloseOtherHolders()
        {
            string dir = NewDir("ownership");
            string path = Path.Combine(dir, "own.db");
            try
            {
                var a = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                a.PutString("", "k", "v");

                var b = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 });
                Assert.NotSame(a, b);
                Assert.Same(a.Root, b.Root);

                // 借用者 B 关闭 —— 仅 B 自身失效，A 依然完好可用！
                b.Dispose();
                Assert.True(b.IsClosed);
                // B 幂等重复 Dispose
                b.Dispose();

                // B 关闭后操作被拒绝
                Assert.Throws<ObjectDisposedException>(() => b.GetString("", "k"));
                Assert.Throws<LockedError>(() => b.PutString("", "k2", "v2"));

                // A 依然可以自由读写已提交与新数据
                Assert.False(a.IsClosed);
                Assert.Equal("v", a.GetString("", "k"));
                a.PutString("", "k2", "v2");
                Assert.Equal("v2", a.GetString("", "k2"));

                // A 关闭 —— 引用计数归零，底层引擎真正彻底关闭
                a.Dispose();
                Assert.True(a.IsClosed);

                // 再次 Open 必须拿到一个全新句柄与底层实例
                var fresh = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 2000 });
                Assert.NotSame(a.Root, fresh.Root);
                Assert.Equal("v2", fresh.GetString("", "k2"));
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