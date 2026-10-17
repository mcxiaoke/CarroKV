package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// FIX-21 regression: the automatic-compaction trigger must measure garbage
// (file size minus live payload), not the absolute file size.
//
// With the old `newFileSize >= CompactionThresholdBytes` condition, a compaction
// rewrote the file down to exactly the live size, so once live data reached the
// threshold *every* subsequent write paid for a full-database rewrite (measured:
// 26ms/write at 6MB live, i.e. ~38 ops/s instead of ~5,000).
//
// Assertions are behavioural (file-size deltas), not timing based: wall-clock
// thresholds are flaky on CI runners. Mirrors the Rust/.NET/Dart suites.

const cliffThreshold uint64 = 4 * 1024 * 1024

func cliffOptions(threshold uint64) DbOptions {
	o := DefaultDbOptions()
	o.EngineMode = StorageEngineAppendOnlyLog
	o.SyncMode = SyncModeDeferred
	o.SyncIntervalMs = 3600000
	o.CompactionThresholdBytes = threshold
	return o
}

// loadCliffFixture bulk-loads 6000 x 1KB with compaction disabled, so the
// reopened database starts with a garbage-free log that is already above 4MB.
func loadCliffFixture(t *testing.T, path string) {
	t.Helper()
	db, err := Open(path, cliffOptions(0))
	if err != nil {
		t.Fatalf("open fixture: %v", err)
	}
	value := strings.Repeat("v", 1000)
	for i := 0; i < 6000; i++ {
		if err := db.PutString("ns", fmt.Sprintf("k%06d", i), value); err != nil {
			db.Close()
			t.Fatalf("put %d: %v", i, err)
		}
	}
	if err := db.Flush(); err != nil {
		db.Close()
		t.Fatalf("flush: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}
}

func overwriteRange(t *testing.T, db *DB, count int) {
	t.Helper()
	value := strings.Repeat("v", 1000)
	for i := 0; i < count; i++ {
		if err := db.PutString("ns", fmt.Sprintf("k%06d", i), value); err != nil {
			t.Fatalf("overwrite %d: %v", i, err)
		}
	}
}

func TestOverwritesBelowThresholdDoNotRewriteTheWholeDatabase(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cliff.db")
	loadCliffFixture(t, path)

	db, err := Open(path, cliffOptions(cliffThreshold))
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer db.Close()

	before := fileSizeOf(t, path)
	if before < 5_500_000 {
		t.Fatalf("fixture unexpectedly small: %d bytes", before)
	}

	// 50 overwrites = ~50KB of pure garbage, 80x below the 4MB threshold.
	overwriteRange(t, db, 50)

	after := fileSizeOf(t, path)
	if after < before+40_000 {
		t.Fatalf("file did not grow (%d -> %d): every write is still triggering a full-database rewrite",
			before, after)
	}
}

func TestGarbagePastTheThresholdStillCompacts(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "reclaim.db")
	loadCliffFixture(t, path)

	db, err := Open(path, cliffOptions(cliffThreshold))
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer db.Close()

	before := fileSizeOf(t, path)

	// ~4.5MB of pure garbage: must be reclaimed once, back to roughly live size.
	overwriteRange(t, db, 4500)

	after := fileSizeOf(t, path)
	if after >= before+2_000_000 {
		t.Fatalf("compaction did not reclaim %d -> %d bytes of garbage", before, after)
	}
	_ = os.Remove(path)
}
