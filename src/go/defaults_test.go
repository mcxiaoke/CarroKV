package ckv

import "testing"

// TestDefaultDbOptionsMatrix guards the cross-language default matrix
// documented in KV-DB-DESIGN-SPEC.md §5.1.1.1: every implementation must ship
// the same conservative, durability-first defaults. A change here is an
// ADR-level decision and must be applied to all five languages in one batch.
func TestDefaultDbOptionsMatrix(t *testing.T) {
	o := DefaultDbOptions()

	if o.ReadOnly {
		t.Fatal("ReadOnly: want false")
	}
	if o.AutoRecoverFromBackup {
		t.Fatal("AutoRecoverFromBackup: want false (D1)")
	}
	if o.SyncMode != SyncModeImmediate {
		t.Fatalf("SyncMode: want Immediate, got %v", o.SyncMode)
	}
	if o.SyncIntervalMs != 100 {
		t.Fatalf("SyncIntervalMs: want 100, got %d", o.SyncIntervalMs)
	}
	if o.EngineMode != StorageEngineSnapshotAtomic {
		t.Fatalf("EngineMode: want SnapshotAtomic, got %v", o.EngineMode)
	}
	if o.CompactionThresholdBytes != 4*1024*1024 {
		t.Fatalf("CompactionThresholdBytes: want 4194304, got %d", o.CompactionThresholdBytes)
	}
	if o.LockTimeoutMs != 10000 {
		t.Fatalf("LockTimeoutMs: want 10000, got %d", o.LockTimeoutMs)
	}
	if o.StrictCompaction {
		t.Fatal("StrictCompaction: want false (D2)")
	}
}
