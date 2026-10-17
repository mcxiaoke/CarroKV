using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
#if NET8_0_OR_GREATER
using Microsoft.Win32.SafeHandles;
#endif

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

        /// <summary>
        /// Σ(key.Length + value.Length) over <see cref="Items"/>，由 <see cref="Put"/> /
        /// <see cref="Delete"/> 增量维护。死键比例压实触发器依赖它保持每次突变 O(1)；
        /// 批量装载等直接写 <see cref="Items"/> 的路径必须调用 <see cref="RecalculateLiveBytes"/>。
        /// </summary>
        public long LiveBytes { get; private set; }

        /// <summary>按当前 <see cref="Items"/> 重算账本（仅用于绕过 Put/Delete 的批量装载路径）。</summary>
        public void RecalculateLiveBytes()
        {
            long total = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                total += Items[i].Key.Length + Items[i].Value.Length;
            }
            LiveBytes = total;
        }

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
                LiveBytes += val.Length - Items[idx].Value.Length;
                Items[idx] = new KeyItem(key, valType, val);
            }
            else
            {
                Items.Insert(~idx, new KeyItem(key, valType, val));
                LiveBytes += key.Length + val.Length;
            }
        }

        public bool Delete(ReadOnlySpan<byte> key)
        {
            int idx = FindIndex(key);
            if (idx >= 0)
            {
                var old = Items[idx];
                LiveBytes -= old.Key.Length + old.Value.Length;
                Items.RemoveAt(idx);
                return true;
            }
            return false;
        }

        public NamespaceStore Clone()
        {
            var clone = new NamespaceStore();
            clone.Items.AddRange(Items);
            clone.LiveBytes = LiveBytes;
            return clone;
        }
    }

    public sealed class CkvDb : IDisposable
    {
        /// <summary>
        /// 注册表键的路径比较器（C11）：Windows 的文件系统大小写不敏感，其余平台大小写
        /// **敏感**。旧实现无条件使用 <see cref="StringComparer.OrdinalIgnoreCase"/>，在
        /// Linux / macOS 上会把 <c>/data/Foo.db</c> 与 <c>/data/foo.db</c> 错误地合并成
        /// 同一个实例——两个互不相干的数据库被当成一个。
        /// </summary>
        private static StringComparer PathComparer() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static readonly ConcurrentDictionary<string, WeakReference<CkvDb>> Registry =
            new ConcurrentDictionary<string, WeakReference<CkvDb>>(PathComparer());

        /// <summary>
        /// 规范化路径（C11）：绝对化 + 解析符号链接，与 Rust 的 <c>canonicalize_path</c>
        /// 等价，使 <c>/data/link.db</c> 与 <c>/data/real.db</c> 归一到同一个注册表键。
        /// netstandard2.0 没有链接解析 API，该目标框架下退化为绝对路径。
        /// </summary>
        internal static string CanonicalizePath(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
#if NET6_0_OR_GREATER
            try
            {
                FileSystemInfo? target = File.ResolveLinkTarget(full, returnFinalTarget: true);
                if (target != null)
                {
                    return System.IO.Path.GetFullPath(target.FullName);
                }
            }
            catch
            {
                // 链接不可解析（不存在、权限不足）：退回绝对路径
            }
#endif
            return full;
        }

        private readonly CkvDb _root;
        private int _refCount = 1;
        private int _disposed = 0;
        private bool _isClosed = false;

        internal CkvDb Root => _root;
        public string Path => _root._path;
        public DbOptions Options => _root._options;
        public bool IsClosed => _disposed != 0 || _root._isClosed;

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
        /// <summary>FIX-07：注册时写入的自身弱引用，Dispose 时用它做“按键+按值”移除，
        /// 避免误删同路径上另一个更新实例的注册条目。</summary>
        private WeakReference<CkvDb>? _selfWeak;
        private bool _isDirty;
        private readonly Timer? _syncTimer;

        /// <summary>
        /// FIX-01：串行化所有写 <c>&lt;db&gt;.tmp</c> 的路径。后台刷盘（C16 两阶段：读锁内取快照、
        /// 锁外序列化）与显式 <see cref="Flush"/> / <see cref="Dispose"/> / <see cref="Compact"/>
        /// 写的是同一个 `.tmp`，没有这把门会让两者互相截断覆盖。
        /// 锁序恒为 <c>_flushGate</c> → <c>_rwLock</c>；已持有写锁的路径（AOL 压实）不得获取它。
        /// </summary>
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// FIX-01：每次已提交的突变自增。后台刷盘把快照与代际一起记录，轮转前若代际已变，
        /// 说明快照陈旧（有更新的写入或已显式落盘），必须丢弃，否则会覆盖已确认的写入并
        /// 误清 <see cref="_isDirty"/>。
        /// </summary>
        private long _dirtyGeneration;

        // 自动压实失败状态（决策 D2）：_needsCompaction 置位表示上次压实失败、待重试；
        // 触发它的那次写入已经持久化。
        private bool _needsCompaction;
        private uint _compactionFailures;
        private ulong _writesSinceCompactionAttempt;

        /// <summary>跨进程连接锁，自 C9 起由 <c>Open</c> 获取并持有整个连接生命周期。</summary>
        private LockFile? _connectionLock;

        // 写前磁盘预检状态（规范 §4.1.3）：与本实例最后一次成功落盘一致的 (maxTxID, fileSize)
        private ulong _lastKnownDiskMaxTxId;
        private long _lastKnownDiskFileSize;

        // 目录 fsync 不可用告警只发一次，避免每次轮转都打扰调用方（规范 §1.2 第 7 条：
        // 目录 fsync 为 best-effort，失败不得让轮转失败）。
        private bool _dirSyncUnavailableWarned;

        // 首次失败后不再尝试打开目录句柄：.NET 8 在 Unix 上对目录必然拒绝，
        // 每次写入都抛一次异常会给 250 ops/s 的默认路径再添开销。
#if NET8_0_OR_GREATER
        private bool _dirSyncUnavailable;
#endif

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

        public event Action<KeyChangeEvent>? KeyChanged
        {
            add => _root.KeyChangedInternal += value;
            remove => _root.KeyChangedInternal -= value;
        }
        private event Action<KeyChangeEvent>? KeyChangedInternal;

        /// <summary>
        /// 注册数据变更监听回调，返回订阅句柄（API v1.0 §2.2）。
        /// 仅显式 <see cref="WatcherHandle.Dispose"/> 才移除回调；单纯丢弃句柄不注销，
        /// 因此既有的 <c>db.Watch(cb);</c> 写法在 v1.0 中行为不变。
        /// </summary>
        public WatcherHandle Watch(Action<KeyChangeEvent> callback)
        {
            ThrowIfDisposed();
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return _root.WatchInternal(this, callback);
        }

        private WatcherHandle WatchInternal(CkvDb handle, Action<KeyChangeEvent> callback)
        {
            lock (_watcherLock)
            {
                _nextWatcherId++;
                long id = _nextWatcherId;
                _watchers.Add((id, callback));
                return new WatcherHandle(handle, id);
            }
        }

        /// <summary>按 id 移除监听器。幂等。</summary>
        internal void RemoveWatcher(long id) => _root.RemoveWatcherInternal(id);

        private void RemoveWatcherInternal(long id)
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
                KeyChangedInternal?.Invoke(ev);
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
            _root = this;
            _refCount = 1;
            _path = path;
            _canonicalPath = canonicalPath;
            _options = options.Clone();
            if (_options.SyncMode == SyncMode.Deferred && !_options.ReadOnly)
            {
                int intervalMs = (int)_options.SyncIntervalMs;
                if (intervalMs <= 0) intervalMs = 100;
                _syncTimer = new Timer(_ => FlushIfDirty(), null, intervalMs, intervalMs);
            }
        }

        private CkvDb(CkvDb root, bool isChild)
        {
            _root = root;
            _refCount = 0;
            _path = root._path;
            _canonicalPath = root._canonicalPath;
            _options = root._options;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0 || _root._isClosed)
            {
                throw new ObjectDisposedException(nameof(CkvDb), "Database handle is closed: operations are refused after Dispose()");
            }
        }

        private void CheckWritable()
        {
            if (_disposed != 0 || _root._isClosed)
            {
                throw new LockedError("Database is closed: writes are refused after Dispose()");
            }
            if (_root._options.ReadOnly)
            {
                throw new LockedError("Database is opened in read-only mode");
            }
        }

        /// <summary>
        /// 后台定时刷盘（C16）：采用短读锁提取快照、锁外落盘写 .tmp 与 fsync、短写锁原子替换，
        /// 避免长时间持有写锁阻塞并发读者。
        /// </summary>
        private void FlushIfDirty()
        {
            if (_isDisposed || _options.ReadOnly) return;

            // FIX-01：Snapshot 模式的后台刷盘会写 `.tmp`，必须与显式路径互斥。
            // AOL 只做 fsync，不碰 `.tmp`，因此不取门（也就不会与持写锁的压实路径形成循环等待）。
            bool snapshotMode = _options.EngineMode == StorageEngineMode.SnapshotAtomic;
            bool gateTaken = false;
            try
            {
                if (snapshotMode)
                {
                    if (!_flushGate.Wait(200)) return;
                    gateTaken = true;
                }

                byte[]? finalData = null;
                ulong newMaxTxId = 0;
                string path = _path;
                string tmpPath = path + ".tmp";
                string bakPath = path + ".bak";
                StorageEngineMode engineMode;
                long generation = 0;

                if (!_rwLock.TryEnterReadLock(200)) return;
                try
                {
                    if (!_isDirty || _isDisposed || _options.ReadOnly) return;
                    engineMode = _options.EngineMode;
                    if (engineMode == StorageEngineMode.SnapshotAtomic)
                    {
                        var diskMaxTxId = PrecheckDiskState();
                        finalData = SerializeSnapshotBytes(diskMaxTxId, out newMaxTxId);
                        generation = _dirtyGeneration;
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }

                if (engineMode == StorageEngineMode.AppendOnlyLog)
                {
                    if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        fs.Flush(true);
                    }
                    if (!_rwLock.TryEnterWriteLock(200)) return;
                    try
                    {
                        if (_options.EngineMode == StorageEngineMode.AppendOnlyLog)
                        {
                            _isDirty = false;
                        }
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }
                    return;
                }

                // Snapshot 模式：锁外执行 .tmp 写入与 fsync (C16)
                if (finalData == null) return;
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(finalData, 0, finalData.Length);
                    if (_options.SyncOnWrite)
                    {
                        fs.Flush(true);
                    }
                }

                // 短写锁完成原子轮转与元数据更新
                if (!_rwLock.TryEnterWriteLock(500))
                {
                    try { File.Delete(tmpPath); } catch { }
                    return;
                }
                try
                {
                    // FIX-01：快照陈旧时丢弃本次 `.tmp`，不轮转、不清脏标记、不动磁盘账本，
                    // 让下一 tick 或显式 Flush 用新状态重试。
                    if (_isDisposed || _options.ReadOnly || !_isDirty || _dirtyGeneration != generation)
                    {
                        try { File.Delete(tmpPath); } catch { }
                        return;
                    }
                    if (File.Exists(path))
                    {
                        AtomicReplaceWithRetry(path, bakPath);
                    }
                    AtomicReplaceWithRetry(tmpPath, path);

                    // 目录项落盘：best-effort，失败只告警不冒泡（规范 §1.2 第 7 条）
                    TryFsyncParentDirectory(path);

                    _currentMaxTxId = Math.Max(_currentMaxTxId, newMaxTxId);
                    var outFi = new FileInfo(path);
                    _lastKnownDiskFileSize = outFi.Length;
                    _lastKnownDiskMaxTxId = _currentMaxTxId;
                    _isDirty = false;
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
            catch
            {
                // Background flush retry on next interval
            }
            finally
            {
                if (gateTaken) _flushGate.Release();
            }
        }

        public void Close() => Dispose();


        /// <summary>
        /// Reentrant open 的选项冲突检查（§3.1 冻结）。
        ///
        /// <para><b>readOnly 冲突必须抛错，不得静默复用。</b>命中单例时若只发一条
        /// <c>onWarning</c> 就返回原实例，调用方以 <c>ReadOnly = true</c> 打开却拿到一个
        /// 读写实例——那是静默提权；而 <c>OnWarning</c> 默认为 <c>null</c>，默认配置下
        /// 连告警都看不见。反方向（要写拿到只读实例）同样会让后续写入以
        /// <see cref="LockedError"/> 的形式在远处失败、误导排查。两个方向都拒绝。
        /// </para>
        ///
        /// <para>engineMode / syncMode 冲突维持告警：它们只造成性能与落盘语义差异，
        /// 不构成权限问题。</para>
        /// </summary>
        private static void CheckOptionsConflict(DbOptions existingOpts, DbOptions newOpts)
        {
            if (existingOpts.ReadOnly != newOpts.ReadOnly)
            {
                throw new ConflictingOptionsError(
                    $"readOnly conflict on reentrant open: existing={existingOpts.ReadOnly}, requested={newOpts.ReadOnly}. " +
                    "The in-process singleton is returned unchanged by contract, so it cannot honour the requested " +
                    "permission. Close the existing handle first, or open a different canonical path.");
            }

            var conflicts = new List<string>();
            if (existingOpts.EngineMode != newOpts.EngineMode)
            {
                conflicts.Add($"engineMode (existing={existingOpts.EngineMode}, requested={newOpts.EngineMode})");
            }
            if (existingOpts.SyncMode != newOpts.SyncMode)
            {
                conflicts.Add($"syncMode (existing={existingOpts.SyncMode}, requested={newOpts.SyncMode})");
            }
            if (conflicts.Count > 0)
            {
                string msg = $"Options conflict on reentrant open: {string.Join(", ", conflicts)}. Using existing singleton options.";
                if (newOpts.OnWarning != null)
                {
                    newOpts.OnWarning("onOptionsConflict", msg);
                }
                else if (existingOpts.OnWarning != null)
                {
                    existingOpts.OnWarning("onOptionsConflict", msg);
                }
            }
        }

        /// <summary>
        /// Opens (or creates) the database and performs crash self-healing.
        ///
        /// <para><b>进程内单例与所有权保证（ADR-20261017）：</b>同路径多次调用 <c>Open</c>，
        /// 统一由注册表复用同一个底层持久化核心引擎，并返回独立的门面句柄，配置以首次打开为准。</para>
        ///
        /// <para><b>句柄独立生命周期：</b>每个返回的 <see cref="CkvDb"/> 都是独立的门面句柄，
        /// 调用方可放心使用标准的 <c>using var db = CkvDb.Open(...)</c>。每个句柄的 <c>Dispose()</c>
        /// 只会递减底层引用计数，并且保证<b>句柄级绝对幂等</b>（重复调用为无害 No-op），
        /// 绝不会误杀并发其它模块正在使用的句柄。</para>
        ///
        /// <para>只有当所有打开的句柄均已释放（底层引用计数归零）或由 GC 回收后，底层引擎
        /// 才会真正执行最终落盘并释放跨进程排他锁。</para>
        ///
        /// <para>options 冲突：<c>ReadOnly</c> 与既有实例不一致时抛
        /// <see cref="ConflictingOptionsError"/>（绝不能静默返回权限不同的实例）；
        /// <c>EngineMode</c> / <c>SyncMode</c> 不一致时通过 <c>OnWarning("onOptionsConflict")</c>
        /// 告警并沿用既有 options。</para>
        /// </summary>
        public static CkvDb Open(string path, DbOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            var opts = options ?? DbOptions.Default;
            string canonical = CanonicalizePath(path);

            // 上限只为防御"同类死循环复发"（P0-5）：每一轮都会新建实例 + 取连接锁 + 全量读盘
            // 校验，失控时是硬挂死 + 无限磁盘 IO。正常路径第 0 或第 1 轮即返回。
            const int maxAttempts = 8;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (Registry.TryGetValue(canonical, out var weakRef))
                {
                    if (weakRef.TryGetTarget(out var existing))
                    {
                        if (existing.TryAddRef())
                        {
                            try
                            {
                                CheckOptionsConflict(existing._options, opts);
                                return new CkvDb(existing, isChild: true);
                            }
                            catch
                            {
                                existing.ReleaseRef(fromFinalizer: false);
                                throw;
                            }
                        }
                        else
                        {
                            EvictStaleRegistryEntry(canonical, weakRef);
                        }
                    }
                    else
                    {
                        // FIX-P0-5：注册表里存在**死弱引用**（实例被 GC 回收但 Dispose 未执行，
                        // 例如 `CkvDb.Open(p);` 不赋值、DI 容器误配置、异常路径漏掉 using）。
                        // ConcurrentDictionary 的 TryAdd 在 key 已存在时**必然失败**，于是旧代码
                        // 每轮都重新 new + 取锁 + InitAndRecover，然后 continue —— 永不退出。
                        // 必须在任何 TryAdd 之前先按 (key, value) 原子驱逐死条目。
                        EvictStaleRegistryEntry(canonical, weakRef);
                    }
                }

                var db = new CkvDb(path, canonical, opts);
                // C9: 跨进程内核锁在任何磁盘动作之前获取，并持有整个连接生命周期。因此
                // InitAndRecover 的所有修复动作（.tmp 晋升/删除、残尾自愈、.bak 回退）都在锁内
                // 执行，不会再截掉另一个进程刚追加的帧（C10）。
                bool registered = false;
                try
                {
                    db._connectionLock = LockFile.Acquire(path, opts);
                    // FIX-P0-6：InitAndRecover 失败（最常见是 CorruptedError）时必须释放
                    // 已持有的跨进程连接锁。
                    db.InitAndRecover();

                    var newWeakRef = new WeakReference<CkvDb>(db);
                    if (Registry.TryAdd(canonical, newWeakRef))
                    {
                        registered = true;
                        db._registered = true;
                        db._selfWeak = newWeakRef;
                        return db;
                    }

                    // If racing with another insert, loop again
                    if (Registry.TryGetValue(canonical, out var racingRef) && racingRef.TryGetTarget(out var rDb))
                    {
                        if (rDb.TryAddRef())
                        {
                            try
                            {
                                CheckOptionsConflict(rDb._options, opts);
                                DisposeLoserInstance(db);
                                return new CkvDb(rDb, isChild: true);
                            }
                            catch
                            {
                                rDb.ReleaseRef(fromFinalizer: false);
                                throw;
                            }
                        }
                        // FIX-07：只有替换成功才认为自己是注册实例。以前无条件置
                        // _registered = true，TryUpdate 失败时会留下一个“已注册”的假象
                        // （Dispose 时还会误删别人的条目），同时把另一个实例当成单例返回。
                        if (Registry.TryUpdate(canonical, newWeakRef, racingRef))
                        {
                            registered = true;
                            db._registered = true;
                            db._selfWeak = newWeakRef;
                            return db;
                        }
                        DisposeLoserInstance(db);
                        continue;
                    }

                    // TryAdd 失败但条目已消失（并发删除）：同样不能泄漏本轮的锁。
                    DisposeLoserInstance(db);
                }
                catch
                {
                    // 任一失败路径（取锁、InitAndRecover、CheckOptionsConflict）都必须把本轮
                    // 已经取到的锁与定时器还回去，否则泄漏。
                    if (!registered)
                    {
                        DisposeLoserInstance(db);
                    }
                    throw;
                }
            }

            throw new InvalidOperationException(
                $"Open('{path}') did not converge after {maxAttempts} attempts; the path is being " +
                "opened concurrently by another thread. This should be unreachable.");
        }

        /// <summary>
        /// 释放一轮"输掉注册竞争"的临时实例：它已经取到了跨进程连接锁并跑完了
        /// InitAndRecover，但最终返回的是别的实例。旧代码在这条路径上直接丢弃它，
        /// 每命中一次就泄漏一个内核锁句柄（并让其它进程 Open 无谓地等满 LockTimeoutMs）。
        /// 该实例从未注册（<c>_registered == false</c>）且从未被写入（<c>_isDirty == false</c>），
        /// 因此 Dispose 只会停定时器并释放连接锁，不会误删注册表条目、也不会做多余的刷盘。
        /// </summary>
        private static void DisposeLoserInstance(CkvDb loser)
        {
            try { loser.Dispose(); } catch { /* 清理失败不得掩盖调用方的真实控制流 */ }
        }

        /// <summary>
        /// 便捷备份静态方法（ADR-20261017 / §7.4）。
        /// <para>优先复用当前进程注册表中的活跃实例导出快照（绝不将其 Dispose）；
        /// 若进程内无活跃实例，则独占打开后导出并安全释放。</para>
        /// </summary>
        public static void BackupFile(string sourceDbPath, string targetBackupFilePath, DbOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(sourceDbPath))
                throw new ArgumentException("Source database path cannot be null or empty", nameof(sourceDbPath));
            if (string.IsNullOrWhiteSpace(targetBackupFilePath))
                throw new ArgumentException("Target backup file path cannot be null or empty", nameof(targetBackupFilePath));

            string canonical = CanonicalizePath(sourceDbPath);
            if (Registry.TryGetValue(canonical, out var weakRef) && weakRef.TryGetTarget(out var activeRoot))
            {
                if (!activeRoot._isClosed)
                {
                    activeRoot.Backup(targetBackupFilePath);
                    return;
                }
            }

            using var db = Open(sourceDbPath, options);
            db.Backup(targetBackupFilePath);
        }

        /// <summary>
        /// 按 (key, value) 原子移除一个已失效的注册表条目（FIX-P0-5）。
        /// 只在 <paramref name="stale"/> 仍指向同一个值对象时移除，避免误删并发写入的新实例。
        /// </summary>
        private static void EvictStaleRegistryEntry(
            string canonical,
            WeakReference<CkvDb> stale)
        {
            try
            {
                // netstandard2.0 没有 ConcurrentDictionary.TryRemove(KeyValuePair)，
                // 用 ICollection.Remove 完成同样的按键+按值原子移除。
                ((ICollection<KeyValuePair<string, WeakReference<CkvDb>>>)Registry)
                    .Remove(new KeyValuePair<string, WeakReference<CkvDb>>(canonical, stale));
            }
            catch
            {
                // 移除失败（并发已替换）无害：外层循环会重新读到最新的条目。
            }
        }

        /// <summary>
        /// Opens the database.
        ///
        /// The caller (Open) has already acquired the connection lock and holds it for the
        /// whole lifetime of the connection, so every repair performed here -- promoting or
        /// deleting <c>.tmp</c>, healing a torn log tail, rolling a <c>.bak</c> forward --
        /// writes to the database files while no other process can append to them. That is
        /// what used to be C10: the tail healing ran without the lock and could truncate
        /// frames a concurrent appender had just written.
        /// </summary>
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
                    // 主库不存在，此时 `.tmp` 是唯一完好的数据。必须区分两类失败：
                    //  1. `.tmp` 内容确实不可解析 → 删除它，继续走分支③（与旧行为一致）；
                    //  2. `.tmp` 有效但晋升（File.Move）失败 —— 可能是杀软/索引器/预览窗格
                    //     的瞬时占用 —— 此时**必须保留 `.tmp` 并让 Open 失败**。旧实现的
                    //     catch-all 会把已经校验通过的 `.tmp` 一并删掉，即销毁唯一的好数据
                    //     （C1-C3 之后的第 4 处静默数据丢失）。
                    Dictionary<string, NamespaceStore>? tmpNs = null;
                    ulong tmpTxId = 0;
                    byte[]? tmpBytes = null;
                    try
                    {
                        tmpBytes = File.ReadAllBytes(tmpPath);
                    }
                    catch
                    {
                        tmpBytes = null;
                    }

                    if (tmpBytes != null)
                    {
                        try
                        {
                            (tmpNs, tmpTxId) = ParseDatabaseBytes(tmpBytes);
                        }
                        catch
                        {
                            tmpNs = null;
                        }
                    }

                    if (tmpNs == null)
                    {
                        // 内容确实不可解析（或读不出来）：删除 .tmp 是安全的。
                        try { File.Delete(tmpPath); } catch { }
                    }
                    else
                    {
                        try
                        {
                            // Valid .tmp! Promote it
                            // 注意用 File.Move 而非 AtomicReplaceWithRetry：晋升场景的目标
                            // `.db` 并不存在，netstandard2.0 路径上的 File.Replace 要求目标
                            // 已存在（P1-16），会把一次合法的晋升变成失败。
                            File.Move(tmpPath, dbPath);
                            ApplyParsed(tmpNs, tmpTxId, new FileInfo(dbPath).Length);
                            dbExists = true;
                        }
                        catch (Exception promoteErr)
                        {
                            // 晋升失败：保留 .tmp 原样，错误信息中给出它的位置。
                            throw new CorruptedError(
                                $"Primary file '{dbPath}' is missing and a valid '.tmp' could not be promoted to it " +
                                $"(rename failed: {promoteErr.Message}); the only intact copy is still at '{tmpPath}': " +
                                "close whatever holds the file handle and reopen");
                        }
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
                bool allowTailTrunc = _options.EngineMode == StorageEngineMode.AppendOnlyLog && !_options.ReadOnly;
                try
                {
                    var (ns, txId, effLen) = ParseDatabaseBytes(bytes, allowTailTrunc, _options.OnWarning);
                    if (allowTailTrunc && effLen < bytes.Length)
                    {
                        using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            fs.SetLength(effLen);
                            fs.Flush(true);
                        }
                    }
                    ApplyParsed(ns, txId, effLen);
                }
                catch (CorruptedError primaryError)
                {
                    // 决策 D1：`.bak` 是上一次轮转**之前**的一代，用它替换主库会静默
                    // 回滚此后写入的全部数据。默认不再自动回退，仅在调用方显式开启时
                    // 才使用；否则错误信息中给出 `.bak` 位置，由运维离线恢复。
                    bool bakAvailable = File.Exists(bakPath);

                    if (_options.AutoRecoverFromBackup && !_options.ReadOnly && bakAvailable)
                    {
                        try
                        {
                            // `.bak` 是恢复源：原样加载，绝不截断（G1）。
                            // 两参重载以 allowTailTruncation = false 解析。
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
                                $"{primaryError.Message}; the previous generation kept at '{bakPath}' could not be loaded either"
                            );
                        }
                    }

                    if (bakAvailable)
                    {
                        throw new CorruptedError(
                            $"{primaryError.Message}; a previous generation is kept at '{bakPath}': " +
                            "copy it over the primary file to recover, or open with AutoRecoverFromBackup = true"
                        );
                    }

                    throw;
                }
            }
            else if (File.Exists(bakPath))
            {
                // Branch ③: primary does not exist but a previous generation does.
                //
                // This state cannot be produced by an interrupted rotation -- that
                // always leaves a complete `.tmp` behind, which the step above
                // promotes -- so it means the primary file disappeared (deleted or
                // replaced externally). Promoting the `.bak` on top of it is a
                // silent downgrade to an older generation, and when the `.bak`
                // itself is unreadable the open used to succeed as an *empty*
                // database: the fourth silent data loss after C1-C3 (D1-7/D1-8).
                // Both cases are refused unless the caller explicitly opted into
                // `.bak` recovery.
                Dictionary<string, NamespaceStore> bakNs;
                ulong bakTxId;
                long bakLen;
                try
                {
                    byte[] bakBytes = File.ReadAllBytes(bakPath);
                    // `.bak` is a recovery source: never tail-truncate it (G1).
                    (bakNs, bakTxId) = ParseDatabaseBytes(bakBytes);
                    bakLen = bakBytes.Length;
                }
                catch (CorruptedError bakError)
                {
                    throw new CorruptedError(
                        $"Primary file '{dbPath}' is missing and the previous generation kept at " +
                        $"'{bakPath}' could not be loaded either: {bakError.Message}"
                    );
                }

                if (!_options.AutoRecoverFromBackup || _options.ReadOnly)
                {
                    throw new CorruptedError(
                        $"Primary file '{dbPath}' is missing while a previous generation is kept at " +
                        $"'{bakPath}': copy it over the primary file to recover, or open with " +
                        "AutoRecoverFromBackup = true"
                    );
                }

                File.Copy(bakPath, dbPath, true);
                ApplyParsed(bakNs, bakTxId, bakLen);
                _options.OnWarning?.Invoke("onRecoveredFromBackup",
                    $"Recovered database '{_path}' from backup '{bakPath}'");
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
        /// <summary>
        /// 判断自 <paramref name="from"/> 起至文件末尾是否全为零字节。
        ///
        /// 崩溃后被文件系统留成零填充的尾部（从未写入的区域）属于残尾；若按介质损坏处理，
        /// 会把一次普通崩溃变成"拒绝打开"。RocksDB 的默认 WAL 恢复模式同样显式容忍尾部零填充。
        /// </summary>
        private static bool TailRegionIsAllZero(byte[] bytes, int from)
        {
            for (int i = from; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) return false;
            }
            return true;
        }

        private static (Dictionary<string, NamespaceStore> Namespaces, ulong MaxTxId) ParseDatabaseBytes(byte[] bytes)
        {
            var (ns, tx, _) = ParseDatabaseBytes(bytes, false, null);
            return (ns, tx);
        }

        private static (Dictionary<string, NamespaceStore> Namespaces, ulong MaxTxId, int EffectiveLength) ParseDatabaseBytes(
            byte[] bytes,
            bool allowTailTruncation,
            WarningCallback? onWarning)
        {
            ValidateDatabaseBytes(bytes);

            var parsed = new List<LoadEntry>(256);
            ulong maxTxId = 0;
            ulong lastTxId = 0;

            int offset = Constants.FileHeaderLen;
            while (offset < bytes.Length)
            {
                if (offset + Constants.FrameHeaderLen > bytes.Length)
                {
                    if (allowTailTruncation)
                    {
                        onWarning?.Invoke("onTailTruncated", "Truncated partial frame header at tail");
                        break;
                    }
                    throw new CorruptedError($"Incomplete frame header at offset {offset}");
                }

                var frameSpan = bytes.AsSpan(offset, Constants.FrameHeaderLen);
                uint frameMagic = BinaryHelper.ReadUInt32LE(frameSpan.Slice(0, 4));
                uint storedHdrCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(4, 4));
                uint calcHdrCrc = Crc32.Compute(bytes, offset + 8, 20);
                ulong txId = BinaryHelper.ReadUInt64LE(frameSpan.Slice(8, 8));
                uint opCount = BinaryHelper.ReadUInt32LE(frameSpan.Slice(16, 4));
                uint payloadLen = BinaryHelper.ReadUInt32LE(frameSpan.Slice(20, 4));
                uint storedPayloadCrc = BinaryHelper.ReadUInt32LE(frameSpan.Slice(24, 4));

                if (frameMagic != Constants.FrameMagic
                    || storedHdrCrc != calcHdrCrc
                    || txId <= lastTxId
                    || opCount == 0
                    || opCount > Constants.MaxOpsPerFrame
                    || payloadLen > Constants.MaxPayloadLen)
                {
                    // 帧头完整存在于文件中，说明这些字节确实已被写入：这不可能是断电残帧，
                    // 只能是介质损坏或从未写入的零填充尾部。
                    if (allowTailTruncation && TailRegionIsAllZero(bytes, offset))
                    {
                        onWarning?.Invoke("onTailTruncated", "Truncated zero-filled tail at tail");
                        break;
                    }
                    // 一律按损坏抛出：损坏点之后可能仍有完整帧，静默截断会连带丢弃它们。
                    throw new CorruptedError(
                        $"Corrupted frame header at offset {offset}: magic 0x{frameMagic:X8}, " +
                        $"tx_id {txId}, op_count {opCount}, payload_len {payloadLen}");
                }

                int payloadOffset = offset + Constants.FrameHeaderLen;
                if ((long)payloadOffset + payloadLen > bytes.Length)
                {
                    if (allowTailTruncation)
                    {
                        onWarning?.Invoke("onTailTruncated", "Truncated incomplete payload at tail");
                        break;
                    }
                    throw new CorruptedError($"Incomplete frame payload at offset {payloadOffset}");
                }

                uint calcPayloadCrc = Crc32.Compute(bytes, payloadOffset, (int)payloadLen);
                if (storedPayloadCrc != calcPayloadCrc)
                {
                    // 整帧完整存在于文件中（帧头已校验、载荷长度也在文件范围内），
                    // 但内容与记录的 CRC 不符：这些字节已被写入且是错的（介质损坏）。
                    throw new CorruptedError($"Frame payload CRC mismatch at offset {payloadOffset}");
                }

                // Parse entries
                int pIdx = payloadOffset;
                int pEnd = payloadOffset + (int)payloadLen;
                bool decodeErr = false;
                var frameEntries = new List<LoadEntry>((int)opCount);

                for (uint op = 0; op < opCount; op++)
                {
                    if (pIdx + Constants.EntryFixedHeaderLen > pEnd)
                    {
                        decodeErr = true;
                        break;
                    }

                    byte opType = bytes[pIdx];
                    byte valType = bytes[pIdx + 1];
                    ushort nsLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 2, 2));
                    ushort keyLen = BinaryHelper.ReadUInt16LE(bytes.AsSpan(pIdx + 4, 2));
                    uint valLen = BinaryHelper.ReadUInt32LE(bytes.AsSpan(pIdx + 6, 4));
                    pIdx += Constants.EntryFixedHeaderLen;

                    // 规范 §3.2.2 硬上限：0 <= NsLen <= 255。nsLen 是直接从磁盘读出的 ushort，
                    // 可达 65535。不校验就会把超长命名空间（外来生产者写入，或本库某个未校验的
                    // 历史版本写入）加载进内存，出现在 Namespaces() 里，却被**每一个** API 入口
                    // （含 DropNamespace / ClearNamespace）以 InvalidNamespaceError 拒绝——
                    // 既读不出也删不掉，且每次快照都会把它重新序列化进 .db / .bak / 全部
                    // Backup() 产物，污染其余四门语言。Kotlin 已在加载期拒绝（Format.kt），
                    // 其余语言对齐：坏文件在 Open 阶段就明确失败，而不是加载成一个不可达的孤儿分区。
                    if (nsLen > Constants.MaxNsLen)
                    {
                        throw new CorruptedError($"Namespace length {nsLen} exceeds max {Constants.MaxNsLen}");
                    }

                    if (pIdx + nsLen + keyLen + valLen > pEnd)
                    {
                        decodeErr = true;
                        break;
                    }

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
                            if (valType > Constants.ValTypeBool
                                || (valType == Constants.ValTypeInt64 && valLen != 8)
                                || (valType == Constants.ValTypeFloat64 && valLen != 8)
                                || (valType == Constants.ValTypeBool && valLen != 1))
                            {
                                decodeErr = true;
                            }
                            break;
                        case Constants.OpDelete:
                        case Constants.OpDropNamespace:
                            break;
                        default:
                            decodeErr = true;
                            break;
                    }
                    if (decodeErr) break;

                    frameEntries.Add(new LoadEntry(parsed.Count + frameEntries.Count, opType, valType, ns, keyBytes, valBytes));
                }

                if (decodeErr)
                {
                    // 载荷 CRC 已通过，说明载荷就是当初写入的内容：无法解码的条目属于
                    // 不变量被破坏（或人为构造的文件），绝不是断电残帧。
                    throw new CorruptedError($"Corrupted entry in payload at offset {offset}");
                }

                parsed.AddRange(frameEntries);
                lastTxId = txId;
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
                    // 批量装载直接写 Items，账本需显式重建
                    store.RecalculateLiveBytes();
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

            return (namespaces, maxTxId, offset);
        }

        /// <summary>
        /// 轻量探测磁盘文件的最大帧 TxID（仅顺序读取帧头，不解码 payload）。
        /// 返回 Exists=false 表示文件不存在；探测失败按 (0, size) 处理，
        /// 与 lastKnown 比对将不匹配，从而拒绝写入（无法验证时不落盘）。
        /// </summary>
        private static (ulong MaxTxId, long FileSize, bool Exists) ProbeDiskState(string path, ulong lastKnownTx = 0, long lastKnownSize = 0)
        {
            // RC2：单个 FileInfo 取代 File.Exists + new FileInfo 的两次路径查询。
            // FileInfo.Exists 已完成一次 stat 并缓存，后续 Length 不再触发系统调用；
            // 探测失败时 Exists 同样返回 false，与原先 File.Exists 的语义一致。
            var fi = new FileInfo(path);
            if (!fi.Exists) return (0, 0, false);
            long size = fi.Length;
            if (size <= Constants.FileHeaderLen) return (0, size, true);
            if ((lastKnownTx != 0 || lastKnownSize != 0) && size == lastKnownSize)
            {
                return (lastKnownTx, size, true);
            }

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
        /// 写路径第一阶段：在任何内存变更**之前**执行只读校验与规范 §4.1.3 的磁盘预检。
        /// 抛出异常时尚未触碰任何内存状态，因此调用方看到的错误与内存状态永远不会分叉。
        /// </summary>
        /// <returns>磁盘上当前的最大 TxID（交给 <see cref="CommitFlush"/> 使用）。</returns>
        /// <remarks>
        /// 自 C9 起这里不再取锁：连接锁由 <c>Open</c> 持有整个生命周期，写路径不会再与自身
        /// 冲突（那正是 C2），预检失败也不会留下"内存已改、磁盘未落"的状态。
        /// </remarks>
        private ulong PrecheckDiskState()
        {
            if (_options.ReadOnly)
                // §3.3（API v1.0 冻结）：与 Rust DbError::Locked / Go LockedError 一致。
                // 旧实现抛 InvalidOperationException，与规范 §5.1.2 错误体系不符。
                throw new LockedError("Database is opened in read-only mode");

            var (diskTx, diskSize, diskExists) = ProbeDiskState(_path, _lastKnownDiskMaxTxId, _lastKnownDiskFileSize);
            if (diskExists)
            {
                // B1 修复：空库（仅 32B 文件头）磁盘无任何帧，探测 maxTxID 语义为 0，
                // 与内存 _currentMaxTxId（条目清空后严禁归零）不可比；
                // 空文件场景只比对文件大小，避免清空后再写入误报并发修改。
                bool stateMatches = diskSize == _lastKnownDiskFileSize &&
                    (diskSize == Constants.FileHeaderLen || diskTx == _lastKnownDiskMaxTxId);
                if ((_lastKnownDiskMaxTxId != 0 || _lastKnownDiskFileSize != 0) && !stateMatches)
                {
                    throw new ConcurrentModificationError(
                        $"Disk state changed externally: expected ({_lastKnownDiskMaxTxId}, {_lastKnownDiskFileSize}), found ({diskTx}, {diskSize})");
                }
            }

            return diskTx;
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
        /// 写路径第二阶段：落盘提交（.db -> .bak，.tmp -> .db）。
        /// 跨进程锁已由连接持有（C9），不再在此获取或释放。
        /// </summary>
        private void CommitFlush(ulong diskMaxTxId)
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

            // 目录项落盘：best-effort，失败只告警不冒泡（规范 §1.2 第 7 条）
            TryFsyncParentDirectory(_path);

            // 轮换完成后刷新预检基线，与 Go/Rust 的 lastKnownDiskState 语义一致
            var outFi = new FileInfo(_path);
            _lastKnownDiskFileSize = outFi.Length;
            _lastKnownDiskMaxTxId = _currentMaxTxId;
            _isDirty = false;
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
        /// 尽力而为地 fsync 父目录，使 <c>rename</c> 产生的目录项在掉电后仍然存在（规范 §1.2 第 7 条）。
        /// <para>
        /// 实测结论（.NET 8.0.426 / Linux ext4，2026-10-17）：<c>File.OpenHandle</c> 以目录路径打开
        /// 会抛 <see cref="UnauthorizedAccessException"/>，因此 <c>RandomAccess.FlushToDisk(SafeFileHandle)</c>
        /// 这条全托管路径在 Unix 上**当前不可用**。这里保留尝试（未来运行时若放开目录句柄即可自动生效），
        /// 失败后置位 <c>_dirSyncUnavailable</c> 不再重试，并通过 <c>onWarning("onDirSyncUnavailable")</c>
        /// 上报一次。
        /// </para>
        /// <para>
        /// Windows 直接返回（NTFS 元数据由操作系统保证）；netstandard2.0 / Unity 无托管 API，为 no-op。
        /// 绝不允许失败冒泡：轮转已经完成，抛错会让调用方误以为写入失败（同决策 D2）。
        /// 也绝不引入 P/Invoke —— 宁可降级并上报，不可破坏"零 Native 依赖"红线。
        /// </para>
        /// </summary>
        private void TryFsyncParentDirectory(string filePath)
        {
#if NET8_0_OR_GREATER
            if (_dirSyncUnavailable || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            try
            {
                using (SafeFileHandle handle = File.OpenHandle(
                    dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    RandomAccess.FlushToDisk(handle);
                }
            }
            catch (Exception failure) when (
                failure is IOException ||
                failure is UnauthorizedAccessException ||
                failure is NotSupportedException ||
                failure is ArgumentException ||
                failure is System.Security.SecurityException)
            {
                _dirSyncUnavailable = true;
                WarnDirSyncUnavailable(failure);
            }
#else
            // netstandard2.0 / Unity 目标：没有任何全托管目录 fsync 手段，明确降级为 no-op。
            _ = filePath;
#endif
        }

        /// <summary>目录 fsync 不可用时只告警一次。</summary>
        private void WarnDirSyncUnavailable(Exception failure)
        {
            if (_dirSyncUnavailableWarned)
            {
                return;
            }
            _dirSyncUnavailableWarned = true;
            _options.OnWarning?.Invoke("onDirSyncUnavailable",
                "Directory fsync is unavailable on this platform/runtime; " +
                "a power loss immediately after rotation may roll back the last rename: " +
                failure.Message);
        }

        /// <summary>
        /// 带 Windows 退避重试的覆盖式原子替换（规范 §4.2：5/15/30/60/120ms 共 5 次）。
        ///
        /// 重试耗尽后退化为“拷贝到 <c>.stage</c> → 校验长度 → 一次 rename 覆盖”（FIX-03）。
        /// 原来直接用 <c>File.Copy(src, dst, true)</c>：它先截断目标再拷贝，这个窗口里崩溃会
        /// 留下一个半成品的 `.db`，而此刻 `.bak` 往往是更老的一代（首次轮转甚至没有 `.bak`）。
        /// 走到 rename 之前目标文件始终未被触碰；rename 失败时 `.stage` 会保留下来供人工恢复。
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

            string stage = dst + ".stage";
            // 任一步失败都直接抛出并保留 .stage（可能是唯一的完整一代），不做清理。
            File.Copy(src, stage, true);
            long srcLen = new FileInfo(src).Length;
            long stageLen = new FileInfo(stage).Length;
            if (srcLen != stageLen)
            {
                throw new IOException(
                    $"Fallback copy produced an incomplete stage file ({stageLen} of {srcLen} bytes): {stage}");
            }
            AtomicReplace(stage, dst);

            try { File.Delete(src); } catch { }
        }

        /// <summary>
        /// 按确定性排序规则重写为紧凑快照文件，TxID = currentMaxTxID + 1（规范 §5.2.2）。
        /// </summary>
        public void Compact()
        {
            CheckWritable();
            _root.CompactInternal();
        }

        private void CompactInternal()
        {
            // FIX-01：与后台刷盘共用 `.tmp`。
            _flushGate.Wait();
            try
            {
                _rwLock.EnterWriteLock();
                try
                {
                    if (_options.ReadOnly)
                        throw new LockedError("Database is opened in read-only mode");

                    var compactDiskMaxTxId = PrecheckDiskState();
                    CommitFlush(compactDiskMaxTxId);
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }

        /// <summary>
        /// 显式把内存状态落盘（API v1.0 §2.3）。
        /// 在 Immediate 模式下做全量紧凑压实；在 Deferred/Manual 模式下落盘未提交的脏数据。
        /// </summary>
        public void Flush()
        {
            CheckWritable();
            _root.FlushInternal();
        }

        private void FlushInternal()
        {
            // FIX-01：与后台刷盘共用 `.tmp`，必须先过门（锁序：_flushGate → _rwLock）。
            _flushGate.Wait();
            try
            {
                _rwLock.EnterWriteLock();
                try
                {
                    if (_options.ReadOnly)
                        throw new LockedError("Database is opened in read-only mode");
                    if (!_isDirty && File.Exists(_path))
                        return;

                    if (_options.EngineMode == StorageEngineMode.AppendOnlyLog)
                    {
                        if (File.Exists(_path))
                        {
                            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                            fs.Flush(true);
                        }
                        _isDirty = false;
                        return;
                    }

                    var diskMaxTxId = PrecheckDiskState();
                    CommitFlush(diskMaxTxId);
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }

        /// <summary>
        /// 失败后需要跳过的写次数：首次失败等 64 次，随后每次连续失败翻倍，上限 4096。
        /// </summary>
        private static ulong CompactionBackoff(uint priorFailures)
        {
            const ulong Base = 64;
            const ulong Cap = 4096;

            var shift = priorFailures > 6 ? 6 : priorFailures;
            var window = Base << (int)shift;
            return window > Cap ? Cap : window;
        }

        /// <summary>
        /// 对刚刚落内存的变更应用自动压实策略：越过阈值时压实，压实先前失败过则在退避
        /// 窗口届满后重试一次。
        /// </summary>
        private void MaybeCompactAfterWrite(long newFileSize)
        {
            if (_options.CompactionThresholdBytes == 0) return;

            if (_needsCompaction)
            {
                _writesSinceCompactionAttempt++;
                var priorFailures = _compactionFailures > 0 ? _compactionFailures - 1 : 0;
                if (_writesSinceCompactionAttempt < CompactionBackoff(priorFailures)) return;
                AttemptCompaction();
                return;
            }

            // FIX-21: 按“垃圾量”触发，而不是按文件绝对大小触发。
            //
            // 压实后文件恰好等于存活大小，因此旧的 `newFileSize >= threshold` 条件在存活数据
            // 达到阈值后对**每一次写**都成立：6MB 存活时实测 26ms/写（38 ops/s），而正常
            // 摊销路径约 5000 ops/s。garbage 由 O(1) 账本派生，无需新增状态，重开库后依然
            // 正确（若用进程内“累计追加字节”计数器，重开会清零）。
            //
            // effectiveThreshold 随存活规模放大，使单次压实的写放大被限制在 ~5x，而不是随
            // 库大小线性增长。
            var live = Math.Min(LivePayloadBytes(), newFileSize);
            var garbage = newFileSize - live;
            var effectiveThreshold = Math.Max(
                _options.CompactionThresholdBytes,
                (ulong)(live / 4));
            if ((ulong)garbage >= effectiveThreshold)
            {
                AttemptCompaction();
                return;
            }

            // 自适应触发：被墓碑与覆写值占据的日志永远长不到阈值，改按死键比例回收。
            if (ShouldCompactByDeadRatio(newFileSize))
            {
                AttemptCompaction();
            }
        }

        /// <summary>所有命名空间的存活载荷合计（key + value 字节数）。O(#命名空间)，不遍历条目。</summary>
        private long LivePayloadBytes()
        {
            long total = 0;
            foreach (var store in _namespaces.Values)
            {
                total += store.LiveBytes;
            }
            return total;
        }

        /// <summary>
        /// 未达大小阈值的日志是否已被死数据主导。`LiveBytes` 只计载荷，恒小于文件大小
        /// （文件头与帧头开销是差额）；夹紧后即便账本偶有偏差也不会算出荒谬比例。
        /// </summary>
        private bool ShouldCompactByDeadRatio(long fileSize)
        {
            if (fileSize < AdaptiveMinFileSize) return false;
            long live = LivePayloadBytes();
            if (live > fileSize) live = fileSize;
            return (fileSize - live) * 100 >= fileSize * AdaptiveDeadRatioPercent;
        }

        /// <summary>死键比例触发的最小文件大小：更小的日志整体重写还不如留着。</summary>
        private const long AdaptiveMinFileSize = 2L * 1024 * 1024;

        /// <summary>死数据占比达到该百分比时，未达大小阈值的日志也会被压实。</summary>
        private const long AdaptiveDeadRatioPercent = 50L;

        /// <summary>
        /// 执行一次自动压实并记录结果。
        ///
        /// 失败不会让调用方的写入失败（除非开启 StrictCompaction）：写入本身已经持久化，
        /// 报错会诱导调用方重试一个已成功的写，而 <c>Increment</c> 是 read-modify-write，
        /// 会重复累加 delta（决策 D2）。
        /// </summary>
        private void AttemptCompaction()
        {
            _writesSinceCompactionAttempt = 0;

            try
            {
                var diskMaxTxId = PrecheckDiskState();
                CommitFlush(diskMaxTxId);
            }
            catch (Exception failure)
            {
                _compactionFailures++;
                _needsCompaction = true;

                // 告警在两种模式下都会发出；StrictCompaction 只决定错误是否继续上抛。
                _options.OnWarning?.Invoke("onCompactionFailed",
                    $"Compaction failed (size {_lastKnownDiskFileSize} bytes, " +
                    $"threshold {_options.CompactionThresholdBytes} bytes, " +
                    $"consecutive failures {_compactionFailures}): {failure.Message}");

                if (_options.StrictCompaction) throw;
                return;
            }

            _needsCompaction = false;
            _compactionFailures = 0;
        }

        /// <summary>
        /// Dispose 时的兜底重试（best-effort）：不改变清理语义，失败已通过 OnWarning 报出。
        /// </summary>
        private void RetryPendingCompaction()
        {
            if (!_needsCompaction || _options.ReadOnly ||
                _options.EngineMode != StorageEngineMode.AppendOnlyLog) return;

            try
            {
                AttemptCompaction();
            }
            catch
            {
                // 已通过 onCompactionFailed 报出；清理路径不因此失败。
            }
        }

        /// <summary>
        /// 追加本次变更的帧，并返回追加后的文件大小。
        ///
        /// 此处**刻意不触发压实**：压实写出的快照取自当前内存状态，必须在本次变更
        /// 落到内存之后才执行，否则刚追加的帧会被不含它的快照覆盖（C1）。
        /// </summary>
        private long AppendEntries(IReadOnlyList<WalOpEntry> entries)
        {
            if (entries == null || entries.Count == 0) return 0;

            var diskMaxTxId = PrecheckDiskState();
            {
                // RC2：热路径用 FileMode.Open（OpenOrCreate 每次打开都要多做一次
                // "不存在则创建"的判定，仅在 Open 报 NotFound 时回退）。
                // 文件是否为空由 Seek(End) 的返回值判定，原来那次 File.Exists
                // 与 fs.Length 都是可省的元数据查询。
                using var fs = OpenLogFile();
                if (fs.Seek(0, SeekOrigin.End) == 0)
                {
                    byte[] fileHeader = new byte[32];
                    Array.Copy(Constants.FileMagic, 0, fileHeader, 0, 8);
                    BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(8, 2), Constants.FormatReadVer);
                    BinaryHelper.WriteUInt16LE(fileHeader.AsSpan(10, 2), Constants.FormatWriteVer);
                    BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(12, 4), 0);
                    uint headerCrc = Crc32.Compute(fileHeader, 0, 28);
                    BinaryHelper.WriteUInt32LE(fileHeader.AsSpan(28, 4), headerCrc);
                    fs.Write(fileHeader, 0, 32);
                }

                ulong currentTxId = Math.Max(_currentMaxTxId, diskMaxTxId) + 1;
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
                    BinaryHelper.WriteUInt64LE(tail20.AsSpan(0, 8), currentTxId++);
                    BinaryHelper.WriteUInt32LE(tail20.AsSpan(8, 4), currentOpCount);
                    BinaryHelper.WriteUInt32LE(tail20.AsSpan(12, 4), payloadLen);
                    BinaryHelper.WriteUInt32LE(tail20.AsSpan(16, 4), pCrc);

                    uint fHdrCrc = Crc32.Compute(tail20);
                    BinaryHelper.WriteUInt32LE(frameHeader.AsSpan(4, 4), fHdrCrc);
                    Array.Copy(tail20, 0, frameHeader, 8, 20);

                    fs.Write(frameHeader, 0, 28);
                    fs.Write(payloadBytes, 0, payloadBytes.Length);

                    currentPayload.SetLength(0);
                    currentOpCount = 0;
                }

                foreach (var entry in entries)
                {
                    int entryLen = Constants.EntryFixedHeaderLen + entry.Namespace.Length + entry.Key.Length + entry.Value.Length;
                    if (currentPayload.Length + entryLen > Constants.MaxPayloadLen || currentOpCount >= Constants.MaxOpsPerFrame)
                    {
                        flushFrame();
                    }

                    byte[] entryHdr = new byte[10];
                    entryHdr[0] = entry.OpCode;
                    entryHdr[1] = entry.ValueType;
                    BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(2, 2), (ushort)entry.Namespace.Length);
                    BinaryHelper.WriteUInt16LE(entryHdr.AsSpan(4, 2), (ushort)entry.Key.Length);
                    BinaryHelper.WriteUInt32LE(entryHdr.AsSpan(6, 4), (uint)entry.Value.Length);

                    currentPayload.Write(entryHdr, 0, 10);
                    if (entry.Namespace.Length > 0) currentPayload.Write(entry.Namespace, 0, entry.Namespace.Length);
                    currentPayload.Write(entry.Key, 0, entry.Key.Length);
                    currentPayload.Write(entry.Value, 0, entry.Value.Length);
                    currentOpCount++;
                }

                if (currentOpCount > 0)
                {
                    flushFrame();
                }

                ulong finalMaxTxId = currentTxId - 1;

                if (_options.SyncMode == SyncMode.Immediate)
                {
                    fs.Flush(true);
                    _isDirty = false;
                }
                else
                {
                    fs.Flush(false);
                    _isDirty = true;
                }

                // RC2：顺序追加结束后的 Position 就是新文件大小（FileStream 内部计数，
                // 无系统调用），原来那次 fs.Length 同样是可省的句柄元数据查询。
                long newFileSize = fs.Position;
                _currentMaxTxId = finalMaxTxId;
                _lastKnownDiskMaxTxId = finalMaxTxId;
                _lastKnownDiskFileSize = newFileSize;

                return newFileSize;
            }
        }

        /// <summary>
        /// RC2：以"先 Open、NotFound 再回落 OpenOrCreate"的方式打开追加日志。
        /// <see cref="FileMode.OpenOrCreate"/> 在每次打开时都要走一遍"不存在则创建"的
        /// 判定，而日志文件除首次写之外必然已存在，因此把创建路径挪到冷分支。
        /// </summary>
        private FileStream OpenLogFile()
        {
            try
            {
                return new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (FileNotFoundException)
            {
                return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }

        private void ApplyMutation(IReadOnlyList<WalOpEntry> entries, Action applyMem)
        {
            CheckWritable();

            if (_options.EngineMode == StorageEngineMode.AppendOnlyLog)
            {
                // 顺序很重要（C1/C2）：
                //   1. 先追加帧（持久化优先），
                //   2. 再把本次变更落到内存状态，
                //   3. 此时才考虑压实——压实快照由内存状态序列化，必然已包含本次变更。
                // AppendEntries 返回前已释放 `.db` 锁，因此下面的压实会在**独立的锁周期**
                // 内取锁，不会与调用方持有的锁冲突（C2）。
                long newFileSize = AppendEntries(entries);
                applyMem();
                MaybeCompactAfterWrite(newFileSize);
            }
            else if (_options.SyncMode == SyncMode.Immediate)
            {
                var diskMaxTxId = PrecheckDiskState();
                applyMem();
                CommitFlush(diskMaxTxId);
            }
            else
            {
                applyMem();
                _currentMaxTxId++;
                _isDirty = true;
            }

            // FIX-01：后台刷盘以此判断自己的快照是否已过时。
            _dirtyGeneration++;
        }

        /// <summary>
        /// 按 UTF-8 无符号字节序返回所有命名空间（API v1.0 §2.1）。
        /// 默认命名空间为 ""；clear_namespace 之后的空命名空间仍然列出。
        /// 非 UTF-8 的命名空间名（只能由外部文件引入）跳过。
        /// </summary>
        public List<string> Namespaces()
        {
            ThrowIfDisposed();
            return _root.NamespacesInternal();
        }

        private List<string> NamespacesInternal()
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
            ThrowIfDisposed();
            return _root.FileSizeInternal();
        }

        private long FileSizeInternal()
        {
            var fi = new FileInfo(_path);
            return fi.Exists ? fi.Length : 0;
        }

        /// <summary>
        /// 纯内存聚合统计，不触发磁盘 I/O、不修改状态（API v1.0 §2.4）。
        /// </summary>
        public DbStats Stats()
        {
            ThrowIfDisposed();
            return _root.StatsInternal();
        }

        private DbStats StatsInternal()
        {
            _rwLock.EnterReadLock();
            try
            {
                int total = 0;
                foreach (var store in _namespaces.Values)
                {
                    total += store.Items.Count;
                }
                return new DbStats(FileSizeInternal(), _namespaces.Count, total, _currentMaxTxId);
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
            ThrowIfDisposed();
            _root.BackupInternal(dest);
        }

        private void BackupInternal(string dest)
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
            ThrowIfDisposed();
            return _root.IterPrefixInternal(ns, prefix);
        }

        private List<Entry> IterPrefixInternal(string? ns, string? prefix = null)
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

        // ================= API Implementation =================

        public byte[]? GetBytes(string? ns, string key)
        {
            ThrowIfDisposed();
            return _root.GetBytesInternal(ns, key);
        }

        private byte[]? GetBytesInternal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.GetStringInternal(ns, key);
        }

        private string? GetStringInternal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.GetInt64Internal(ns, key);
        }

        private long? GetInt64Internal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.GetFloat64Internal(ns, key);
        }

        private double? GetFloat64Internal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.GetBoolInternal(ns, key);
        }

        private bool? GetBoolInternal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.ContainsKeyInternal(ns, key);
        }

        private bool ContainsKeyInternal(string? ns, string key)
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
            ThrowIfDisposed();
            return _root.CountInternal(ns);
        }

        private int CountInternal(string? ns)
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
            ThrowIfDisposed();
            return _root.CreateSnapshotInternal(this);
        }

        private Snapshot CreateSnapshotInternal(CkvDb handle)
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
                return new Snapshot(_currentMaxTxId, snapNs, handle);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>快照释放时递减存活计数（由 Snapshot.Dispose 调用，且仅一次）。</summary>
        internal void ReleaseSnapshot() => _root.ReleaseSnapshotInternal();

        private void ReleaseSnapshotInternal()
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

        private void PutInternal(string normNs, string key, byte[] keyBytes, byte valType, byte[] valBytes)
        {
            List<KeyChangeEvent>? events = null;
            ThrowIfDisposed();
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");

                byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
                var entries = new[] { new WalOpEntry(Constants.OpPut, valType, nsBytes, keyBytes, valBytes) };

                ApplyMutation(entries, () =>
                {
                    var store = GetOrCreateStoreForWrite(normNs);
                    store.Put(keyBytes, valType, valBytes);
                });

                events = new List<KeyChangeEvent>
                {
                    new KeyChangeEvent(KeyChangeOp.Put, normNs, key, valType)
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

        public void PutBytes(string? ns, string key, byte[] val)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, val);
            _root.PutInternal(normNs, key, keyBytes, Constants.ValTypeBytes, val);
        }

        public void PutString(string? ns, string key, string val)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (val == null) throw new ArgumentNullException(nameof(val));
            byte[] valBytes = Encoding.UTF8.GetBytes(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            _root.PutInternal(normNs, key, keyBytes, Constants.ValTypeString, valBytes);
        }

        public void PutInt64(string? ns, string key, long val)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            byte[] valBytes = BinaryHelper.EncodeInt64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            _root.PutInternal(normNs, key, keyBytes, Constants.ValTypeInt64, valBytes);
        }

        public void PutFloat64(string? ns, string key, double val)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            byte[] valBytes = BinaryHelper.EncodeFloat64(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            _root.PutInternal(normNs, key, keyBytes, Constants.ValTypeFloat64, valBytes);
        }

        public void PutBool(string? ns, string key, bool val)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            byte[] valBytes = BinaryHelper.EncodeBool(val);
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, valBytes);
            _root.PutInternal(normNs, key, keyBytes, Constants.ValTypeBool, valBytes);
        }

        public long IncrementInt64(string? ns, string key, long delta)
        {
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            return _root.IncrementInt64Internal(ns, key, delta);
        }

        private long IncrementInt64Internal(string? ns, string key, long delta)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            Limits.ValidateKeyAndValueLengths(keyBytes, Array.Empty<byte>());

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");

                long newVal;
                if (!_namespaces.TryGetValue(normNs, out var existingStore))
                {
                    newVal = delta;
                }
                else
                {
                    int idx = existingStore.FindIndex(keyBytes);
                    if (idx < 0)
                    {
                        newVal = delta;
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
                    }
                }

                byte[] valBytes = BinaryHelper.EncodeInt64(newVal);
                byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
                var entries = new[] { new WalOpEntry(Constants.OpPut, Constants.ValTypeInt64, nsBytes, keyBytes, valBytes) };

                ApplyMutation(entries, () =>
                {
                    var store = GetOrCreateStoreForWrite(normNs);
                    store.Put(keyBytes, Constants.ValTypeInt64, valBytes);
                });

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
            CheckWritable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            return _root.DeleteInternal(ns, key);
        }

        private bool DeleteInternal(string? ns, string key)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");
                if (!_namespaces.TryGetValue(normNs, out var store)) return false;
                int idx = store.FindIndex(keyBytes);
                if (idx < 0) return false;

                byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
                var entries = new[] { new WalOpEntry(Constants.OpDelete, Constants.ValTypeBytes, nsBytes, keyBytes, Array.Empty<byte>()) };

                bool deleted = false;
                ApplyMutation(entries, () =>
                {
                    if (_snapshotCount > 0)
                    {
                        store = store.Clone();
                        _namespaces[normNs] = store;
                    }
                    deleted = store.Delete(keyBytes);
                });

                if (deleted)
                {
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
            CheckWritable();
            return _root.DropNamespaceInternal(ns);
        }

        private bool DropNamespaceInternal(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");
                if (!_namespaces.ContainsKey(normNs)) return false;

                byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
                var entries = new[] { new WalOpEntry(Constants.OpDropNamespace, Constants.ValTypeBytes, nsBytes, Array.Empty<byte>(), Array.Empty<byte>()) };

                ApplyMutation(entries, () =>
                {
                    _namespaces.Remove(normNs);
                });

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
            CheckWritable();
            return _root.ClearNamespaceInternal(ns);
        }

        private bool ClearNamespaceInternal(string? ns)
        {
            string normNs = Utf8Helper.NormalizeNamespace(ns);

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");
                if (!_namespaces.TryGetValue(normNs, out var store) || store.Items.Count == 0)
                {
                    return false;
                }

                byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
                var entries = new List<WalOpEntry>(store.Items.Count);
                foreach (var item in store.Items)
                {
                    entries.Add(new WalOpEntry(Constants.OpDelete, Constants.ValTypeBytes, nsBytes, item.Key, Array.Empty<byte>()));
                }

                ApplyMutation(entries, () =>
                {
                    _namespaces[normNs] = new NamespaceStore();
                });

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
            ThrowIfDisposed();
            return _root.KeysInternal(ns, prefix);
        }

        private List<string> KeysInternal(string? ns, string? prefix = null)
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
            ThrowIfDisposed();
            return _root.ScanRangeInternal(ns, startKey, endKey);
        }

        private List<Entry> ScanRangeInternal(string? ns, string? startKey = null, string? endKey = null)
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
            CheckWritable();
            if (action == null) throw new ArgumentNullException(nameof(action));
            _root.BatchInternal(this, action);
        }

        private void BatchInternal(CkvDb callerHandle, Action<BatchWriter> action)
        {
            var writer = new BatchWriter(callerHandle);
            action(writer);

            if (writer.Ops.Count == 0) return;

            List<KeyChangeEvent>? events = null;
            _rwLock.EnterWriteLock();
            try
            {
                if (_options.ReadOnly)
                    throw new LockedError("Database is opened in read-only mode");

                var walEntries = new List<WalOpEntry>();
                foreach (var op in writer.Ops)
                {
                    byte[] nsBytes = Encoding.UTF8.GetBytes(op.Namespace);
                    switch (op.OpType)
                    {
                        case Constants.OpPut:
                            walEntries.Add(new WalOpEntry(Constants.OpPut, op.ValType, nsBytes, op.Key, op.Val));
                            break;
                        case Constants.OpDelete:
                            walEntries.Add(new WalOpEntry(Constants.OpDelete, Constants.ValTypeBytes, nsBytes, op.Key, Array.Empty<byte>()));
                            break;
                        case Constants.OpDropNamespace:
                            walEntries.Add(new WalOpEntry(Constants.OpDropNamespace, Constants.ValTypeBytes, nsBytes, Array.Empty<byte>(), Array.Empty<byte>()));
                            break;
                        case Constants.BatchOpClearStaging:
                            // C3: a Put issued earlier in this same batch is not in
                            // _namespaces yet, so expanding the clear from memory
                            // alone would leave its OpPut in the log with no matching
                            // delete -- replaying the log would bring that key back
                            // to life. Drop the shadowed puts (the clear removes them
                            // again by definition) before collecting the deletes.
                            walEntries.RemoveAll(entry =>
                                entry.OpCode == Constants.OpPut
                                && entry.Namespace.AsSpan().SequenceEqual(nsBytes));

                            if (_namespaces.TryGetValue(op.Namespace, out var clrStore))
                            {
                                foreach (var item in clrStore.Items)
                                {
                                    walEntries.Add(new WalOpEntry(Constants.OpDelete, Constants.ValTypeBytes, nsBytes, item.Key, Array.Empty<byte>()));
                                }
                            }
                            break;
                    }
                }

                events = new List<KeyChangeEvent>();
                ApplyMutation(walEntries, () =>
                {
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
                });
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

        internal bool TryAddRef()
        {
            while (true)
            {
                int count = Volatile.Read(ref _refCount);
                if (count <= 0 || _isClosed)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref _refCount, count + 1, count) == count)
                {
                    return true;
                }
            }
        }

        private readonly object _closeLock = new object();

        internal void ReleaseRef(bool fromFinalizer)
        {
            int remaining = Interlocked.Decrement(ref _refCount);
            if (remaining == 0)
            {
                lock (_closeLock)
                {
                    if (!_isClosed)
                    {
                        _isClosed = true;
                        _isDisposed = true;
                        CloseRoot(fromFinalizer);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _root.ReleaseRef(fromFinalizer: false);
            GC.SuppressFinalize(this);
        }

        ~CkvDb()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _options.OnWarning?.Invoke("onFinalizerLeak", $"CkvDb instance for path '{_path}' was collected by GC without being disposed.");
            }
            catch
            {
            }
            _root.ReleaseRef(fromFinalizer: true);
        }

        private void CloseRoot(bool fromFinalizer)
        {
            if (_syncTimer != null)
            {
                try
                {
                    if (!fromFinalizer)
                    {
                        using var waitHandle = new ManualResetEvent(false);
                        if (_syncTimer.Dispose(waitHandle))
                        {
                            // 等回调真正结束，不设上限：以前 1s 超时后直接继续，会在回调
                            // 仍在写 `.tmp` 时开始最终刷盘（FIX-01 的竞态窗口之一）。
                            waitHandle.WaitOne();
                        }
                    }
                    else
                    {
                        _syncTimer.Dispose();
                    }
                }
                catch
                {
                    _syncTimer.Dispose();
                }
            }

            // 定时器已停且回调已结束，这里只可能与另一个线程的显式 Flush() 竞争。
            bool gateTaken = false;
            try
            {
                if (fromFinalizer)
                {
                    gateTaken = _flushGate.Wait(2000);
                }
                else
                {
                    _flushGate.Wait();
                    gateTaken = true;
                }
            }
            catch
            {
            }

            try
            {
                bool lockTaken = false;
                try
                {
                    if (fromFinalizer)
                    {
                        lockTaken = _rwLock.TryEnterWriteLock(2000);
                    }
                    else
                    {
                        _rwLock.EnterWriteLock();
                        lockTaken = true;
                    }
                }
                catch
                {
                }

                try
                {
                    if (_isDirty && !_options.ReadOnly)
                    {
                        try
                        {
                            if (_options.EngineMode == StorageEngineMode.AppendOnlyLog)
                            {
                                if (File.Exists(_path))
                                {
                                    using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                                    fs.Flush(true);
                                }
                                _isDirty = false;
                            }
                            else
                            {
                                var diskMaxTxId = PrecheckDiskState();
                                CommitFlush(diskMaxTxId);
                            }
                        }
                        catch (Exception ex)
                        {
                            // FIX-06：最终刷盘失败必须上报，不能静默吞掉（磁盘满时数据会丢）。
                            _options.OnWarning?.Invoke(
                                "onFinalFlushFailed",
                                "Final flush during Dispose failed; pending writes may be lost: " + ex);
                        }
                    }

                    // D2：给此前失败的压实再一次机会，避免瞬时故障跟随实例整个生命周期。
                    RetryPendingCompaction();
                }
                finally
                {
                    if (lockTaken)
                    {
                        _rwLock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                if (gateTaken)
                {
                    _flushGate.Release();
                }
            }

            // C9：释放连接锁（锁文件保留为标记文件）
            _connectionLock?.Dispose();
            _connectionLock = null;

            if (_registered && _selfWeak != null)
            {
                // FIX-07：按 (key, value) 移除，而不是只按键移除。旧写法在“本实例已关闭、
                // 同路径又被新实例注册”的时序下会把新实例的条目删掉，破坏单例保护。
                // netstandard2.0 没有 ConcurrentDictionary.TryRemove(KeyValuePair)，
                // 用 ICollection.Remove 完成同样的按键+按值原子移除。
                ((ICollection<KeyValuePair<string, WeakReference<CkvDb>>>)Registry)
                    .Remove(new KeyValuePair<string, WeakReference<CkvDb>>(_canonicalPath, _selfWeak));
                _registered = false;
            }

            // P1-13：删掉 _rwLock.Dispose()。ReaderWriterLockSlim 为纯托管同步原语，
            // 无非托管资源。在关闭时显式 Dispose 会导致并发快照或读者竞态下抛 ObjectDisposedException 崩溃。
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

        /// <summary>
        /// 按本批次可见的状态读取键：暂存区优先；暂存墓碑（Delete / ClearNamespace /
        /// DropNamespace）一律视为"不存在"，即使主库仍有该键；只有本批次未触碰的键才
        /// 回落到已提交状态。
        /// </summary>
        /// <remarks>
        /// 回落读取在数据库读锁内进行，因此能看到其他线程在批次执行期间的提交。需要冻结
        /// 基准视图的调用方请先取 <see cref="CkvDb.CreateSnapshot"/>。
        /// 暂存区从后向前扫描，最近一次操作优先。
        /// </remarks>
        public byte[]? GetBytes(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            for (int i = Ops.Count - 1; i >= 0; i--)
            {
                var op = Ops[i];
                if (!string.Equals(op.Namespace, normNs, StringComparison.Ordinal))
                {
                    continue;
                }
                switch (op.OpType)
                {
                    case Constants.OpPut:
                        if (op.Key.AsSpan().SequenceEqual(keyBytes)) return (byte[])op.Val.Clone();
                        break;
                    case Constants.OpDelete:
                        if (op.Key.AsSpan().SequenceEqual(keyBytes)) return null;
                        break;
                    case Constants.OpDropNamespace:
                    case Constants.BatchOpClearStaging:
                        return null;
                }
            }

            return _db.GetBytes(normNs, key);
        }

        /// <summary>
        /// <see cref="GetBytes"/> 的字符串版本：与 <see cref="CkvDb.GetString"/> 一致，
        /// 要求值以字符串类型写入，否则抛 <see cref="TypeMismatchError"/>。
        /// </summary>
        public string? GetString(string? ns, string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string normNs = Utf8Helper.NormalizeNamespace(ns);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            for (int i = Ops.Count - 1; i >= 0; i--)
            {
                var op = Ops[i];
                if (!string.Equals(op.Namespace, normNs, StringComparison.Ordinal))
                {
                    continue;
                }
                switch (op.OpType)
                {
                    case Constants.OpPut:
                        if (op.Key.AsSpan().SequenceEqual(keyBytes))
                        {
                            if (op.ValType != Constants.ValTypeString)
                            {
                                throw new TypeMismatchError(
                                    $"Key '{key}' has value_type {op.ValType}, expected string (0x01)");
                            }
                            if (!Utf8Helper.IsValid(op.Val))
                            {
                                throw new InvalidUtf8Error($"Invalid UTF-8 sequence in key '{key}'");
                            }
                            return Encoding.UTF8.GetString(op.Val);
                        }
                        break;
                    case Constants.OpDelete:
                        if (op.Key.AsSpan().SequenceEqual(keyBytes)) return null;
                        break;
                    case Constants.OpDropNamespace:
                    case Constants.BatchOpClearStaging:
                        return null;
                }
            }

            return _db.GetString(normNs, key);
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
        /// <summary>FIX-P1-17：Release 的一次性闩锁（取代原来非原子的 bool check-then-set）。</summary>
        private int _released;

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
            Release();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// FIX-05：调用方忘记 Dispose 时的兜底。没有终结器时 <c>_snapshotCount</c> 会永久
        /// 大于 0，此后**每一次写**都退化为全量克隆命名空间——静默且不可恢复的性能降级。
        /// </summary>
        ~Snapshot()
        {
            Release();
        }

        private void Release()
        {
            // FIX-P1-17：原实现是非原子的 check-then-set（`if (_isDisposed) return; _isDisposed = true;`）。
            // 两个线程并发 Dispose（典型的 `using` + 显式 Dispose 重叠）会双双通过检查，
            // ReleaseSnapshot() 被调用两次 → _snapshotCount 提前归零 → 后续写路径的
            // `if (_snapshotCount > 0)` 全为假 → 原地修改 NamespaceStore.Items →
            // 那个**仍然存活**的 Snapshot 正在读的列表被就地改写，快照隔离被打破，
            // 返回撕裂/错误的数据。Interlocked.Exchange 让 Release 恰好生效一次。
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _owner?.ReleaseSnapshot();
            _namespaces = null;
        }
    }

    internal sealed class WalOpEntry
    {
        public byte OpCode { get; set; }
        public byte ValueType { get; set; }
        public byte[] Namespace { get; set; } = Array.Empty<byte>();
        public byte[] Key { get; set; } = Array.Empty<byte>();
        public byte[] Value { get; set; } = Array.Empty<byte>();

        public WalOpEntry() { }

        public WalOpEntry(byte opCode, byte valueType, byte[] ns, byte[] key, byte[] val)
        {
            OpCode = opCode;
            ValueType = valueType;
            Namespace = ns ?? Array.Empty<byte>();
            Key = key ?? Array.Empty<byte>();
            Value = val ?? Array.Empty<byte>();
        }
    }
}
