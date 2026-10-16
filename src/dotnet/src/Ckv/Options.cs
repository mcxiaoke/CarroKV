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

        // ---- 以下为 .NET 平台扩展项（规范未定义，跨语言 API 差异点）----
        public bool SyncOnWrite { get; set; } = true;
        public bool AutoRecoverFromBackup { get; set; } = true;

        public static DbOptions Default => new DbOptions();

        public DbOptions Clone()
        {
            return new DbOptions
            {
                ReadOnly = this.ReadOnly,
                LockTimeoutMs = this.LockTimeoutMs,
                ForceUnlock = this.ForceUnlock,
                OnWarning = this.OnWarning,
                SyncOnWrite = this.SyncOnWrite,
                AutoRecoverFromBackup = this.AutoRecoverFromBackup
            };
        }
    }
}
