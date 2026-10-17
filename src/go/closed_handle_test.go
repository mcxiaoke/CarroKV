package ckv

import (
	"os"
	"path/filepath"
	"testing"
)

// FIX-22 regression: a handle kept past Close() must not write.
//
// Close() releases the inter-process connection lock (C9), so a write that still
// succeeded afterwards would land in a database that another process may already
// hold, i.e. exactly the two-writer situation the lock exists to prevent. Rust
// (.closed), .NET (_isDisposed) and Kotlin (isDisposed) already refuse; Go and
// Dart silently accepted the write before this fix.
func TestWriteAfterCloseIsRefused(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "closed.db")

	db, err := Open(path)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "before", "1"); err != nil {
		t.Fatalf("put before close: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	before, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}

	if err := db.PutString("", "after", "2"); err == nil {
		t.Fatal("PutString after Close() must fail, got nil")
	} else if dbErr, ok := err.(*DbError); !ok || dbErr.Type != "LockedError" {
		t.Fatalf("PutString after Close() must return LockedError, got %T: %v", err, err)
	}
	if _, err := db.Delete("", "before"); err == nil {
		t.Fatal("Delete after Close() must fail, got nil")
	}
	if err := db.Flush(); err == nil {
		t.Fatal("Flush after Close() must fail, got nil")
	}
	if err := db.Compact(); err == nil {
		t.Fatal("Compact after Close() must fail, got nil")
	}

	after, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat after failed writes: %v", err)
	}
	if after.Size() != before.Size() || !after.ModTime().Equal(before.ModTime()) {
		t.Fatalf("file was modified by refused writes: %d/%v -> %d/%v",
			before.Size(), before.ModTime(), after.Size(), after.ModTime())
	}

	// Close is idempotent.
	if err := db.Close(); err != nil {
		t.Fatalf("second Close: %v", err)
	}

	// A fresh open still sees the pre-close state.
	reopened, err := Open(path)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer reopened.Close()
	if got, _ := reopened.GetString("", "after"); got != nil {
		t.Fatalf("a refused write reached the file: %q", *got)
	}
}
