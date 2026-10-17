using Xunit;

namespace Ckv.Tests
{
    // Guards the cross-language default matrix documented in
    // `KV-DB-DESIGN-SPEC.md` §5.1.1.1: every implementation must ship the same
    // conservative, durability-first defaults. A change here is an ADR-level
    // decision and must be applied to all five languages in one batch.
    public class DefaultOptionsTest
    {
        [Fact]
        public void Test_Default_Options_Matrix()
        {
            var o = new DbOptions();

            Assert.False(o.ReadOnly);
            Assert.False(o.AutoRecoverFromBackup); // D1
            Assert.Equal(SyncMode.Immediate, o.SyncMode);
            Assert.Equal(100u, o.SyncIntervalMs);
            Assert.Equal(StorageEngineMode.SnapshotAtomic, o.EngineMode);
            Assert.Equal(4UL * 1024 * 1024, o.CompactionThresholdBytes);
            Assert.Equal(10000u, o.LockTimeoutMs);
            Assert.True(o.SyncOnWrite);
            Assert.False(o.StrictCompaction); // D2
        }
    }
}
