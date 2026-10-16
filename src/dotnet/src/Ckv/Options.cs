using System;

namespace Ckv
{
    /// <summary>告警通知回调：onWarning(type, message)（如从备份自愈恢复）。</summary>
    public delegate void WarningCallback(string type, string message);

    public sealed class DbOptions
    {
        public bool ReadOnly { get; set; } = false;
        public uint LockTimeoutMs { get; set; } = 10000;
        /// <summary>启动时若发现遗留锁，强制清除（单实例桌面 App 崩溃重启场景）。</summary>
        public bool ForceUnlock { get; set; } = false;
        /// <summary>告警通知回调（如从备份自愈恢复）。</summary>
        public WarningCallback? OnWarning { get; set; }

        /// <summary>数据落盘同步模式（默认 Immediate）。</summary>
        public SyncMode SyncMode { get; set; } = SyncMode.Immediate;

        /// <summary>Deferred 模式下后台合并落盘的间隔毫秒（默认 100ms）。</summary>
        public uint SyncIntervalMs { get; set; } = 100;

        /// <summary>底层存储引擎模式（默认 SnapshotAtomic）。</summary>
        public StorageEngineMode EngineMode { get; set; } = StorageEngineMode.SnapshotAtomic;

        /// <summary>AppendOnlyLog 模式下触发自动压实的文件大小阈值（默认 4MB）。</summary>
        public ulong CompactionThresholdBytes { get; set; } = 4 * 1024 * 1024;

        /// <summary>AppendOnlyLog 模式下触发自动压实的死数据比例阈值（默认 0.30 即 30%）。</summary>
        public double CompactionDeadRatio { get; set; } = 0.30;

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
                ForceUnlock = this.ForceUnlock,
                OnWarning = this.OnWarning,
                SyncMode = this.SyncMode,
                SyncIntervalMs = this.SyncIntervalMs,
                EngineMode = this.EngineMode,
                CompactionThresholdBytes = this.CompactionThresholdBytes,
                CompactionDeadRatio = this.CompactionDeadRatio,
                SyncOnWrite = this.SyncOnWrite,
                AutoRecoverFromBackup = this.AutoRecoverFromBackup,
                StrictCompaction = this.StrictCompaction
            };
        }
    }
}
