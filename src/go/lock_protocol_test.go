package ckv

// Regression tests for the connection lock (C9, threshold T10).
//
// Since C9 the lock is a kernel lock on `<db>.lock` — flock(2) on Unix, an
// exclusive/shared share-mode open on Windows — acquired by Open and held for the
// whole lifetime of the connection:
//
//   - the lock file's *contents* are irrelevant: the old 32-byte
//     `PID | UUID | CreateTimeMs` record, its clock-based staleness judgement and the
//     whole "steal the stale lock" dance are gone, so a leftover or malformed lock
//     file must cost nothing at all;
//   - a live holder can never be mistaken for a dead one (which used to put two
//     writers on one database);
//   - a crashed holder releases the lock by dying, because the kernel owns it.
//
// Writable connections take the lock exclusively, read-only ones share it: readers
// exclude writers but not each other (bbolt's `flock(db, !readOnly)`).
//
// The cross-process half of T10 (a holder in another process that is then killed) is
// exercised by the Rust suite; here the same property is checked through the lock
// primitive itself, which is platform-specific code, plus Open's refusal path.

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func shortLockOptions() DbOptions {
	opts := DefaultDbOptions()
	opts.LockTimeoutMs = 150
	return opts
}

// The lock file's contents no longer take part in the protocol: a malformed,
// ancient-looking lock file must be ignored -- not waited on, not stolen, not deleted.
func TestLockFileContentsAreIrrelevant(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "garbage_lock.db")
	lockPath := dbPath + ".lock"

	garbage := []byte("not a 32-byte lock record: no pid, no uuid, no timestamp")
	if err := os.WriteFile(lockPath, garbage, 0o600); err != nil {
		t.Fatal(err)
	}

	started := time.Now()
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("a leftover lock file must not block a free lock: %v", err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("write: %v", err)
	}
	if elapsed := time.Since(started); elapsed > 2*time.Second {
		t.Fatalf("the open took %v: the lock file's contents are still being interpreted", elapsed)
	}

	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	// The marker file is never deleted (unlinking it would let a second writer lock a
	// different inode while the first still holds the old one) and never rewritten.
	if _, err := os.Stat(lockPath); err != nil {
		t.Fatalf("the lock file must not be unlinked: %v", err)
	}
	after, err := os.ReadFile(lockPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(after) != string(garbage) {
		t.Fatalf("the lock file must not be rewritten, got %q", string(after))
	}
}

// A writable connection holds the lock exclusively: neither a second writer nor a
// reader can open the database while it lives; once it is closed the lock is free.
func TestExclusiveLockExcludesWritersAndReaders(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "exclusive.db")

	holder, err := openLockHandle(dbPath+".lock", true)
	if err != nil {
		t.Fatalf("holder must be able to lock: %v", err)
	}

	if _, err := Open(dbPath, shortLockOptions()); !IsLocked(err) {
		_ = closeLockHandle(holder)
		t.Fatalf("a second writer must be rejected, got %v", err)
	}

	readerOpts := shortLockOptions()
	readerOpts.ReadOnly = true
	if _, err := Open(dbPath, readerOpts); !IsLocked(err) {
		_ = closeLockHandle(holder)
		t.Fatalf("a reader must be excluded by a writer, got %v", err)
	}

	_ = closeLockHandle(holder)

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("the lock must be free after the holder is gone: %v", err)
	}
	defer db.Close()
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("write after the holder is gone: %v", err)
	}
}

// A read-only connection shares the lock: another reader may open the database, a
// writer may not.
func TestSharedLockAllowsReadersButNotWriters(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "shared.db")

	{
		db, err := Open(dbPath)
		if err != nil {
			t.Fatal(err)
		}
		if err := db.PutString("", "k", "v"); err != nil {
			t.Fatal(err)
		}
		if err := db.Close(); err != nil {
			t.Fatal(err)
		}
	}

	holder, err := openLockHandle(dbPath+".lock", false) // shared
	if err != nil {
		t.Fatalf("holder must be able to take a shared lock: %v", err)
	}
	defer func() { _ = closeLockHandle(holder) }()

	readerOpts := DefaultDbOptions()
	readerOpts.ReadOnly = true
	reader, err := Open(dbPath, readerOpts)
	if err != nil {
		t.Fatalf("a reader must coexist with another reader: %v", err)
	}
	if err := reader.Close(); err != nil {
		t.Fatal(err)
	}

	if _, err := Open(dbPath, shortLockOptions()); !IsLocked(err) {
		t.Fatalf("a writer must be excluded by a reader, got %v", err)
	}

	_ = closeLockHandle(holder)
	holder = nil

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("open after the holders are gone: %v", err)
	}
	defer db.Close()
	if v, _ := db.GetString("", "k"); v == nil || *v != "v" {
		t.Fatalf("the seeded value must survive, got %v", v)
	}
}

// A connection keeps the lock for its whole lifetime: the lock is still held while
// the instance is open, and released by Close (so another process can then take it).
func TestConnectionHoldsTheLockUntilClose(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "lifetime.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatal(err)
	}

	// While the instance is alive, a competing lock attempt cannot succeed.
	if probe, err := openLockHandle(dbPath+".lock", true); err == nil {
		_ = closeLockHandle(probe)
		t.Fatal("the connection must hold the lock for its whole lifetime")
	}

	if err := db.Close(); err != nil {
		t.Fatal(err)
	}

	// After Close the lock is free again -- without any lock file being deleted.
	probe, err := openLockHandle(dbPath+".lock", true)
	if err != nil {
		t.Fatalf("Close must release the connection lock: %v", err)
	}
	_ = closeLockHandle(probe)
}
