package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
)

func TestWalAppendAndPersistence(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "wal_test.db")

	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate

	{
		db, err := Open(dbPath, opts)
		if err != nil {
			t.Fatalf("Open failed: %v", err)
		}
		if err := db.PutString("", "k1", "v1"); err != nil {
			t.Fatalf("PutString failed: %v", err)
		}
		if err := db.PutString("", "k2", "v2"); err != nil {
			t.Fatalf("PutString failed: %v", err)
		}
		if err := db.PutString("", "k1", "v1_updated"); err != nil {
			t.Fatalf("PutString failed: %v", err)
		}
		if _, err := db.Delete("", "k2"); err != nil {
			t.Fatalf("Delete failed: %v", err)
		}
		if err := db.Close(); err != nil {
			t.Fatalf("Close failed: %v", err)
		}
	}

	// Reopen
	{
		db, err := Open(dbPath, opts)
		if err != nil {
			t.Fatalf("Reopen failed: %v", err)
		}
		defer db.Close()

		v1, err := db.GetString("", "k1")
		if err != nil || v1 == nil || *v1 != "v1_updated" {
			t.Fatalf("expected v1_updated, got %v (err: %v)", v1, err)
		}
		v2, err := db.GetString("", "k2")
		if err != nil || v2 != nil {
			t.Fatalf("expected nil for deleted k2, got %v (err: %v)", v2, err)
		}
	}
}

func TestWalTailTruncationRecovery(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "wal_trunc_test.db")

	var warned int32
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate
	opts.OnWarning = func(code, msg string) {
		if code == "onTailTruncated" {
			atomic.StoreInt32(&warned, 1)
		}
	}

	// Step 1: Write healthy data
	{
		db, err := Open(dbPath, opts)
		if err != nil {
			t.Fatalf("Open failed: %v", err)
		}
		_ = db.PutString("", "healthy1", "val1")
		_ = db.PutString("", "healthy2", "val2")
		_ = db.Close()
	}

	fi, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("Stat failed: %v", err)
	}
	healthyLen := fi.Size()
	if healthyLen <= 32 {
		t.Fatalf("expected healthy file size > 32, got %d", healthyLen)
	}

	// Step 2: Inject corrupt partial trailing bytes
	f, err := os.OpenFile(dbPath, os.O_APPEND|os.O_WRONLY, 0666)
	if err != nil {
		t.Fatalf("OpenFile failed: %v", err)
	}
	if _, err := f.Write([]byte("CORRUPT_TRAIL!!")); err != nil {
		t.Fatalf("Write corrupt trail failed: %v", err)
	}
	_ = f.Close()

	corruptFi, _ := os.Stat(dbPath)
	if corruptFi.Size() != healthyLen+15 {
		t.Fatalf("expected size %d, got %d", healthyLen+15, corruptFi.Size())
	}

	// Step 3: Reopen - auto truncate bad tail and heal
	{
		db, err := Open(dbPath, opts)
		if err != nil {
			t.Fatalf("Open with tail corruption failed: %v", err)
		}
		defer db.Close()

		if atomic.LoadInt32(&warned) != 1 {
			t.Fatalf("expected onTailTruncated warning callback")
		}

		v1, err := db.GetString("", "healthy1")
		if err != nil || v1 == nil || *v1 != "val1" {
			t.Fatalf("expected val1, got %v", v1)
		}

		healedFi, _ := os.Stat(dbPath)
		if healedFi.Size() != healthyLen {
			t.Fatalf("expected healed file size %d, got %d", healthyLen, healedFi.Size())
		}

		// Verify we can continue writing
		if err := db.PutString("", "healthy3", "val3"); err != nil {
			t.Fatalf("PutString after heal failed: %v", err)
		}
		v3, _ := db.GetString("", "healthy3")
		if v3 == nil || *v3 != "val3" {
			t.Fatalf("expected val3, got %v", v3)
		}
	}
}

func TestWalCompactionAndBakRotation(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "wal_compact_test.db")

	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	for i := 0; i < 100; i++ {
		_ = db.PutString("", "heavy_key", fmt.Sprintf("value_generation_%d", i))
	}

	bloatedFi, _ := os.Stat(dbPath)
	bloatedSize := bloatedFi.Size()
	if bloatedSize <= 3000 {
		t.Fatalf("expected bloated size > 3000, got %d", bloatedSize)
	}

	if err := db.Compact(); err != nil {
		t.Fatalf("Compact failed: %v", err)
	}

	compactFi, _ := os.Stat(dbPath)
	compactSize := compactFi.Size()
	if compactSize >= bloatedSize {
		t.Fatalf("expected compact size %d < bloated size %d", compactSize, bloatedSize)
	}

	bakPath := dbPath + ".bak"
	bakFi, err := os.Stat(bakPath)
	if err != nil || bakFi.Size() != bloatedSize {
		t.Fatalf("expected .bak with bloated size %d, got err: %v", bloatedSize, err)
	}

	v, err := db.GetString("", "heavy_key")
	if err != nil || v == nil || *v != "value_generation_99" {
		t.Fatalf("expected value_generation_99, got %v", v)
	}
}
