package ckv

import (
	"path/filepath"
	"testing"
)

// Read-your-own-writes inside Batch: staged writes win, staged tombstones hide
// committed values, untouched keys fall through to the committed state.

func TestBatchReadYourOwnWrites(t *testing.T) {
	dir := t.TempDir()
	db, err := Open(filepath.Join(dir, "ryow.db"), DefaultDbOptions())
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	if err := db.PutString("", "a", "committed"); err != nil {
		t.Fatalf("PutString: %v", err)
	}
	if err := db.PutString("", "b", "committed-b"); err != nil {
		t.Fatalf("PutString: %v", err)
	}

	err = db.Batch(func(b *BatchWriter) error {
		// Untouched key: committed state.
		v, getErr := b.GetString("", "a")
		if getErr != nil || v == nil || *v != "committed" {
			t.Fatalf("base read of a: v=%v err=%v", v, getErr)
		}

		if putErr := b.PutString("", "a", "staged"); putErr != nil {
			return putErr
		}
		if v, _ = b.GetString("", "a"); v == nil || *v != "staged" {
			t.Fatalf("staged read of a: v=%v", v)
		}

		if putErr := b.PutString("", "a", "staged-2"); putErr != nil {
			return putErr
		}
		if v, _ = b.GetString("", "a"); v == nil || *v != "staged-2" {
			t.Fatalf("latest staged put must win: v=%v", v)
		}

		if delErr := b.Delete("", "b"); delErr != nil {
			return delErr
		}
		if v, _ = b.GetString("", "b"); v != nil {
			t.Fatalf("deleted key must read as absent inside the batch, got %q", *v)
		}

		// Brand new key never committed.
		if putErr := b.PutInt64("", "n", 42); putErr != nil {
			return putErr
		}
		if raw, _ := b.GetBytes("", "n"); len(raw) != 8 {
			t.Fatalf("staged int64 should be 8 bytes, got %d", len(raw))
		}

		// Type mismatch is enforced inside staging too.
		if _, tmErr := b.GetString("", "n"); tmErr == nil {
			t.Fatal("GetString on a staged int64 must fail with TypeMismatchError")
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch: %v", err)
	}

	if v, _ := db.GetString("", "a"); v == nil || *v != "staged-2" {
		t.Fatalf("committed a: %v", v)
	}
	if v, _ := db.GetString("", "b"); v != nil {
		t.Fatalf("b should be deleted, got %q", *v)
	}
	if n, _ := db.GetInt64("", "n"); n == nil || *n != 42 {
		t.Fatalf("n: want 42, got %v", n)
	}
}

func TestBatchClearAndDropHideStagedWrites(t *testing.T) {
	dir := t.TempDir()
	db, err := Open(filepath.Join(dir, "ryow_ns.db"), DefaultDbOptions())
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	if err := db.PutString("users", "u1", "alice"); err != nil {
		t.Fatalf("PutString: %v", err)
	}

	err = db.Batch(func(b *BatchWriter) error {
		if putErr := b.PutString("users", "u2", "bob"); putErr != nil {
			return putErr
		}
		if v, _ := b.GetString("users", "u2"); v == nil || *v != "bob" {
			t.Fatalf("staged u2: %v", v)
		}

		if clrErr := b.ClearNamespace("users"); clrErr != nil {
			return clrErr
		}
		if v, _ := b.GetString("users", "u1"); v != nil {
			t.Fatal("clear must hide committed u1")
		}
		if v, _ := b.GetString("users", "u2"); v != nil {
			t.Fatal("clear must hide staged u2")
		}

		// A put staged after the clear is visible again.
		if putErr := b.PutString("users", "u3", "carol"); putErr != nil {
			return putErr
		}
		if v, _ := b.GetString("users", "u3"); v == nil || *v != "carol" {
			t.Fatalf("post-clear staged put: %v", v)
		}

		// Drop hides everything for that namespace.
		if dropErr := b.DropNamespace("users"); dropErr != nil {
			return dropErr
		}
		if v, _ := b.GetString("users", "u3"); v != nil {
			t.Fatal("drop must hide post-clear staged put")
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch: %v", err)
	}

	if n, _ := db.Count("users"); n != 0 {
		t.Fatalf("users count after drop: want 0, got %d", n)
	}
}

func TestBatchReadYourOwnWritesInWALMode(t *testing.T) {
	dir := t.TempDir()
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog

	db, err := Open(filepath.Join(dir, "ryow_wal.db"), opts)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	if err := db.PutString("", "counter", "1"); err != nil {
		t.Fatalf("PutString: %v", err)
	}

	err = db.Batch(func(b *BatchWriter) error {
		v, getErr := b.GetString("", "counter")
		if getErr != nil || v == nil || *v != "1" {
			t.Fatalf("base read: v=%v err=%v", v, getErr)
		}
		if putErr := b.PutString("", "counter", "2"); putErr != nil {
			return putErr
		}
		if v, _ = b.GetString("", "counter"); v == nil || *v != "2" {
			t.Fatalf("staged read: %v", v)
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch: %v", err)
	}

	if v, _ := db.GetString("", "counter"); v == nil || *v != "2" {
		t.Fatalf("committed counter: %v", v)
	}
}
