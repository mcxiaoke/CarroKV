using System;

namespace Ckv
{
    /// <summary>告警通知回调：onWarning(type, message)（如从备份自愈恢复）。</summary>
    public delegate void WarningCallback(string type, string message);

    public sealed class DbOptions
    {
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// <c>Open</c> 等待其他连接释放文件锁的毫秒数（默认 10000）。
        /// 自 C9 起锁是由操作系统持有的内核锁、覆盖整个连接生命周期，因此该值**只是等待预算**：
        /// 不再有基于锁文件年龄的判活，也不再有窃取协议；崩溃进程留下的锁由操作系统释放。
        /// </summary>
        public uint LockTimeoutMs { get; set; } = 10000;
        /// <summary>告警通知回调（如从备份自愈恢复）。</summary>
        public WarningCallback? OnWarning { get; set; }

        /// <summary>数据落盘同步模式（默认 Immediate）。</summary>
        public SyncMode SyncMode { get; set; } = SyncMode.Immediate;

        /// <summary>Deferred 模式下后台合并落盘的间隔毫秒（默认 100ms）。</summary>
        public uint SyncIntervalMs { get; set; } = 100;

        /// <summary>
        /// 底层存储引擎模式（默认 <c>SnapshotAtomic</c>）。
        ///
        /// <para><b>⚠️ <c>SnapshotAtomic</c> 的写成本是 O(库大小)。</b>
        /// 它每次写入都会<em>全量序列化整库并 fsync</em>，代价随库大小线性增长。
        /// 这是默认配置，但没有任何运行时护栏或告警阻止你把它用在远大于建议规模的库上
        /// ——到那时吞吐会比 <c>AppendOnlyLog</c> 差两个数量级。</para>
        ///
        /// <para>实测（本机 Windows 10，稳态写延迟中位数）：</para>
        /// <list type="table">
        ///   <item><term>存活条目</term><description>SnapshotAtomic + Immediate / AppendOnlyLog</description></item>
        ///   <item><term>500</term><description>2.29 ms (~437 ops/s) / 0.18 ms (~5,500 ops/s)，与规模无关</description></item>
        ///   <item><term>2,000</term><description>2.93 ms (~341 ops/s) / 同上</description></item>
        ///   <item><term>5,000</term><description>4.43 ms (~226 ops/s) / 同上</description></item>
        ///   <item><term>~50,000（外推，约 5.7 MB）</term><description>~40 ms (~25 ops/s) / 同上</description></item>
        /// </list>
        ///
        /// <para><b>选型建议</b>：数据量超过约 2 MB，或存在高频写入时，请改用
        /// <c>StorageEngineMode.AppendOnlyLog</c>。</para>
        /// </summary>
        public StorageEngineMode EngineMode { get; set; } = StorageEngineMode.SnapshotAtomic;

        /// <summary>AppendOnlyLog 模式下触发自动压实的文件大小阈值（默认 4MB）。</summary>
        public ulong CompactionThresholdBytes { get; set; } = 4 * 1024 * 1024;

        // ---- 以下为 .NET 平台扩展项（规范未定义，跨语言 API 差异点）----
        public bool SyncOnWrite { get; set; } = true;

        /// <summary>
        /// 是否允许打开时用 `.bak` 覆盖损坏的主库。
        ///
        /// 默认 <c>false</c>（决策 D1）：`.bak` 是上一次轮转**之前**的一代，
        /// 用它替换主库会静默回滚此后写入的全部数据；它现在的定位是**人工恢复用的离线副本**。
        /// 只读模式下该开关不生效，且恢复路径不会写盘（D1-9）。
        /// </summary>
        public bool AutoRecoverFromBackup { get; set; } = false;

        /// <summary>
        /// 自动压实失败时是否向上抛出。
        ///
        /// 默认 <c>false</c>（决策 D2）：触发压实失败的这次写入其实已经 append + fsync 落盘，
        /// 向调用方报错等于让它重试一个已经成功的写（`Increment` 是 read-modify-write，
        /// 重试会重复累加 delta）。默认改为告警 + 稍后重试；需要"日志绝不允许越过阈值"的
        /// 场景可显式开启，代价是写入可能报错但数据已持久化。
        /// </summary>
        public bool StrictCompaction { get; set; } = false;

        public static DbOptions Default => new DbOptions();

        public DbOptions Clone()
        {
            return new DbOptions
            {
                ReadOnly = this.ReadOnly,
                LockTimeoutMs = this.LockTimeoutMs,
                OnWarning = this.OnWarning,
                SyncMode = this.SyncMode,
                SyncIntervalMs = this.SyncIntervalMs,
                EngineMode = this.EngineMode,
                CompactionThresholdBytes = this.CompactionThresholdBytes,
                SyncOnWrite = this.SyncOnWrite,
                AutoRecoverFromBackup = this.AutoRecoverFromBackup,
                StrictCompaction = this.StrictCompaction
            };
        }
    }
}
