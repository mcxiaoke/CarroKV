package ckv

type WarningCallback func(errType, message string)

type DbOptions struct {
	ReadOnly bool
	// LockTimeoutMs is how long Open waits for another connection to release the
	// file lock. Since C9 the lock is a kernel lock held for the whole lifetime of
	// the connection, so this is purely a wait budget: there is no clock-based
	// staleness judgement and no lock stealing, and a lock left behind by a crashed
	// process is released by the operating system.
	LockTimeoutMs  uint32
	OnWarning      WarningCallback
	SyncMode       SyncMode
	SyncIntervalMs uint32
	// ⚠️ StorageEngineSnapshotAtomic 的写成本是 O(库大小)。
	//
	// 它每次写入都会**全量序列化整库并 fsync**，代价随库大小线性增长。这是默认配置
	// （DefaultDbOptions 返回 StorageEngineSnapshotAtomic），但没有任何运行时护栏或告警
	// 阻止你把它用在远大于建议规模的库上——到那时吞吐会比 StorageEngineAppendOnlyLog
	// 差两个数量级。
	//
	// 实测（本机 Windows 10，稳态写延迟中位数）：
	//
	//	存活条目   SnapshotAtomic+Immediate   AppendOnlyLog
	//	     500    2.29 ms (~437 ops/s)        0.18 ms (~5,500 ops/s)，与规模无关
	//	   2,000    2.93 ms (~341 ops/s)        同上
	//	   5,000    4.43 ms (~226 ops/s)        同上
	//	  ~50,000   ~40 ms (~25 ops/s) 外推     同上
	//
	// 选型建议：数据量超过约 2 MB，或存在高频写入时，请用 StorageEngineAppendOnlyLog。
	EngineMode               StorageEngineMode
	CompactionThresholdBytes uint64
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
		ReadOnly:                 false,
		LockTimeoutMs:            10000,
		OnWarning:                nil,
		SyncMode:                 SyncModeImmediate,
		SyncIntervalMs:           100,
		EngineMode:               StorageEngineSnapshotAtomic,
		CompactionThresholdBytes: 4 * 1024 * 1024,
		AutoRecoverFromBackup:    false,
		StrictCompaction:         false,
	}
}
