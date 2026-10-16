package ckv

// D2: the compaction-failure contract.
//
// A failed automatic compaction must not fail the caller's write. By the time
// compaction runs, the mutation is already appended and synced, so returning an
// error would invite a retry of a write that succeeded -- and Increment, being a
// read-modify-write, would then apply its delta twice. The failure is reported
// through onCompactionFailed and retried later (widening backoff, plus one
// best-effort attempt from Close); StrictCompaction opts into propagating.
//
// Compaction is made to fail with a real filesystem condition rather than a test
// hook: commitFlush writes `<db>.tmp` before rotating, and a *directory* at that
// path cannot be opened as a file while appends to `<db>` keep working.

import (
	"fmt"
	"os"
	"path/filepath"
	"testing"
)

const d2Threshold = 512

func d2Options(onCompactionFailed func()) DbOptions {
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate
	opts.CompactionThresholdBytes = d2Threshold
	opts.OnWarning = func(code, _ string) {
		if code == "onCompactionFailed" && onCompactionFailed != nil {
			onCompactionFailed()
		}
	}
	return opts
}

func d2TmpPath(dbPath string) string { return dbPath + ".tmp" }

func d2BlockCompaction(t *testing.T, dbPath string) {
	t.Helper()
	tmp := d2TmpPath(dbPath)
	if err := os.Mkdir(tmp, 0o755); err != nil {
		t.Fatalf("block compaction: %v", err)
	}
	// Keep the directory non-empty: recovery tries to remove a stale `.tmp`, and
	// on Windows os.Remove falls back to RemoveDirectory, which succeeds for an
	// empty directory -- the blocker has to survive that.
	if err := os.WriteFile(filepath.Join(tmp, "keep"), []byte("x"), 0o644); err != nil {
		t.Fatalf("block compaction (keep file): %v", err)
	}
}

func d2UnblockCompaction(dbPath string) {
	tmp := d2TmpPath(dbPath)
	_ = os.Remove(filepath.Join(tmp, "keep"))
	_ = os.Remove(tmp)
}

// d2Seed creates the database so the `.tmp` path lives in an existing directory
// before it is blocked (recovery ignores an unremovable `.tmp` when the primary
// file is fine).
func d2Seed(t *testing.T, dbPath string, opts DbOptions) {
	t.Helper()
	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("seed open: %v", err)
	}
	if err := db.PutString("", "seed", "seed_value"); err != nil {
		t.Fatalf("seed put: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("seed close: %v", err)
	}
}

// D2-1: the write returns success, the failure is warned about, and every write
// made while compaction was failing is still durable.
func TestCompactionFailureMustNotFailTheWrite(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t6a.db")

	failures := 0
	opts := d2Options(func() { failures++ })

	d2Seed(t, dbPath, opts)
	d2BlockCompaction(t, dbPath)

	const keys = 30
	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for i := 0; i < keys; i++ {
		if err := db.PutString("", fmt.Sprintf("k%03d", i), fmt.Sprintf("v%03d", i)); err != nil {
			t.Fatalf("a failed compaction must not fail the write (D2): %v", err)
		}
	}
	if failures < 1 {
		t.Fatal("the compaction failure must be reported through onCompactionFailed")
	}
	if _, err := os.Stat(dbPath + ".bak"); err == nil {
		t.Fatal("the test must actually have blocked compaction")
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	// Replay: nothing written while compaction was failing may be lost.
	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("replay open: %v", err)
	}
	defer db.Close()

	if got, _ := db.GetString("", "seed"); got == nil || *got != "seed_value" {
		t.Fatalf("seed must survive, got %v", got)
	}
	for i := 0; i < keys; i++ {
		key := fmt.Sprintf("k%03d", i)
		want := fmt.Sprintf("v%03d", i)
		if got, _ := db.GetString("", key); got == nil || *got != want {
			t.Fatalf("key %s must survive a failed compaction, got %v", key, got)
		}
	}
}

// D2-4: retries are throttled. With the failure persisting, no retry happens
// inside the first backoff window (64 writes), and crossing it triggers exactly
// one retry.
func TestCompactionBackoffMustThrottleRetries(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t6b.db")

	failures := 0
	opts := d2Options(func() { failures++ })

	d2Seed(t, dbPath, opts)
	d2BlockCompaction(t, dbPath)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer db.Close()

	written := 0
	for failures == 0 {
		if err := db.PutString("", fmt.Sprintf("a%03d", written), "v"); err != nil {
			t.Fatalf("put: %v", err)
		}
		written++
		if written > 500 {
			t.Fatal("the compaction failure was never triggered")
		}
	}
	first := failures
	if first != 1 {
		t.Fatalf("crossing the threshold must trigger exactly one attempt, got %d", first)
	}

	// Still inside the first backoff window (64 writes since the failure).
	for i := 0; i < 30; i++ {
		if err := db.PutString("", fmt.Sprintf("b%03d", written), "v"); err != nil {
			t.Fatalf("put: %v", err)
		}
		written++
	}
	if failures != first {
		t.Fatalf("retries must be suppressed inside the backoff window, failures=%d", failures)
	}

	// Crossing the window attempts exactly one retry (which fails again).
	for i := 0; i < 40; i++ {
		if err := db.PutString("", fmt.Sprintf("c%03d", written), "v"); err != nil {
			t.Fatalf("put: %v", err)
		}
		written++
	}
	if failures != first+1 {
		t.Fatalf("exactly one retry after the backoff window elapses, failures=%d", failures)
	}
}

// D2-3: Close gives a pending compaction one more chance, so a transient failure
// does not outlive the handle.
func TestCloseMustRetryAPendingCompaction(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t6c.db")

	failures := 0
	opts := d2Options(func() { failures++ })

	d2Seed(t, dbPath, opts)
	d2BlockCompaction(t, dbPath)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for i := 0; i < 30; i++ {
		if err := db.PutString("", fmt.Sprintf("k%03d", i), "v"); err != nil {
			t.Fatalf("put: %v", err)
		}
	}
	if failures < 1 {
		t.Fatal("the compaction failure must have been triggered")
	}
	if _, err := os.Stat(dbPath + ".bak"); err == nil {
		t.Fatal("compaction must really have failed")
	}

	// Clear the obstacle: the retry from Close must now succeed.
	d2UnblockCompaction(dbPath)
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	if _, err := os.Stat(dbPath + ".bak"); err != nil {
		t.Fatal("Close must retry a pending compaction")
	}
}

// D2-5: StrictCompaction surfaces the failure to the caller. The write that
// reports the error is nevertheless durable, which is exactly why the default is
// not to report it.
func TestStrictCompactionMustSurfaceTheError(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t6d.db")

	failures := 0
	opts := d2Options(func() { failures++ })
	opts.StrictCompaction = true

	d2Seed(t, dbPath, opts)
	d2BlockCompaction(t, dbPath)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}

	failingKey := ""
	for i := 0; i < 30; i++ {
		key := fmt.Sprintf("k%03d", i)
		if err := db.PutString("", key, "v"); err != nil {
			failingKey = key
			break
		}
	}
	if failingKey == "" {
		t.Fatal("StrictCompaction must surface the compaction failure")
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}
	d2UnblockCompaction(dbPath)

	// The documented caveat: an error here means "compaction failed", not "the
	// write was lost" -- the frame was already appended and synced.
	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("replay open: %v", err)
	}
	defer db.Close()

	if got, _ := db.GetString("", failingKey); got == nil || *got != "v" {
		t.Fatalf("the write that reported the strict error must still be on disk, got %v", got)
	}
}
