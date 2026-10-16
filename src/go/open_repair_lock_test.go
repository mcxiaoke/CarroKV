package ckv

// Regression tests for the *open* path's remaining silent-failure modes, mirroring
// src/rust/tests/open_repair_lock_test.rs so both languages stay locked to the same
// behaviour.
//
// Covered defects:
//   - C10: tail healing during open used to run without the inter-process file
//     lock, so a second process could truncate frames another process had just
//     appended. The repair now holds the lock, which is proven here by making the
//     open *fail* (LockedError) while a live lock is held and by asserting that the
//     damaged file was left byte-for-byte untouched.
//   - D1-7 / D1-8: a missing primary next to a `.bak` used to be "repaired" by
//     promoting that (older) generation, and when the `.bak` itself was unreadable
//     the open silently succeeded as an *empty* database. Both are refused now
//     unless the caller explicitly opts into `.bak` recovery.

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const openPathFileHeaderLen = 32

// Fewer bytes than the 28-byte frame header: the file ends *inside* the last frame.
var openPathTornTail = []byte{0x4B, 0x56, 0x46, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22}

func openPathWalOptions() DbOptions {
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate
	opts.CompactionThresholdBytes = ^uint64(0) - 1
	return opts
}

// liveLockHolder stands in for "another process holds the lock": since C9 the lock is a
// kernel lock held for the lifetime of a connection, so the holder must take a *real*
// lock on `<db>.lock` instead of forging the old 32-byte record (which is now just an
// inert marker file).
type liveLockHolder struct {
	file *os.File
}

func newLiveLockHolder(t *testing.T, dbPath string) *liveLockHolder {
	t.Helper()
	f, err := openLockHandle(dbPath+".lock", true)
	if err != nil {
		t.Fatalf("the test itself must be able to take the lock: %v", err)
	}
	return &liveLockHolder{file: f}
}

func (h *liveLockHolder) release() {
	if h.file != nil {
		_ = closeLockHandle(h.file)
		h.file = nil
	}
}

func appendOpenPathTornTail(t *testing.T, path string) {
	t.Helper()

	f, err := os.OpenFile(path, os.O_WRONLY|os.O_APPEND, 0o666)
	if err != nil {
		t.Fatalf("open for append: %v", err)
	}
	defer f.Close()
	if _, err := f.Write(openPathTornTail); err != nil {
		t.Fatalf("append torn tail: %v", err)
	}
	if err := f.Sync(); err != nil {
		t.Fatalf("sync torn tail: %v", err)
	}
}

func fileLen(t *testing.T, path string) int64 {
	t.Helper()
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat %s: %v", path, err)
	}
	return fi.Size()
}

// writeTwoOpenPathGenerations writes two generations in Snapshot mode so that
// `.bak` holds the first one.
func writeTwoOpenPathGenerations(t *testing.T, dbPath string) DbOptions {
	t.Helper()

	opts := DefaultDbOptions()
	for _, value := range []string{"v1", "v2"} {
		db, err := Open(dbPath, opts)
		if err != nil {
			t.Fatalf("open: %v", err)
		}
		if err := db.PutString("", "version", value); err != nil {
			t.Fatalf("put: %v", err)
		}
		if err := db.Close(); err != nil {
			t.Fatalf("close: %v", err)
		}
	}
	if _, err := os.Stat(dbPath + ".bak"); err != nil {
		t.Fatalf("this scenario needs a `.bak` generation: %v", err)
	}
	return opts
}

