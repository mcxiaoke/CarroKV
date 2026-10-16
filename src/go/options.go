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
	// AutoRecoverFromBackup allows a corrupt primary file to be replaced by
	// `.bak` while opening. Default false (decision D1): `.bak` holds the
	// generation from *before* the last rotation, so using it silently rolls
	// back every write made since. It is an offline copy for manual recovery.
	AutoRecoverFromBackup bool

	// StrictCompaction propagates automatic-compaction failures to the caller
	// instead of warning and retrying later. Default false (decision D2): the
	// mutation that triggered the failure is already durable, so reporting an
	// error would invite a retry of a write that succeeded.
	StrictCompaction bool
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
		AutoRecoverFromBackup:   false,
		StrictCompaction:        false,
	}
}

