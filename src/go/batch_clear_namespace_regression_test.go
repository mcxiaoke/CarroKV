package ckv

// C3: `Batch { Put(ns, k); ClearNamespace(ns); }` used to resurrect `k` after a
// restart.
//
// When the batch builds its log entries, ClearNamespace was expanded from the
// in-memory state *as it was before the batch ran*. A key written by a Put
// earlier in the same batch is not in that state yet, so no delete was emitted
// for it while its OpPut was already in the log. The in-memory state was then
// cleared for real, so the divergence only appeared when the log was replayed --
// i.e. after a reopen, which is exactly what the existing batch tests never did.
//
// The mirrored scenario runs in both engine modes, because the two modes must
// agree on one write sequence and only the append-only log can diverge here.

import (
	"path/filepath"
	"testing"
)

func batchClearOptions(mode StorageEngineMode) DbOptions {
	opts := DefaultDbOptions()
	opts.EngineMode = mode
	opts.SyncMode = SyncModeImmediate
	return opts
}

// Put then ClearNamespace in one batch: the key must be gone in memory *and* gone
// after replaying the log from disk.
func assertPutThenClearDoesNotResurrect(t *testing.T, mode StorageEngineMode) {
	t.Helper()

	dir := t.TempDir()
	dbPath := filepath.Join(dir, "c3_put_clear.db")
	opts := batchClearOptions(mode)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("ns", "old", "old_value"); err != nil {
		t.Fatalf("put: %v", err)
	}
	if err := db.PutString("other", "keep", "keep_value"); err != nil {
		t.Fatalf("put: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	if err := db.Batch(func(b *BatchWriter) error {
		if err := b.PutString("ns", "fresh", "fresh_value"); err != nil {
			return err
		}
		if err := b.ClearNamespace("ns"); err != nil {
			return err
		}
		// A different namespace must be unaffected by the clear.
		return b.PutString("other", "keep2", "keep2_value")
	}); err != nil {
		t.Fatalf("batch: %v", err)
	}

	if got, _ := db.GetString("ns", "fresh"); got != nil {
		t.Fatalf("memory: the key written before the clear must be gone, got %v", *got)
	}
	if got, _ := db.GetString("ns", "old"); got != nil {
		t.Fatalf("memory: 'old' must be gone, got %v", *got)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	// Replay from disk: this is where the key used to come back to life.
	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("replay open: %v", err)
	}
	defer db.Close()

	if got, _ := db.GetString("ns", "fresh"); got != nil {
		t.Fatalf("replay: 'fresh' was resurrected (C3), got %v", *got)
	}
	if got, _ := db.GetString("ns", "old"); got != nil {
		t.Fatalf("replay: 'old' must stay deleted, got %v", *got)
	}
	if got, _ := db.GetString("other", "keep"); got == nil || *got != "keep_value" {
		t.Fatalf("the clear must not touch other namespaces, got %v", got)
	}
	if got, _ := db.GetString("other", "keep2"); got == nil || *got != "keep2_value" {
		t.Fatalf("the clear must not touch other namespaces, got %v", got)
	}
}

// The mirror image: a Put issued *after* the clear must survive, otherwise the
// fix for the case above would be dropping live writes instead of shadowed ones.
func assertClearThenPutSurvives(t *testing.T, mode StorageEngineMode) {
	t.Helper()

	dir := t.TempDir()
	dbPath := filepath.Join(dir, "c3_clear_put.db")
	opts := batchClearOptions(mode)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("ns", "old", "old_value"); err != nil {
		t.Fatalf("put: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	if err := db.Batch(func(b *BatchWriter) error {
		if err := b.ClearNamespace("ns"); err != nil {
			return err
		}
		return b.PutString("ns", "fresh", "fresh_value")
	}); err != nil {
		t.Fatalf("batch: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("replay open: %v", err)
	}
	defer db.Close()

	if got, _ := db.GetString("ns", "old"); got != nil {
		t.Fatalf("'old' must stay deleted, got %v", *got)
	}
	if got, _ := db.GetString("ns", "fresh"); got == nil || *got != "fresh_value" {
		t.Fatalf("a put issued after the clear must survive the replay, got %v", got)
	}
}

func TestBatchPutThenClearMustNotResurrectInWalMode(t *testing.T) {
	assertPutThenClearDoesNotResurrect(t, StorageEngineAppendOnlyLog)
}

func TestBatchPutThenClearMustNotResurrectInSnapshotMode(t *testing.T) {
	assertPutThenClearDoesNotResurrect(t, StorageEngineSnapshotAtomic)
}

func TestBatchClearThenPutMustSurviveReplayInWalMode(t *testing.T) {
	assertClearThenPutSurvives(t, StorageEngineAppendOnlyLog)
}

func TestBatchClearThenPutMustSurviveReplayInSnapshotMode(t *testing.T) {
	assertClearThenPutSurvives(t, StorageEngineSnapshotAtomic)
}