// T11: repairing a torn tail at open must hold the inter-process lock.
//
// With a live lock held by "another process", the open must fail with LockedError
// and — crucially — must not have truncated the log. An unlocked repair would
// silently succeed here and discard whatever a concurrent appender had just
// written at the tail.
func TestT11TailRepairRequiresTheFileLock(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t11.db")
	opts := openPathWalOptions()

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for _, kv := range [][2]string{{"a", "1"}, {"b", "2"}, {"c", "3"}} {
		if err := db.PutString("", kv[0], kv[1]); err != nil {
			t.Fatalf("put: %v", err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	healthyLen := fileLen(t, dbPath)
	appendOpenPathTornTail(t, dbPath)
	damagedLen := fileLen(t, dbPath)
	if damagedLen != healthyLen+int64(len(openPathTornTail)) {
		t.Fatalf("expected damaged length %d, got %d", healthyLen+int64(len(openPathTornTail)), damagedLen)
	}

	holder := newLiveLockHolder(t, dbPath)
	defer holder.release()

	lockedOpts := openPathWalOptions()
	lockedOpts.LockTimeoutMs = 100
	_, openErr := Open(dbPath, lockedOpts)
	if openErr == nil {
		t.Fatal("tail repair must not proceed without the file lock (C10)")
	}
	if !IsLocked(openErr) {
		t.Fatalf("expected LockedError while another process holds the lock, got %v", openErr)
	}
	if got := fileLen(t, dbPath); got != damagedLen {
		t.Fatalf("refused open must not have truncated the log: %d != %d", got, damagedLen)
	}

	// Once the holder is gone the same open heals the tail as before.
	holder.release()

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("a torn tail must still self-heal: %v", err)
	}
	defer db.Close()
	for _, kv := range [][2]string{{"a", "1"}, {"b", "2"}, {"c", "3"}} {
		got, err := db.GetString("", kv[0])
		if err != nil || got == nil || *got != kv[1] {
			t.Fatalf("key %s: got %v (err %v), want %s", kv[0], got, err, kv[1])
		}
	}
	if got := fileLen(t, dbPath); got != healthyLen {
		t.Fatalf("healed log must end at the last complete frame: %d != %d", got, healthyLen)
	}
}

// A missing primary next to a *healthy* `.bak` is not a fresh database, so the
// default open refuses it, leaves both files untouched, and names the copy an
// operator can restore from.
func TestMissingPrimaryWithHealthyBackupIsRefusedByDefault(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "d17.db")
	opts := writeTwoOpenPathGenerations(t, dbPath)

	bakPath := dbPath + ".bak"
	bakBefore, err := os.ReadFile(bakPath)
	if err != nil {
		t.Fatalf("read bak: %v", err)
	}
	if err := os.Remove(dbPath); err != nil {
		t.Fatalf("remove primary: %v", err)
	}

	_, openErr := Open(dbPath, opts)
	if openErr == nil {
		t.Fatal("a missing primary with a `.bak` must not be silently promoted (D1-7)")
	}
	if !IsCorrupted(openErr) {
		t.Fatalf("expected CorruptedError, got %v", openErr)
	}
	if !strings.Contains(openErr.Error(), ".bak") {
		t.Fatalf("error must point at the offline copy, got: %v", openErr)
	}
	if _, statErr := os.Stat(dbPath); statErr == nil {
		t.Fatal("a refused open must not create a database")
	}
	bakAfter, err := os.ReadFile(bakPath)
	if err != nil {
		t.Fatalf("read bak after: %v", err)
	}
	if string(bakBefore) != string(bakAfter) {
		t.Fatal("the offline copy must be left untouched")
	}
}

// The explicit opt-in restores the legacy promotion, with the existing warning
// code (onRecoveredFromBackup).
func TestMissingPrimaryWithHealthyBackupIsPromotedWhenExplicitlyEnabled(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "d17_opt_in.db")
	opts := writeTwoOpenPathGenerations(t, dbPath)

	var warnings []string
	opts.AutoRecoverFromBackup = true
	opts.OnWarning = func(code, _ string) { warnings = append(warnings, code) }

	if err := os.Remove(dbPath); err != nil {
		t.Fatalf("remove primary: %v", err)
	}

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("explicit opt-in must recover: %v", err)
	}
	defer db.Close()
	if _, statErr := os.Stat(dbPath); statErr != nil {
		t.Fatal("the `.bak` must have been promoted")
	}
	got, err := db.GetString("", "version")
	if err != nil || got == nil || *got != "v1" {
		t.Fatalf("expected v1 from the promoted generation, got %v (err %v)", got, err)
	}
	found := false
	for _, code := range warnings {
		if code == "onRecoveredFromBackup" {
			found = true
		}
	}
	if !found {
		t.Fatalf("promotion must keep reporting onRecoveredFromBackup, got %v", warnings)
	}
}

// D1-8: a missing primary with an *unreadable* `.bak` used to open as an empty
// database, hiding the loss of both generations. It must now be refused.
func TestMissingPrimaryWithDamagedBackupIsNotOpenedAsAnEmptyDatabase(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "d18.db")
	opts := writeTwoOpenPathGenerations(t, dbPath)
	bakPath := dbPath + ".bak"

	flipByteAt(t, bakPath, openPathFileHeaderLen+12)
	if err := os.Remove(dbPath); err != nil {
		t.Fatalf("remove primary: %v", err)
	}

	_, openErr := Open(dbPath, opts)
	if openErr == nil {
		t.Fatal("an unreadable `.bak` next to a missing primary must be refused (D1-8)")
	}
	if !IsCorrupted(openErr) {
		t.Fatalf("expected CorruptedError, got %v", openErr)
	}
	if !strings.Contains(openErr.Error(), "could not be loaded") {
		t.Fatalf("error must explain that the offline copy is unusable, got: %v", openErr)
	}
	if _, statErr := os.Stat(dbPath); statErr == nil {
		t.Fatal("a refused open must not leave a fresh empty database behind")
	}
}

// Negative control: with no `.bak` at all the same path is a genuinely new
// database and must keep opening as one.
func TestMissingPrimaryWithoutBackupStillOpensAsANewDatabase(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "fresh.db")
	if _, err := os.Stat(dbPath + ".bak"); err == nil {
		t.Fatal("precondition: no `.bak` expected")
	}

	db, err := Open(dbPath, DefaultDbOptions())
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer db.Close()
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}
	got, err := db.GetString("", "k")
	if err != nil || got == nil || *got != "v" {
		t.Fatalf("expected v, got %v (err %v)", got, err)
	}
}
