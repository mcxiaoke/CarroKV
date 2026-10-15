using System;

namespace Ckv
{
    public sealed class DbOptions
    {
        public bool ReadOnly { get; set; } = false;
        public uint LockTimeoutMs { get; set; } = 10000;
        public bool SyncOnWrite { get; set; } = true;
        public bool AutoRecoverFromBackup { get; set; } = true;

        public static DbOptions Default => new DbOptions();

        public DbOptions Clone()
        {
            return new DbOptions
            {
                ReadOnly = this.ReadOnly,
                LockTimeoutMs = this.LockTimeoutMs,
                SyncOnWrite = this.SyncOnWrite,
                AutoRecoverFromBackup = this.AutoRecoverFromBackup
            };
        }
    }
}
