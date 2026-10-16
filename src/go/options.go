package ckv

type WarningCallback func(errType, message string)

type DbOptions struct {
	ReadOnly                bool
	LockTimeoutMs           uint32
	ForceUnlock             bool
	OnWarning               WarningCallback
	SyncMode                SyncMode
	SyncIntervalMs          uint32
	EngineMode              StorageEngineMode
	CompactionThresholdBytes uint64
	CompactionDeadRatio     float64
}

func DefaultDbOptions() DbOptions {
	return DbOptions{
		ReadOnly:                false,
		LockTimeoutMs:           10000,
		ForceUnlock:             false,
		OnWarning:               nil,
		SyncMode:                SyncModeImmediate,
		SyncIntervalMs:          100,
		EngineMode:              StorageEngineSnapshotAtomic,
		CompactionThresholdBytes: 4 * 1024 * 1024,
		CompactionDeadRatio:     0.30,
	}
}

