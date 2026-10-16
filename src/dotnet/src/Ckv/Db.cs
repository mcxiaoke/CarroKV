using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ckv
{
    internal sealed class KeyItem
    {
        public byte[] Key { get; }
        public byte ValueType { get; }
        public byte[] Value { get; }

        public KeyItem(byte[] key, byte valueType, byte[] value)
        {
            Key = key;
            ValueType = valueType;
            Value = value;
        }
    }

    internal sealed class NamespaceStore
    {
        public List<KeyItem> Items { get; } = new List<KeyItem>();

        public int FindIndex(ReadOnlySpan<byte> key)
        {
            int low = 0;
            int high = Items.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                int cmp = Items[mid].Key.AsSpan().SequenceCompareTo(key);
                if (cmp == 0) return mid;
                if (cmp < 0) low = mid + 1;
                else high = mid - 1;
            }
            return ~low;
        }

        public void Put(byte[] key, byte valType, byte[] val)
        {
            int idx = FindIndex(key);
            if (idx >= 0)
            {
                Items[idx] = new KeyItem(key, valType, val);
            }
            else
            {
                Items.Insert(~idx, new KeyItem(key, valType, val));
            }
        }

        public bool Delete(ReadOnlySpan<byte> key)
        {
            int idx = FindIndex(key);
            if (idx >= 0)
            {
                Items.RemoveAt(idx);
                return true;
            }
            return false;
        }

        public NamespaceStore Clone()
        {
            var clone = new NamespaceStore();
            clone.Items.AddRange(Items);
            return clone;
        }
    }

    public sealed class CkvDb : IDisposable
    {
        private static readonly ConcurrentDictionary<string, WeakReference<CkvDb>> Registry =
            new ConcurrentDictionary<string, WeakReference<CkvDb>>(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly string _canonicalPath;
        private readonly DbOptions _options;
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();

        // 命名空间按键的 UTF-8 字节无符号字典序排列（规范 §3.3 排序规则），
        // 替换 StringComparer.Ordinal（UTF-16 码元序，对代理对字符次序不一致）。
        private readonly SortedDictionary<string, NamespaceStore> _namespaces =
            new SortedDictionary<string, NamespaceStore>(Utf8StringComparer.Instance);

        private ulong _currentMaxTxId;
        private bool _isDisposed;
        private bool _registered;

        // 写前磁盘预检状态（规范 §4.1.3）：与本实例最后一次成功落盘一致的 (maxTxID, fileSize)
        private ulong _lastKnownDiskMaxTxId;
        private long _lastKnownDiskFileSize;

        // 存活快照计数：>0 时写路径执行 CoW 克隆；=0 时原地修改。
        // 创建/释放均在 _snapshotLock 保护下变更；写路径在写锁内读取
        // （读写锁互斥保证创建对后续写操作可见）。
        private readonly object _snapshotLock = new object();
        private int _snapshotCount;

        // §2.2（API v1.0 冻结）：带 id 的回调槽，使精确注销成为可能
        private readonly List<(long Id, Action<KeyChangeEvent> Callback)> _watchers =
            new List<(long, Action<KeyChangeEvent>)>();
        private readonly object _watcherLock = new object();
        private long _nextWatcherId;
        public event Action<KeyChangeEvent>? KeyChanged;

        /// <summary>
        /// 注册数据变更监听回调，返回订阅句柄（API v1.0 §2.2）。
        /// 仅显式 <see cref="WatcherHandle.Dispose"/> 才移除回调；单纯丢弃句柄不注销，
        /// 因此既有的 <c>db.Watch(cb);</c> 写法在 v1.0 中行为不变。
        /// </summary>
        public WatcherHandle Watch(Action<KeyChangeEvent> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_watcherLock)
            {
                _nextWatcherId++;
                long id = _nextWatcherId;
                _watchers.Add((id, callback));
                return new WatcherHandle(this, id);
            }
        }

        /// <summary>按 id 移除监听器。幂等。</summary>
        internal void RemoveWatcher(long id)
        {
            lock (_watcherLock)
            {
                for (int i = 0; i < _watchers.Count; i++)
                {
                    if (_watchers[i].Id == id)
                    {
                        _watchers.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private void DispatchEvents(List<KeyChangeEvent> events)
        {
            if (events == null || events.Count == 0) return;
            Action<KeyChangeEvent>[] snapshot;
            lock (_watcherLock)
            {
                snapshot = new Action<KeyChangeEvent>[_watchers.Count];
                for (int i = 0; i < _watchers.Count; i++)
                {
                    snapshot[i] = _watchers[i].Callback;
                }
            }

            foreach (var ev in events)
            {
                KeyChanged?.Invoke(ev);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    // §3.6（API v1.0 冻结）：不吞 Watcher 回调异常，与 Rust/Go 的 panic
                    // 传播对齐。旧实现静默 catch，会让订阅方的 bug 永远不可见。
                    // 文档化语义：回调异常会中断后续监听器派发并传播给写操作调用方。
                    snapshot[i](ev);
                }
            }
        }

        /// <summary>
        /// <see cref="CkvDb.Watch"/> 返回的订阅句柄（API v1.0 §2.2）。
        /// 仅显式 <see cref="Dispose"/> 才移除回调；单纯丢弃句柄不注销，
        /// 因此既有的 <c>db.Watch(cb);</c> 写法在 v1.0 中行为不变。
        /// </summary>
        public sealed class WatcherHandle : IDisposable
        {
            private readonly CkvDb _owner;
            private readonly long _id;
            private bool _disposed;

            internal WatcherHandle(CkvDb owner, long id)
            {
                _owner = owner;
                _id = id;
            }

            /// <summary>该订阅在实例内的 id。</summary>
            public long Id => _id;

            /// <summary>移除关联的回调。幂等。</summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.RemoveWatcher(_id);
            }
        }

        /// <summary>
        /// <see cref="CkvDb.Stats"/> 返回的聚合统计（API v1.0 §2.4）。
        /// </summary>
        public sealed class DbStats
        {
            /// <summary>数据库文件物理字节数；尚未落盘的新库为 0。</summary>
            public long FileSize { get; }
            /// <summary>当前命名空间数量（含空命名空间）。</summary>
            public int NamespaceCount { get; }
            /// <summary>所有命名空间的存活条目总数。</summary>
            public int TotalEntries { get; }
            /// <summary>实例当前的 max TxID。</summary>
            public ulong CurrentMaxTxId { get; }

            internal DbStats(long fileSize, int namespaceCount, int totalEntries, ulong currentMaxTxId)
            {
                FileSize = fileSize;
                NamespaceCount = namespaceCount;
                TotalEntries = totalEntries;
                CurrentMaxTxId = currentMaxTxId;
            }
        }

        private CkvDb(string path, string canonicalPath, DbOptions options)
        {
            _path = path;
            _canonicalPath = canonicalPath;
            _options = options.Clone();
        }

        public static CkvDb Open(string path, DbOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            var opts = options ?? DbOptions.Default;
            string canonical = Path.GetFullPath(path);

            while (true)
            {
                if (Registry.TryGetValue(canonical, out var weakRef) && weakRef.TryGetTarget(out var existing))
                {
                    if (!existing._isDisposed)
                    {
                        return existing;
                    }
                }

                var db = new CkvDb(path, canonical, opts);
                db.InitAndRecover();

                var newWeakRef = new WeakReference<CkvDb>(db);
                if (Registry.TryAdd(canonical, newWeakRef))
                {
                    db._registered = true;
                    return db;
                }

                // If racing with another insert, loop again
                if (Registry.TryGetValue(canonical, out var racingRef) && racingRef.TryGetTarget(out var rDb))
                {
                    if (!rDb._isDisposed)
                    {
                        return rDb;
                    }
                    Registry.TryUpdate(canonical, newWeakRef, racingRef);
                    db._registered = true;
                    return db;
                }
            }
        }

        private void InitAndRecover()
        {
            string dbPath = _path;
            string tmpPath = _path + ".tmp";
            string bakPath = _path + ".bak";

            bool dbExists = File.Exists(dbPath);

            if (File.Exists(tmpPath))
            {
                // B6：无论主库是否存在都必须处理残留 .tmp。
                // 旧实现只在主库缺失时检查，导致主库存在时的残留 tmp 永不清理。
                if (!dbExists)
                {
                    try
                    {
                        byte[] tmpBytes = File.ReadAllBytes(tmpPath);
                        var (tmpNs, tmpTxId) = ParseDatabaseBytes(tmpBytes);
                        // Valid .tmp! Promote it
                        File.Move(tmpPath, dbPath);
                        ApplyParsed(tmpNs, tmpTxId, new FileInfo(dbPath).Length);
                        dbExists = true;
                    }
                    catch
                    {
                        // Invalid .tmp, remove it
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
                else
                {
                    // 主库完好：.tmp 是上一次写入崩溃/失败的残留，安全清理
                    // （规范 §4.2 启动自愈第 1 步）
                    try { File.Delete(tmpPath); } catch { }
                }
            }

            if (dbExists)
            {
                byte[] bytes = File.ReadAllBytes(dbPath);
                try
                {
                    var (ns, txId) = ParseDatabaseBytes(bytes);
                    ApplyParsed(ns, txId, bytes.Length);
                }
                catch (CorruptedError)
                {
                    if (_options.AutoRecoverFromBackup && File.Exists(bakPath))
                    {
                        try
                        {
                            byte[] bakBytes = File.ReadAllBytes(bakPath);
                            var (bakNs, bakTxId) = ParseDatabaseBytes(bakBytes);
                            // Backup healed! Overwrite corrupted dbPath
                            File.Copy(bakPath, dbPath, true);
                            ApplyParsed(bakNs, bakTxId, bakBytes.Length);
                            _options.OnWarning?.Invoke("onRecoveredFromBackup",
                                $"Recovered database '{_path}' from backup '{bakPath}'");
                            return;
                        }
                        catch
                        {
                            // Both corrupted
                            throw new CorruptedError(
                                $"Database '{_path}' and backup '{bakPath}' are both corrupted"
                            );
                        }
                    }
                    throw;
                }
            }
            else if (File.Exists(bakPath))
            {
                // 主库不存在但备份存在：晋升备份（与 Go/Rust 对等的两代自愈）
                try
                {
                    byte[] bakBytes = File.ReadAllBytes(bakPath);
                    var (bakNs, bakTxId) = ParseDatabaseBytes(bakBytes);
                    File.Copy(bakPath, dbPath, true);
                    ApplyParsed(bakNs, bakTxId, bakBytes.Length);
                    _options.OnWarning?.Invoke("onRecoveredFromBackup",
                        $"Recovered database '{_path}' from backup '{bakPath}'");
                }
                catch (CorruptedError)
                {
                    // 备份同样损坏且主库本就不存在：按新库处理，保留现场不清空
                }
            }

            // New database: _lastKnownDiskMaxTxId/_lastKnownDiskFileSize 保持 (0, 0)
        }

        /// <summary>将解析结果合入内存状态，并同步写前预检的磁盘状态基线。</summary>
        private void ApplyParsed(Dictionary<string, NamespaceStore> namespaces, ulong maxTxId, long fileSize)
        {
            _namespaces.Clear();
            foreach (var kvp in namespaces)
            {
                _namespaces[kvp.Key] = kvp.Value;
            }
            _currentMaxTxId = maxTxId;
            _lastKnownDiskMaxTxId = maxTxId;
            _lastKnownDiskFileSize = fileSize;
        }

        private static void ValidateDatabaseBytes(byte[] bytes)
        {
            if (bytes.Length < Constants.FileHeaderLen)
                throw new CorruptedError($"File size {bytes.Length} is less than header length 32");

            for (int i = 0; i < 8; i++)
            {
                if (bytes[i] != Constants.FileMagic[i])
                    throw new CorruptedError("Invalid file magic");
            }

            ushort readVer = BinaryHelper.ReadUInt16LE(bytes.AsSpan(8, 2));
            if (readVer > Constants.FormatReadVer)
                throw new UnsupportedVersionError($"Unsupported format read version {readVer}");

            uint storedHeaderCrc = BinaryHelper.ReadUInt32LE(bytes.AsSpan(28, 4));
            uint calcHeaderCrc = Crc32.Compute(bytes, 0, 28);
            if (storedHeaderCrc != calcHeaderCrc)
                throw new CorruptedError($"Header CRC mismatch: stored {storedHeaderCrc:X8}, calculated {calcHeaderCrc:X8}");
        }

        private void LoadFromBytes(byte[] bytes)
        {
            var (namespaces, maxTxId) = ParseDatabaseBytes(bytes);
            ApplyParsed(namespaces, maxTxId, bytes.Length);
        }

        private sealed class LoadEntry
        {
            public readonly int Seq;
            public readonly byte OpType;
            public readonly byte ValType;
            public readonly string Ns;
            public readonly byte[] Key;
            public readonly byte[] Val;

            public LoadEntry(int seq, byte opType, byte valType, string ns, byte[] key, byte[] val)
            {
                Seq = seq;
                OpType = opType;
                ValType = valType;
                Ns = ns;
                Key = key;
                Val = val;
            }
        }

        /// <summary>
        /// 全量解析数据库字节：文件头 + 逐帧 CRC/TxID 单调/OpCount 范围校验，
        /// 并重放全部 OpCode（OpPut/OpDelete/OpDropNamespace，规范 §3.3.4）。
        /// 快路径：全部条目为 OpPut（快照文件常态）时按命名空间分组追加，
        /// 稳定排序后同键去重（保留后写入者），整体 O(N log N)；
        /// 慢路径：含删除/清仓操作时保持顺序重放语义。
        /// </summary>
        private static (Dictionary<string, NamespaceStore> Namespaces, ulong MaxTxId) ParseDatabaseBytes(byte[] bytes)
        {
            ValidateDatabaseBytes(bytes);

            var parsed = new List<LoadEntry>(256);
            ulong maxTxId = 0;
            ulong lastTxId = 0;

            int offset = Constants.FileHeaderLen;
            while (offset < bytes.Length)
            {
                if (offset + Constants.FrameHeaderLen > bytes.Length)
                    throw new CorruptedError($"Incomplete frame header at offset {offset}");

                var frameSpan = bytes.AsSpan(offset, Constants.FrameHeaderLen);
                uint frameMagic = BinaryHelper.ReadUInt32LE(frameSpan.Slice(0, 4));
                if (frameMagic != Constants.FrameMagic)
                    throw new CorruptedError($"Invalid frame magic 0x{frameMagic:X8} at offset {offset}");

                uint storedHdrCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(4, 4));
                uint calcHdrCrc = Crc32.Compute(bytes, offset + 8, 20);
                if (storedHdrCrc != calcHdrCrc)
                    throw new CorruptedError($"Frame header CRC mismatch at offset {offset}");

                ulong txId = BinaryHelper.ReadUInt64LE(frameSpan.Slice(8, 8));
                if (txId <= lastTxId)
                    throw new CorruptedError(
                        $"Non-monotonic TxID {txId} (last was {lastTxId}) at offset {offset}");
                lastTxId = txId;

                uint opCount = BinaryHelper.ReadUInt32LE(frameSpan.Slice(16, 4));
                if (opCount == 0 || opCount > Constants.MaxOpsPerFrame)
                    throw new CorruptedError($"Invalid OpCount {opCount} at offset {offset}");

                uint payloadLen = BinaryHelper.ReadUInt32LE(frameSpan.Slice(20, 4));
                if (payloadLen > Constants.MaxPayloadLen)
                    throw new CorruptedError(
                        $"Payload length {payloadLen} exceeds limit {Constants.MaxPayloadLen} at offset {offset}");

                uint storedPayloadCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(24, 4));

                int payloadOffset = offset + Constants.FrameHeaderLen;
                if ((long)payloadOffset + payloadLen > bytes.Length)
                    throw new CorruptedError($"Incomplete frame payload at offset {payloadOffset}");

                uint calcPayloadCrc = Crc32.Compute(bytes, payloadOffset, (int)payloadLen);
                if (storedPayloadCrc != calcPayloadCrc)
                    throw new CorruptedError($"Frame payload CRC mismatch at offset {payloadOffset}");

                // Parse entries
                int pIdx = payloadOffset;
                int pEnd = payloadOffset + (int)payloadLen;
                for (uint op = 0; op < opCount; op++)
                {
                    if (pIdx + Constants.EntryFixedHeaderLen > pEnd)
                        throw new CorruptedError("Truncated entry fixed header");

                    byte opType = bytes[pIdx];
                    byte valType = bytes[pIdx + 1];
                    ushort nsLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 2, 2));
                    ushort keyLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 4, 2));
                    uint valLen = BinaryHelper.ReadUInt32LE(bytes.AsSpan(pIdx + 6, 4));
                    pIdx += Constants.EntryFixedHeaderLen;

                    if (pIdx + nsLen + keyLen + valLen > pEnd)
                        throw new CorruptedError("Truncated entry dynamic payload");

                    string ns = nsLen == 0 ? "" : Encoding.UTF8.GetString(bytes, pIdx, nsLen);
                    pIdx += nsLen;

                    byte[] keyBytes = new byte[keyLen];
                    Array.Copy(bytes, pIdx, keyBytes, 0, keyLen);
                    pIdx += keyLen;

                    byte[] valBytes = new byte[valLen];
                    Array.Copy(bytes, pIdx, valBytes, 0, (int)valLen);
                    pIdx += (int)valLen;

                    switch (opType)
                    {
                        case Constants.OpPut:
                        case Constants.OpDelete:
                        case Constants.OpDropNamespace:
                            break;
                        default:
                            throw new CorruptedError(
                                $"Unknown OpCode 0x{opType:X2} in frame at offset {offset}");
                    }

                    parsed.Add(new LoadEntry(parsed.Count, opType, valType, ns, keyBytes, valBytes));
                }

                if (txId > maxTxId)
                {
                    maxTxId = txId;
                }

                offset = payloadOffset + (int)payloadLen;
            }

            // 第二遍：判定是否存在非 Put 操作
            bool allPuts = true;
            foreach (var e in parsed)
            {
                if (e.OpType != Constants.OpPut)
                {
                    allPuts = false;
                    break;
                }
            }

            var namespaces = new Dictionary<string, NamespaceStore>(Utf8StringComparer.Instance);

            if (allPuts)
            {
                // 快路径：分组追加 + 一次稳定排序 + 同键去重（后写覆盖先写）
                var groups = new Dictionary<string, List<LoadEntry>>(Utf8StringComparer.Instance);
                foreach (var e in parsed)
                {
                    if (!groups.TryGetValue(e.Ns, out var list))
                    {
                        list = new List<LoadEntry>();
                        groups[e.Ns] = list;
                    }
                    list.Add(e);
                }

                foreach (var kvp in groups)
                {
                    var list = kvp.Value;
                    list.Sort((a, b) =>
                    {
                        int c = a.Key.AsSpan().SequenceCompareTo(b.Key.AsSpan());
                        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
                    });

                    var store = new NamespaceStore();
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0 && list[i].Key.AsSpan().SequenceEqual(list[i - 1].Key.AsSpan()))
                        {
                            // 稳定排序后同键块保持原先后顺序，保留最后一个（后写覆盖先写）
                            store.Items[store.Items.Count - 1] =
                                new KeyItem(list[i].Key, list[i].ValType, list[i].Val);
                        }
                        else
                        {
                            store.Items.Add(new KeyItem(list[i].Key, list[i].ValType, list[i].Val));
                        }
                    }
                    namespaces[kvp.Key] = store;
                }
            }
            else
            {
                // 慢路径：顺序重放，保持 put/delete/drop 交错语义
                foreach (var e in parsed)
                {
                    switch (e.OpType)
                    {
                        case Constants.OpPut:
                            if (!namespaces.TryGetValue(e.Ns, out var store))
                            {
                                store = new NamespaceStore();
                                namespaces[e.Ns] = store;
                            }
                            store.Put(e.Key, e.ValType, e.Val);
                            break;
                        case Constants.OpDelete:
                            if (namespaces.TryGetValue(e.Ns, out var delStore))
                            {
                                delStore.Delete(e.Key);
                            }
                            break;
                        case Constants.OpDropNamespace:
                            namespaces.Remove(e.Ns);
                            break;
                    }
                }
            }

            return (namespaces, maxTxId);
        }

        /// <summary>
        /// 轻量探测磁盘文件的最大帧 TxID（仅顺序读取帧头，不解码 payload）。
        /// 返回 Exists=false 表示文件不存在；探测失败按 (0, size) 处理，
        /// 与 lastKnown 比对将不匹配，从而拒绝写入（无法验证时不落盘）。
        /// </summary>
        private static (ulong MaxTxId, long FileSize, bool Exists) ProbeDiskState(string path)
        {
            if (!File.Exists(path)) return (0, 0, false);
            var fi = new FileInfo(path);
            long size = fi.Length;
            if (size <= Constants.FileHeaderLen) return (0, size, true);

            ulong lastTxId = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hdr = new byte[Constants.FrameHeaderLen];
                    long offset = Constants.FileHeaderLen;
                    while (offset < size)
                    {
                        if (size - offset < Constants.FrameHeaderLen)
                            throw new CorruptedError("Truncated frame in fast scan");
                        fs.Position = offset;
                        int read = 0;
                        while (read < hdr.Length)
                        {
                            int n = fs.Read(hdr, read, hdr.Length - read);
                            if (n <= 0) throw new CorruptedError("Truncated frame header in fast scan");
                            read += n;
                        }
                        uint magic = BinaryHelper.ReadUInt32LE(hdr.AsSpan(0, 4));
                        if (magic != Constants.FrameMagic)
                            throw new CorruptedError("Invalid frame magic in fast scan");
                        ulong txId = BinaryHelper.ReadUInt64LE(hdr.AsSpan(8, 8));
                        uint payloadLen = BinaryHelper.ReadUInt32LE(hdr.AsSpan(20, 4));
                        lastTxId = txId;
                        offset += Constants.FrameHeaderLen + payloadLen;
                    }
                }
            }
            catch (CorruptedError)
            {
                return (0, size, true);
            }
            catch (IOException)
            {
                return (0, size, true);
            }
            return (lastTxId, size, true);
        }

        /// <summary>
        /// 写路径第一阶段：在任何内存变更**之前**执行只读校验、获取进程间文件锁
        /// 并做规范 §4.1.3 的磁盘预检。抛出异常时尚未触碰任何内存状态，
        /// 因此调用方看到的错误与内存状态永远不会分叉。
        /// </summary>
        /// <returns>必须交给 <see cref="CommitFlush"/> 消费的锁句柄。</returns>
        private LockFile BeginFlush(out ulong diskMaxTxId)
        {
            if (_options.ReadOnly)
                // §3.3（API v1.0 冻结）：与 Rust DbError::Locked / Go LockedError 一致。
                // 旧实现抛 InvalidOperationException，与规范 §5.1.2 错误体系不符。
                throw new LockedError("Database is opened in read-only mode");

            // 先取锁再做预检，避免"预检通过 -> 取锁前被外部改写"的竞态窗口
            var lockHandle = LockFile.Acquire(_path, _options);

            var (diskTx, diskSize, diskExists) = ProbeDiskState(_path);
            if (diskExists)
            {
                // B1 修复：空库（仅 32B 文件头）磁盘无任何帧，探测 maxTxID 语义为 0，
                // 与内存 _currentMaxTxId（条目清空后严禁归零）不可比；
                // 空文件场景只比对文件大小，避免清空后再写入误报并发修改。
                bool stateMatches = diskSize == _lastKnownDiskFileSize &&
                    (diskSize == Constants.FileHeaderLen || diskTx == _lastKnownDiskMaxTxId);
                if ((_lastKnownDiskMaxTxId != 0 || _lastKnownDiskFileSize != 0) && !stateMatches)
                {
                    lockHandle.Dispose();
                    throw new ConcurrentModificationError(
                        $"Disk state changed externally: expected ({_lastKnownDiskMaxTxId}, {_lastKnownDiskFileSize}), found ({diskTx}, {diskSize})");
                }
            }

            diskMaxTxId = diskTx;
            return lockHandle;
        }

        /// <summary>
        /// 把当前内存状态按规范 §3.3 的确定性规则序列化为完整数据库文件字节
        /// （32B 文件头 + N 个 BatchFrame）。主写路径与 <see cref="Backup"/> 共用，
        /// 保证备份产物与正常落盘字节一致。
        /// </summary>
        /// <param name="diskMaxTxId">磁盘上当前的最大 TxID（用于 max(memory, disk) 规则）。</param>
        /// <param name="newMaxTxId">写出的最后一帧的 TxID。</param>
        private byte[] SerializeSnapshotBytes(ulong diskMaxTxId, out ulong newMaxTxId)
        {
            var ms = new MemoryStream();

            // 1. File Header (32 bytes)
            byte[] fileHeader = new byte[32];
            Array.Copy(Constants.FileMagic, 0, fileHeader, 0, 8);
            BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(8, 2), Constants.FormatReadVer);
            BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(10, 2), Constants.FormatWriteVer);
            BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(12, 4), 0); // Flags
            // 16..28 reserved 0x00
            uint headerCrc = Crc32.Compute(fileHeader, 0, 28);
            BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(28, 4), headerCrc);
            ms.Write(fileHeader, 0, 32);

            int totalEntries = 0;
            foreach (var kvp in _namespaces)
            {
                totalEntries += kvp.Value.Items.Count;
            }

            if (totalEntries == 0)
            {
                // 空库：仅 32B 文件头，TxID 保持不变（严禁归零，规范 §3.2.1）
                newMaxTxId = _currentMaxTxId;
                return ms.ToArray();
            }

            // 规范 §3.2.1：newTxID = max(memoryKnownMaxTxID, diskScannedMaxTxID) + 1
            ulong nextTxId = Math.Max(_currentMaxTxId, diskMaxTxId) + 1;

            var currentPayload = new MemoryStream();
            uint currentOpCount = 0;

            void flushFrame()
            {
                if (currentOpCount == 0) return;
                byte[] payloadBytes = currentPayload.ToArray();
                uint payloadLen = (uint)payloadBytes.Length;
                uint pCrc = Crc32.Compute(payloadBytes);

                byte[] frameHeader = new byte[28];
                BinaryHelper.WriteUInt32LE(frameHeader.AsSpan(0, 4), Constants.FrameMagic);

                byte[] tail20 = new byte[20];
                BinaryHelper.WriteUInt64LE(tail20.AsSpan(0, 8), nextTxId++);
                BinaryHelper.WriteUInt32LE(tail20.AsSpan(8, 4), currentOpCount);
                BinaryHelper.WriteUInt32LE(tail20.AsSpan(12, 4), payloadLen);
                BinaryHelper.WriteUInt32LE(tail20.AsSpan(16, 4), pCrc);

                uint fHdrCrc = Crc32.Compute(tail20);
                BinaryHelper.WriteUInt32LE(frameHeader.AsSpan(4, 4), fHdrCrc);
                Array.Copy(tail20, 0, frameHeader, 8, 20);

                ms.Write(frameHeader, 0, 28);
                ms.Write(payloadBytes, 0, payloadBytes.Length);

                currentPayload.SetLength(0);
                currentOpCount = 0;
            }

            // _namespaces 已按 UTF-8 字节序排列（Utf8StringComparer），枚举顺序即为
            // 规范要求的确定性排序，无需二次排序。
            foreach (var nsEntry in _namespaces)
            {
                byte[] nsBytes = Encoding.UTF8.GetBytes(nsEntry.Key);
                foreach (var item in nsEntry.Value.Items)
                {
                    int entryLen = Constants.EntryFixedHeaderLen + nsBytes.Length + item.Key.Length + item.Value.Length;
                    if (currentPayload.Length + entryLen > Constants.MaxPayloadLen || currentOpCount >= Constants.MaxOpsPerFrame)
                    {
                        flushFrame();
                    }

                    byte[] entryHdr = new byte[10];
                    entryHdr[0] = Constants.OpPut;
                    entryHdr[1] = item.ValueType;
                    BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(2, 2), (ushort)nsBytes.Length);
                    BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(4, 2), (ushort)item.Key.Length);
                    BinaryHelper.WriteUInt32LE(entryHdr.AsSpan(6, 4), (uint)item.Value.Length);

                    currentPayload.Write(entryHdr, 0, 10);
                    if (nsBytes.Length > 0) currentPayload.Write(nsBytes, 0, nsBytes.Length);
                    currentPayload.Write(item.Key, 0, item.Key.Length);
                    currentPayload.Write(item.Value, 0, item.Value.Length);
                    currentOpCount++;
                }
            }

            if (currentOpCount > 0)
            {
                flushFrame();
            }

            newMaxTxId = nextTxId - 1;
            return ms.ToArray();
        }

        /// <summary>
        /// 写路径第二阶段：落盘提交
        /// （.db -> .bak，.tmp -> .db）。消费 <see cref="BeginFlush"/> 取得的锁，
        /// 无论成功或失败都会释放它。
        /// </summary>
        private void CommitFlush(LockFile lockHandle, ulong diskMaxTxId)
        {
            using (lockHandle)
            {
                string tmpPath = _path + ".tmp";
                string bakPath = _path + ".bak";

                // Count total alive entries
                int totalEntries = 0;
                foreach (var kvp in _namespaces)
                {
                    totalEntries += kvp.Value.Items.Count;
                }

                // 序列化与落盘分离：Backup() 复用同一套确定性序列化（API v1.0 §2.5）
                byte[] finalData = SerializeSnapshotBytes(diskMaxTxId, out ulong newMaxTxId);
                _currentMaxTxId = newMaxTxId;

                // Write to .tmp with fsync
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(finalData, 0, finalData.Length);
                    if (_options.SyncOnWrite)
                    {
                        fs.Flush(true);
                    }
                }

                // 两代原子轮转（规范 §4.2）：覆盖式原子替换。
                // 旧实现用 File.Delete(bakPath) + File.Move(db, bak)，中间存在
                // "两代备份同时不存在"的窗口；rename 覆盖在 Windows/POSIX 上都只是
                // 毫秒级元数据修改，写放大为 0%，即使磁盘剩余空间为 0 也能完成。
                if (File.Exists(_path))
                {
                    AtomicReplaceWithRetry(_path, bakPath);
                }
                AtomicReplaceWithRetry(tmpPath, _path);

                // 轮换完成后刷新预检基线，与 Go/Rust 的 lastKnownDiskState 语义一致
                var outFi = new FileInfo(_path);
                _lastKnownDiskFileSize = outFi.Length;
                _lastKnownDiskMaxTxId = _currentMaxTxId;
            }
        }

        /// <summary>
        /// 原子地把 <paramref name="src"/> 覆盖到 <paramref name="dst"/>。
        /// 单次系统调用完成替换，不会出现"先删目标再移动"的两代备份消失窗口。
        /// </summary>
        private static void AtomicReplace(string src, string dst)
        {
            if (!File.Exists(dst))
            {
                File.Move(src, dst);
                return;
            }
#if NETCOREAPP3_0_OR_GREATER
            File.Move(src, dst, overwrite: true);
#else
            // .NET Framework / netstandard2.0 没有三参 File.Move，
            // File.Replace 语义等价（destinationBackupFileName 传 null 表示丢弃旧内容）。
            try
            {
                File.Replace(src, dst, null);
            }
            catch (FileNotFoundException)
            {
                // 目标在检查后被删除：退化为普通移动
                File.Move(src, dst);
            }
#endif
        }

        /// <summary>
        /// 带 Windows 退避重试的覆盖式原子替换（规范 §4.2：5/15/30/60/120ms 共 5 次）。
        /// 重试耗尽后退化为拷贝兜底：非原子，但目标内容始终完整，
        /// 不会留下"两代备份都不存在"的窗口。
        /// </summary>
        private static void AtomicReplaceWithRetry(string src, string dst)
        {
            int[] backoffs = { 5, 15, 30, 60, 120 };
            for (int attempt = 0; attempt < backoffs.Length; attempt++)
            {
                try
                {
                    AtomicReplace(src, dst);
                    return;
                }
                catch (IOException)
                {
                    if (attempt == backoffs.Length - 1) break;
                    Thread.Sleep(backoffs[attempt]);
                }
            }
            File.Copy(src, dst, true);
            try { File.Delete(src); } catch { }
        }

        /// <summary>
        /// 按确定性排序规则重写为紧凑快照文件，TxID = currentMaxTxID + 1（规范 §5.2.2）。
        /// </summary>
        public void Compact()
        {
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var compactDiskMaxTxId);
                CommitFlush(lockHandle, compactDiskMaxTxId);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 显式把内存状态落盘为紧凑快照（API v1.0 §2.3）。
        /// 与 <see cref="Compact"/> 完全等价，二者均为冻结名称。
        /// </summary>
        public void Flush()
        {
            Compact();
        }

        /// <summary>
        /// 按 UTF-8 无符号字节序返回所有命名空间（API v1.0 §2.1）。
        /// 默认命名空间为 ""；clear_namespace 之后的空命名空间仍然列出。
        /// 非 UTF-8 的命名空间名（只能由外部文件引入）跳过。
        /// </summary>
        public List<string> Namespaces()
        {
            _rwLock.EnterReadLock();
            try
            {
                var list = new List<string>(_namespaces.Count);
                foreach (var key in _namespaces.Keys)
                {
                    // §3.7：与 Keys() 一致，跳过非 UTF-8 名称
                    byte[] raw = Encoding.UTF8.GetBytes(key);
                    if (Utf8Helper.IsValid(raw))
                    {
                        list.Add(key);
                    }
                }
                // _namespaces 本身已按 Utf8StringComparer（UTF-8 字节序）排列
                return list;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// 数据库文件当前物理字节数；尚未落盘的新库返回 0（API v1.0 §2.4）。
        /// </summary>
        public long FileSize()
        {
            var fi = new FileInfo(_path);
            return fi.Exists ? fi.Length : 0;
        }

        /// <summary>
        /// 纯内存聚合统计，不触发磁盘 I/O、不修改状态（API v1.0 §2.4）。
        /// </summary>
        public DbStats Stats()
        {
            _rwLock.EnterReadLock();
            try
            {
                int total = 0;
                foreach (var store in _namespaces.Values)
                {
                    total += store.Items.Count;
                }
                return new DbStats(FileSize(), _namespaces.Count, total, _currentMaxTxId);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// 把当前内存状态写入 <paramref name="dest"/> 作为独立数据库文件（API v1.0 §2.5）。
        /// 产出文件与正常库字节兼容，可被任意 CKV 实现打开。
        /// 经 dest + ".tmp" + fsync + 原子 rename 完成，不触碰本库的 .bak。
        /// 只读模式下允许执行：备份不修改源库。
        /// </summary>
        public void Backup(string dest)
        {
            if (string.IsNullOrWhiteSpace(dest))
                throw new ArgumentException("Destination cannot be null or empty", nameof(dest));

            byte[] payload;
            _rwLock.EnterReadLock();
            try
            {
                // 复用主写路径的确定性序列化；diskMaxTxId 传 0，因为备份是独立文件，
                // 其 TxID 只从本实例的 currentMaxTxId 继续递增。
                payload = SerializeSnapshotBytes(0, out _);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            string tmpPath = dest + ".tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload, 0, payload.Length);
                if (_options.SyncOnWrite)
                {
                    fs.Flush(true);
                }
            }
            AtomicReplaceWithRetry(tmpPath, dest);
        }

        /// <summary>
        /// 按 UTF-8 无符号字节序返回匹配前缀的条目（API v1.0 §2.6）。
        /// 缓冲式语义：内部一次性收集后逐个返回（三语言一致的刻意选择）。
        /// </summary>
        public List<Entry> IterPrefix(string? ns, string? prefix = null)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? prefixBytes = string.IsNullOrEmpty(prefix) ? null : Encoding.UTF8.GetBytes(prefix);

            _rwLock.EnterReadLock();
            try
            {
                var list = new List<Entry>();
                if (!_namespaces.TryGetValue(normNs, out var store)) return list;
                foreach (var item in store.Items)
                {
                    if (prefixBytes == null || item.Key.AsSpan().StartsWith(prefixBytes.AsSpan()))
                    {
                        byte[] copyVal = new byte[item.Value.Length];
                        Array.Copy(item.Value, 0, copyVal, 0, copyVal.Length);
                        list.Add(new Entry(normNs, Encoding.UTF8.GetString(item.Key), item.ValueType, copyVal));
                    }
                }
                return list;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        // §3.5：长度校验已移至 Limits（BatchWriter 与 CkvDb 共用）        }

        // ================= API Implementation =================

        public byte[]? GetBytes(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                byte[] res = new byte[store.Items[idx].Value.Length];
                Array.Copy(store.Items[idx].Value, 0, res, 0, res.Length);
                return res;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public string? GetString(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeString)
                    throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected string (0x01)");
                if (!Utf8Helper.IsValid(item.Value))
                    throw new InvalidUtf8Error($"Invalid UTF-8 sequence in key '{key}'");
                return Encoding.UTF8.GetString(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public long? GetInt64(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeInt64)
                    throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected int64 (0x02)");
                return BinaryHelper.DecodeInt64(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public double? GetFloat64(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeFloat64)
                    throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected float64 (0x03)");
                return BinaryHelper.DecodeFloat64(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public bool? GetBool(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return null;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return null;
                var item = store.Items[idx];
                if (item.ValueType != Constants.ValTypeBool)
                    throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected bool (0x04)");
                return BinaryHelper.DecodeBool(item.Value);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public bool ContainsKey(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return false;
                return store.FindIndex(keyBytes) >= 0;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public int Count(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            _rwLock.EnterReadLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return 0;
                return store.Items.Count;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// 创建时间点快照。CoW 隔离：快照仅共享 NamespaceStore 引用，
        /// 写路径在 _snapshotCount > 0 时才克隆，因此创建本身是 O(命名空间数)。
        /// 计数通过 Snapshot.Dispose 显式释放；未释放时写路径退化为克隆模式
        /// （正确性不受影响，仅性能下降）。
        /// </summary>
        public Snapshot CreateSnapshot()
        {
            _rwLock.EnterReadLock();
            try
            {
                lock (_snapshotLock)
                {
                    _snapshotCount++;
                }
                var snapNs = new Dictionary<string, NamespaceStore>(_namespaces.Count, Utf8StringComparer.Instance);
                foreach (var kvp in _namespaces)
                {
                    snapNs[kvp.Key] = kvp.Value;
                }
                return new Snapshot(_currentMaxTxId, snapNs, this);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>快照释放时递减存活计数（由 Snapshot.Dispose 调用，且仅一次）。</summary>
        internal void ReleaseSnapshot()
        {
            lock (_snapshotLock)
            {
                if (_snapshotCount > 0)
                {
                    _snapshotCount--;
                }
            }
        }

        private NamespaceStore GetOrCreateStoreForWrite(string normNs)
        {
            if (!_namespaces.TryGetValue(normNs, out var store))
            {
                store = new NamespaceStore();
                _namespaces[normNs] = store;
                return store;
            }
            if (_snapshotCount > 0)
            {
                // 存在存活快照：CoW 克隆，冻结旧列表
                store = store.Clone();
                _namespaces[normNs] = store;
            }
            // 无快照：原地修改，避免每次写 O(N) 克隆
            return store;
        }

        public void PutBytes(string? ns, string key, byte[] val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, val);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var diskMaxTxId);
                var store = GetOrCreateStoreForWrite(normNs);
                store.Put(keyBytes, Constants.ValTypeBytes, val);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeBytes)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutString(string? ns, string key, string val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            byte[] valBytes = Encoding.UTF8.GetBytes(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var diskMaxTxId);
                var store = GetOrCreateStoreForWrite(normNs);
                store.Put(keyBytes, Constants.ValTypeString, valBytes);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeString)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutInt64(string? ns, string key, long val)
        {
            byte[] valBytes = BinaryHelper.EncodeInt64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var diskMaxTxId);
                var store = GetOrCreateStoreForWrite(normNs);
                store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeInt64)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutFloat64(string? ns, string key, double val)
        {
            byte[] valBytes = BinaryHelper.EncodeFloat64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var diskMaxTxId);
                var store = GetOrCreateStoreForWrite(normNs);
                store.Put(keyBytes, Constants.ValTypeFloat64, valBytes);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeFloat64)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void PutBool(string? ns, string key, bool val)
        {
            byte[] valBytes = BinaryHelper.EncodeBool(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                var lockHandle = BeginFlush(out var diskMaxTxId);
                var store = GetOrCreateStoreForWrite(normNs);
                store.Put(keyBytes, Constants.ValTypeBool, valBytes);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeBool)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public long IncrementInt64(string? ns, string key, long delta)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, Array.Empty<byte>());

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                // 只读校验/取锁/磁盘预检先于任何内存修改
                var lockHandle = BeginFlush(out var diskMaxTxId);
                long newVal;
                if (!_namespaces.TryGetValue(normNs, out var existingStore))
                {
                    var store = new NamespaceStore();
                    _namespaces[normNs] = store;
                    newVal = delta;
                    byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                    store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                }
                else
                {
                    int idx = existingStore.FindIndex(keyBytes);
                    if (idx < 0)
                    {
                        var store = existingStore;
                        if (_snapshotCount > 0)
                        {
                            // 存在存活快照：CoW 克隆，冻结旧列表
                            store = existingStore.Clone();
                            _namespaces[normNs] = store;
                        }
                        newVal = delta;
                        byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                        store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                    }
                    else
                    {
                        var item = existingStore.Items[idx];
                        if (item.ValueType != Constants.ValTypeInt64)
                        {
                            throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected int64 (0x02)");
                        }
                        long current = BinaryHelper.DecodeInt64(item.Value);
                        newVal = unchecked(current + delta);
                        var store = existingStore;
                        if (_snapshotCount > 0)
                        {
                            // 存在存活快照：CoW 克隆，冻结旧列表
                            store = existingStore.Clone();
                            _namespaces[normNs] = store;
                        }
                        byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                        store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                    }
                }

                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, Constants.ValTypeInt64)
                };
                return newVal;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool Delete(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store)) return false;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return false;
                var lockHandle = BeginFlush(out var diskMaxTxId);
                if (_snapshotCount > 0)
                {
                    // 存在存活快照：CoW 克隆，冻结旧列表
                    store = store.Clone();
                    _namespaces[normNs] = store;
                }
                bool deleted = store.Delete(keyBytes);
                if (deleted)
                {
                    CommitFlush(lockHandle, diskMaxTxId);
                    events = new List<KeyChangeEvent>
                    {
                        new KeyChangeEvent(KeyChangeOp.Delete, normNs, key)
                    };
                }
                return deleted;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool DropNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.ContainsKey(normNs)) return false;
                var lockHandle = BeginFlush(out var diskMaxTxId);
                _namespaces.Remove(normNs);
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.DropNamespace, normNs, string.Empty)
                };
                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public bool ClearNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (!_namespaces.TryGetValue(normNs, out var store) || store.Items.Count == 0)
                {
                    return false;
                }
                var lockHandle = BeginFlush(out var diskMaxTxId);
                _namespaces[normNs] = new NamespaceStore();
                CommitFlush(lockHandle, diskMaxTxId);
                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Clear, normNs, string.Empty)
                };
                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public List<string> Keys(string? ns, string? prefix = null)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? prefixBytes = string.IsNullOrEmpty(prefix) ? null : Encoding.UTF8.GetBytes(prefix);

            _rwLock.EnterReadLock();
            try
            {
                var list = new List<string>();
                if (!_namespaces.TryGetValue(normNs, out var store)) return list;

                foreach (var item in store.Items)
                {
                    if (prefixBytes == null || item.Key.AsSpan().StartsWith(prefixBytes.AsSpan()))
                    {
                        if (Utf8Helper.IsValid(item.Key))
                        {
                            list.Add(Encoding.UTF8.GetString(item.Key));
                        }
                    }
                }
                return list;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public List<Entry> ScanRange(string? ns, string? startKey = null, string? endKey = null)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? startBytes = startKey != null ? Encoding.UTF8.GetBytes(startKey) : null;
            byte[]? endBytes = endKey != null ? Encoding.UTF8.GetBytes(endKey) : null;

            if (startBytes != null && endBytes != null && startBytes.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
            {
                return new List<Entry>();
            }

            _rwLock.EnterReadLock();
            try
            {
                var result = new List<Entry>();
                if (!_namespaces.TryGetValue(normNs, out var store)) return result;

                int startIdx = 0;
                if (startBytes != null)
                {
                    int found = store.FindIndex(startBytes);
                    startIdx = found >= 0 ? found : ~found;
                }

                for (int i = startIdx; i < store.Items.Count; i++)
                {
                    var item = store.Items[i];
                    if (endBytes != null && item.Key.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
                    {
                        break;
                    }
                    byte[] copyVal = new byte[item.Value.Length];
                    Array.Copy(item.Value, 0, copyVal, 0, copyVal.Length);
                    result.Add(new Entry(normNs, Encoding.UTF8.GetString(item.Key), item.ValueType, copyVal));
                }

                return result;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public void Batch(Action<BatchWriter> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var writer = new BatchWriter(this);
            action(writer);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                // 第一阶段（只读校验 / 取锁 / 磁盘预检）先于任何 op 应用：
                // batch 要么整体生效，要么完全不生效。
                var lockHandle = BeginFlush(out var diskMaxTxId);
                events = new List<KeyChangeEvent>();
                foreach (var op in writer.Ops)
                {
                    if (op.OpType == Constants.OpPut)
                    {
                        var store = GetOrCreateStoreForWrite(op.Namespace);
                        store.Put(op.Key, op.ValType, op.Val);
                        events.Add(new KeyChangeEvent(KeyChangeOp.Put, op.Namespace, Encoding.UTF8.GetString(op.Key), op.ValType));
                    }
                    else if (op.OpType == Constants.OpDelete)
                    {
                        // B3：仅当键真实存在并被删除时才派发事件，与单条 Delete 语义一致
                        if (_namespaces.TryGetValue(op.Namespace, out var store))
                        {
                            int idx = store.FindIndex(op.Key);
                            if (idx >= 0)
                            {
                                if (_snapshotCount > 0)
                                {
                                    // 存在存活快照：CoW 克隆，冻结旧列表
                                    store = store.Clone();
                                    _namespaces[op.Namespace] = store;
                                }
                                store.Delete(op.Key);
                                events.Add(new KeyChangeEvent(KeyChangeOp.Delete, op.Namespace, Encoding.UTF8.GetString(op.Key)));
                            }
                        }
                    }
                    else if (op.OpType == Constants.OpDropNamespace)
                    {
                        // B3：仅当命名空间真实存在时才派发事件
                        if (_namespaces.Remove(op.Namespace))
                        {
                            events.Add(new KeyChangeEvent(KeyChangeOp.DropNamespace, op.Namespace, string.Empty));
                        }
                    }
                    else if (op.OpType == Constants.BatchOpClearStaging)
                    {
                        // B3：仅当命名空间存在且非空时才派发事件（聚合事件 Key 为空串）
                        if (_namespaces.TryGetValue(op.Namespace, out var store) && store.Items.Count > 0)
                        {
                            _namespaces[op.Namespace] = new NamespaceStore();
                            events.Add(new KeyChangeEvent(KeyChangeOp.Clear, op.Namespace, string.Empty));
                        }
                    }
                }
                CommitFlush(lockHandle, diskMaxTxId);
            }
            finally
            {
                _rwLock.ExitWriteLock();
                if (events != null)
                {
                    DispatchEvents(events);
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_registered)
            {
                Registry.TryRemove(_canonicalPath, out _);
            }
            _rwLock.Dispose();
        }
    }

    /// <summary>
    /// Key / Value 长度校验（API v1.0 §3.4 / §3.5）。
    /// CkvDb 与 BatchWriter 共用，因此是独立的内部静态类而非 CkvDb 成员。
    /// </summary>
    internal static class Limits
    {
        public static void ValidateKeyAndValueLengths(byte[] key, byte[] val)
        {
            if (key.Length == 0 && val.Length == 0) return; // Empty key and empty val are permitted
            if (key.Length > Constants.MaxKeyLen)
                throw new KeyTooLargeError($"Key length {key.Length} exceeds maximum {Constants.MaxKeyLen}");
            if (val.Length > Constants.MaxValLen)
                // §3.4（API v1.0 冻结）：Value 超限与 Key 超限同为 KeyTooLargeError。
                // 旧实现抛通用 DbError，订阅方无法按类型分流。
                throw new KeyTooLargeError($"Value length {val.Length} exceeds maximum {Constants.MaxValLen}");
        }
    }

    public sealed class BatchWriter
    {
        private readonly CkvDb _db;
        internal List<BatchOp> Ops { get; } = new List<BatchOp>();

        internal BatchWriter(CkvDb db)
        {
            _db = db;
        }

        public BatchWriter PutBytes(string? ns, string key, byte[] val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            // §3.5（API v1.0 冻结）：构造期即校验长度，与 Rust Batch::put_* /
            // Go BatchWriter.* 对齐。旧实现直到提交才可能暴露超限。
            Limits.ValidateKeyAndValueLengths(keyBytes, val);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeBytes, val));
            return this;
        }

        public BatchWriter PutString(string? ns, string key, string val)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = Encoding.UTF8.GetBytes(val);
            // §3.5（API v1.0 冻结）：构造期即校验长度，与 Rust Batch::put_* /
            // Go BatchWriter.* 对齐。旧实现直到提交才可能暴露超限。
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeString, valBytes));
            return this;
        }

        public BatchWriter PutInt64(string? ns, string key, long val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeInt64(val);
            // §3.5（API v1.0 冻结）：构造期即校验长度，与 Rust Batch::put_* /
            // Go BatchWriter.* 对齐。旧实现直到提交才可能暴露超限。
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeInt64, valBytes));
            return this;
        }

        public BatchWriter PutFloat64(string? ns, string key, double val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeFloat64(val);
            // §3.5（API v1.0 冻结）：构造期即校验长度，与 Rust Batch::put_* /
            // Go BatchWriter.* 对齐。旧实现直到提交才可能暴露超限。
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeFloat64, valBytes));
            return this;
        }

        public BatchWriter PutBool(string? ns, string key, bool val)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] valBytes = BinaryHelper.EncodeBool(val);
            // §3.5（API v1.0 冻结）：构造期即校验长度，与 Rust Batch::put_* /
            // Go BatchWriter.* 对齐。旧实现直到提交才可能暴露超限。
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            Ops.Add(new BatchOp(Constants.OpPut, normNs, keyBytes, Constants.ValTypeBool, valBytes));
            return this;
        }

        public BatchWriter Delete(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            // §3.5：构造期即校验 Key 长度
            Limits.ValidateKeyAndValueLengths(keyBytes, Array.Empty<byte>());
            Ops.Add(new BatchOp(Constants.OpDelete, normNs, keyBytes, 0, Array.Empty<byte>()));
            return this;
        }

        public BatchWriter DropNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            Ops.Add(new BatchOp(Constants.OpDropNamespace, normNs, Array.Empty<byte>(), 0, Array.Empty<byte>()));
            return this;
        }

        public BatchWriter ClearNamespace(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            // 内部暂存码：clearNamespace 是纯内存 Staging 操作，不进入二进制格式
            Ops.Add(new BatchOp(Constants.BatchOpClearStaging, normNs, Array.Empty<byte>(), 0, Array.Empty<byte>()));
            return this;
        }
    }

    internal sealed class BatchOp
    {
        public byte OpType { get; }
        public string Namespace { get; }
        public byte[] Key { get; }
        public byte ValType { get; }
        public byte[] Val { get; }

        public BatchOp(byte opType, string @namespace, byte[] key, byte valType, byte[] val)
        {
            OpType = opType;
            Namespace = @namespace;
            Key = key;
            ValType = valType;
            Val = val;
        }
    }

    /// <summary>
    /// An immutable, point-in-time snapshot of the database providing read-only isolation.
    /// Readers holding this snapshot never block concurrent writes, and concurrent writes
    /// never affect this snapshot.
    /// </summary>
    public sealed class Snapshot : IDisposable
    {
        private readonly ulong _txId;
        private readonly CkvDb? _owner;
        private Dictionary<string, NamespaceStore>? _namespaces;
        private bool _isDisposed;

        /// <summary>
        /// §3.8（API v1.0 冻结）：快照 Dispose 后 <c>_namespaces</c> 置空，
        /// 读操作统一返回「不存在」而非抛错。用空字典兜底，各读方法无需逐个判空。
        /// </summary>
        private Dictionary<string, NamespaceStore> Stores =>
            _namespaces ?? EmptyStores;

        private static readonly Dictionary<string, NamespaceStore> EmptyStores =
            new Dictionary<string, NamespaceStore>();

        public ulong TxId => _txId;

        internal Snapshot(ulong txId, Dictionary<string, NamespaceStore> namespaces, CkvDb owner)
        {
            _txId = txId;
            _namespaces = namespaces;
            _owner = owner;
        }

        public bool ContainsKey(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return false;
            return store.FindIndex(keyBytes) >= 0;
        }

        public int Count(string? ns)
        {
            ThrowIfDisposed();
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            if (!Stores.TryGetValue(normNs, out var store)) return 0;
            return store.Items.Count;
        }

        public byte[]? GetBytes(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return null;
            int idx = store.FindIndex(keyBytes);
            if (idx < 0) return null;
            byte[] res = new byte[store.Items[idx].Value.Length];
            Array.Copy(store.Items[idx].Value, 0, res, 0, res.Length);
            return res;
        }

        public string? GetString(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return null;
            int idx = store.FindIndex(keyBytes);
            if (idx < 0) return null;
            var item = store.Items[idx];
            if (item.ValueType != Constants.ValTypeString)
                throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected string (0x01)");
            if (!Utf8Helper.IsValid(item.Value))
                throw new InvalidUtf8Error($"Invalid UTF-8 sequence in key '{key}'");
            return Encoding.UTF8.GetString(item.Value);
        }

        public long? GetInt64(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return null;
            int idx = store.FindIndex(keyBytes);
            if (idx < 0) return null;
            var item = store.Items[idx];
            if (item.ValueType != Constants.ValTypeInt64)
                throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected int64 (0x02)");
            return BinaryHelper.DecodeInt64(item.Value);
        }

        public double? GetFloat64(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return null;
            int idx = store.FindIndex(keyBytes);
            if (idx < 0) return null;
            var item = store.Items[idx];
            if (item.ValueType != Constants.ValTypeFloat64)
                throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected float64 (0x03)");
            return BinaryHelper.DecodeFloat64(item.Value);
        }

        public bool? GetBool(string? ns, string key)
        {
            ThrowIfDisposed();
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!Stores.TryGetValue(normNs, out var store)) return null;
            int idx = store.FindIndex(keyBytes);
            if (idx < 0) return null;
            var item = store.Items[idx];
            if (item.ValueType != Constants.ValTypeBool)
                throw new TypeMismatchError($"Key '{key}' has value_type {item.ValueType}, expected bool (0x04)");
            return BinaryHelper.DecodeBool(item.Value);
        }

        /// <summary>
        /// 与本库 <see cref="CkvDb.Namespaces"/> 同构（API v1.0 §2.1）。
        /// </summary>
        public List<string> Namespaces()
        {
            ThrowIfDisposed();
            var list = new List<string>();
            if (_namespaces == null) return list;
            foreach (var key in _namespaces.Keys)
            {
                byte[] raw = Encoding.UTF8.GetBytes(key);
                if (Utf8Helper.IsValid(raw))
                {
                    list.Add(key);
                }
            }
            return list;
        }

        /// <summary>
        /// 与本库 <see cref="CkvDb.IterPrefix"/> 同构（API v1.0 §2.6）。
        /// </summary>
        public List<Entry> IterPrefix(string? ns, string? prefix = null)
        {
            ThrowIfDisposed();
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? prefixBytes = string.IsNullOrEmpty(prefix) ? null : Encoding.UTF8.GetBytes(prefix);
            var list = new List<Entry>();
            if (_namespaces == null || !_namespaces.TryGetValue(normNs, out var store)) return list;
            foreach (var item in store.Items)
            {
                if (prefixBytes == null || item.Key.AsSpan().StartsWith(prefixBytes.AsSpan()))
                {
                    byte[] copyVal = new byte[item.Value.Length];
                    Array.Copy(item.Value, 0, copyVal, 0, copyVal.Length);
                    list.Add(new Entry(normNs, Encoding.UTF8.GetString(item.Key), item.ValueType, copyVal));
                }
            }
            return list;
        }

        public List<string> Keys(string? ns, string? prefix = null)
        {
            ThrowIfDisposed();
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? prefixBytes = string.IsNullOrEmpty(prefix) ? null : Encoding.UTF8.GetBytes(prefix);
            var list = new List<string>();
            if (!Stores.TryGetValue(normNs, out var store)) return list;

            foreach (var item in store.Items)
            {
                if (prefixBytes == null || item.Key.AsSpan().StartsWith(prefixBytes.AsSpan()))
                {
                    if (Utf8Helper.IsValid(item.Key))
                    {
                        list.Add(Encoding.UTF8.GetString(item.Key));
                    }
                }
            }
            return list;
        }

        public List<Entry> ScanRange(string? ns, string? startKey = null, string? endKey = null)
        {
            ThrowIfDisposed();
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[]? startBytes = startKey != null ? Encoding.UTF8.GetBytes(startKey) : null;
            byte[]? endBytes = endKey != null ? Encoding.UTF8.GetBytes(endKey) : null;

            if (startBytes != null && endBytes != null && startBytes.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
            {
                return new List<Entry>();
            }

            var result = new List<Entry>();
            if (!Stores.TryGetValue(normNs, out var store)) return result;

            int startIdx = 0;
            if (startBytes != null)
            {
                int found = store.FindIndex(startBytes);
                startIdx = found >= 0 ? found : ~found;
            }

            for (int i = startIdx; i < store.Items.Count; i++)
            {
                var item = store.Items[i];
                if (endBytes != null && item.Key.AsSpan().SequenceCompareTo(endBytes.AsSpan()) >= 0)
                {
                    break;
                }
                byte[] copyVal = new byte[item.Value.Length];
                Array.Copy(item.Value, 0, copyVal, 0, copyVal.Length);
                result.Add(new Entry(normNs, Encoding.UTF8.GetString(item.Key), item.ValueType, copyVal));
            }
            return result;
        }

        /// <summary>
        /// §3.8（API v1.0 冻结）：快照关闭后的读语义统一为「返回不存在」，不抛错。
        /// .NET 旧实现抛 ObjectDisposedException；Rust（无 Close）/ Go（返回空）
        /// 本就不抛。统一后三语言一致，且对「关闭后误查询」更温和。
        /// Dispose 已将 _namespaces 置空，各读方法天然走到 not-found 分支。
        /// </summary>
        private void ThrowIfDisposed()
        {
            // 刻意不再抛 ObjectDisposedException。
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            // 释放宿主数据库的存活快照计数（仅一次，由 _isDisposed 保证）
            _owner?.ReleaseSnapshot();
            _namespaces = null;
        }
    }
}
