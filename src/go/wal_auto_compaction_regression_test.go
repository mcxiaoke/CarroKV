package ckv

// Regression tests for the auto-compaction write path, log-damage classification
// and the `.bak` recovery contract.
//
// They mirror the Rust suite (src/rust/tests/wal_auto_compaction_regression_test.go
// area) so that both languages are locked to the same behaviour. The paths they
// cover previously had zero coverage: every existing WAL test used the default
// 4 MiB compaction threshold (never reached) or called Compact() by hand.
//
// Covered defects:
//   - C1: compaction triggered from inside appendEntries serialises a snapshot of
//     an in-memory state that does not yet contain the mutation being written, so
//     that write is appended and then immediately overwritten.
//   - C2: compaction performed while the caller still holds the `.db` lock, so it
//     can never succeed and every crossing write blocks for the lock timeout.
//   - C4: damage in the middle of the log was treated as a torn tail and silently
//     truncated, discarding every intact record after it.
//
// Every assertion checks the whole key set after a reopen, and damage is injected
// by flipping bytes in a real log file.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const (
	regressionTinyThreshold = 512
	// threshold that no small test file will ever reach
	regressionNoCompaction = ^uint64(0) - 1
)

func regressionWalOptions(threshold uint64) DbOptions {
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeImmediate
	opts.CompactionThresholdBytes = threshold
	return opts
}

func flipByteAt(t *testing.T, path string, offset int64) {
	t.Helper()

	f, err := os.OpenFile(path, os.O_RDWR, 0o666)
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer f.Close()

	buf := make([]byte, 1)
	if _, err := f.ReadAt(buf, offset); err != nil {
		t.Fatalf("read at %d: %v", offset, err)
	}
	buf[0] ^= 0xFF
	if _, err := f.WriteAt(buf, offset); err != nil {
		t.Fatalf("write at %d: %v", offset, err)
	}
	if err := f.Sync(); err != nil {
		t.Fatalf("sync: %v", err)
	}
}

// probeFrameLen returns the byte length of the frame written by a single
// PutString, derived empirically so the tests do not depend on the frame layout.
func probeFrameLen(t *testing.T, dir string) int64 {
	t.Helper()

	probe := filepath.Join(dir, "probe_frame_len.db")
	db, err := Open(probe, regressionWalOptions(regressionNoCompaction))
	if err != nil {
		t.Fatalf("open probe: %v", err)
	}
	if err := db.PutString("", "a", "1"); err != nil {
		t.Fatalf("probe put: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("probe close: %v", err)
	}

	fi, err := os.Stat(probe)
	if err != nil {
		t.Fatalf("stat probe: %v", err)
	}
	if fi.Size() <= int64(FileHeaderLen) {
		t.Fatalf("probe database must contain one frame, got %d bytes", fi.Size())
	}
	return fi.Size() - int64(FileHeaderLen)
}

// C1: the write that crosses the compaction threshold must survive a reopen,
// together with every other write.
func TestAutoCompactionMustNotLoseWrites(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t1.db")
	opts := regressionWalOptions(regressionTinyThreshold)

	const keys = 300

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for i := 0; i < keys; i++ {
		key := fmt.Sprintf("key%04d", i)
		if err := db.PutString("", key, "value"+key); err != nil {
			t.Fatalf("put %s: %v", key, err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	// Guard against a vacuous pass: rotation during compaction produces `.bak`.
	if _, err := os.Stat(dbPath + ".bak"); err != nil {
		t.Fatalf("auto compaction never ran (threshold %d): %v", regressionTinyThreshold, err)
	}

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer db.Close()

	var broken []string
	for i := 0; i < keys; i++ {
		key := fmt.Sprintf("key%04d", i)
		want := "value" + key
		got, err := db.GetString("", key)
		switch {
		case err != nil:
			broken = append(broken, fmt.Sprintf("%s: error %v", key, err))
		case got == nil || *got != want:
			broken = append(broken, fmt.Sprintf("%s: want %q, got %v", key, want, got))
		}
	}
	if len(broken) > 0 {
		shown := broken
		if len(shown) > 5 {
			shown = shown[:5]
		}
		t.Fatalf("%d of %d keys did not survive auto compaction: %v", len(broken), keys, shown)
	}
}

// C2: threshold-triggered compaction must actually complete, reclaim space, and
// must not stall each write for a lock timeout.
func TestAutoCompactionMustRunWithoutLockStalls(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t2.db")
	opts := regressionWalOptions(regressionTinyThreshold)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}

	value := strings.Repeat("x", 64)
	var peak int64
	var worst time.Duration

	for i := 0; i < 200; i++ {
		start := time.Now()
		if err := db.PutString("", "hot", value); err != nil {
			t.Fatalf("put %d: %v", i, err)
		}
		if elapsed := time.Since(start); elapsed > worst {
			worst = elapsed
		}
		if fi, statErr := os.Stat(dbPath); statErr == nil && fi.Size() > peak {
			peak = fi.Size()
		}
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	// 200 writes of a 64-byte value append far more than 8x the threshold.
	if peak > 8*regressionTinyThreshold {
		t.Fatalf("file grew to %d bytes: compaction never reclaimed space (threshold %d)",
			peak, regressionTinyThreshold)
	}
	if _, err := os.Stat(dbPath + ".bak"); err != nil {
		t.Fatalf("compaction never produced a `.bak` rotation: %v", err)
	}
	if _, err := os.Stat(dbPath + ".tmp"); err == nil {
		t.Fatal("compaction left a `.tmp` file behind")
	}
	// A nested lock acquisition blocks for LockTimeoutMs (10 s by default).
	if worst > 2*time.Second {
		t.Fatalf("a single write took %v: compaction is blocking the write path", worst)
	}
}

// C4: damage to a record that is followed by intact records cannot be a torn
// write on an append-only file, so opening must report corruption.
func TestMidLogDamageMustBeReportedAsCorruption(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t4.db")
	opts := regressionWalOptions(regressionNoCompaction)
	frameLen := probeFrameLen(t, dir)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for _, kv := range [][2]string{{"a", "1"}, {"b", "2"}, {"c", "3"}} {
		if err := db.PutString("", kv[0], kv[1]); err != nil {
			t.Fatalf("put %s: %v", kv[0], err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	healthyLen := int64(FileHeaderLen) + 3*frameLen
	// Flip one byte inside the second frame; the third frame stays intact.
	flipByteAt(t, dbPath, int64(FileHeaderLen)+frameLen+8)

	if _, err := Open(dbPath, opts); err == nil {
		t.Fatal("damage followed by intact records must fail the open, not silently truncate the log")
	} else if !IsCorrupted(err) {
		t.Fatalf("expected CorruptedError, got %v", err)
	}

	fi, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	if fi.Size() != healthyLen {
		t.Fatalf("a refused open must leave the log untouched: want %d bytes, got %d",
			healthyLen, fi.Size())
	}
}

// C4 negative control: a genuine torn write (the file ends inside the last frame)
// must keep healing.
func TestTornTailMustStillHeal(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "t5.db")
	opts := regressionWalOptions(regressionNoCompaction)
	frameLen := probeFrameLen(t, dir)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	for _, kv := range [][2]string{{"a", "1"}, {"b", "2"}, {"c", "3"}} {
		if err := db.PutString("", kv[0], kv[1]); err != nil {
			t.Fatalf("put %s: %v", kv[0], err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	fi, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	if err := os.Truncate(dbPath, fi.Size()-frameLen/2); err != nil {
		t.Fatalf("truncate: %v", err)
	}

	db, err = Open(dbPath, opts)
	if err != nil {
		t.Fatalf("a torn tail must self-heal: %v", err)
	}
	defer db.Close()

	if got, _ := db.GetString("", "a"); got == nil || *got != "1" {
		t.Fatalf("key a must survive, got %v", got)
	}
	if got, _ := db.GetString("", "b"); got == nil || *got != "2" {
		t.Fatalf("key b must survive, got %v", got)
	}
	if got, _ := db.GetString("", "c"); got != nil {
		t.Fatalf("the torn frame must be discarded as a whole, got %v", *got)
	}

	healed, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	want := int64(FileHeaderLen) + 2*frameLen
	if healed.Size() != want {
		t.Fatalf("the log must be healed back to the last complete frame: want %d, got %d",
			want, healed.Size())
	}
}

// writeSecondGeneration writes two generations so that `.bak` holds the first
// one, then flips a byte inside the last frame of the primary file.
func writeSecondGeneration(t *testing.T, dbPath string, opts DbOptions) {
	t.Helper()

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

	fi, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	flipByteAt(t, dbPath, fi.Size()-2)
}

// D1: a corrupt primary file must be reported as corruption by default. Before
// D1 the engine silently replaced it with `.bak`, the generation from *before*
// the last rotation, discarding every write made since.
func TestCorruptPrimaryIsRejectedByDefault(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "d1.db")
	bakPath := dbPath + ".bak"

	recovered := false
	opts := DefaultDbOptions()
	opts.OnWarning = func(errType, _ string) {
		if errType == "onRecoveredFromBackup" {
			recovered = true
		}
	}

	writeSecondGeneration(t, dbPath, opts)
	if _, err := os.Stat(bakPath); err != nil {
		t.Fatalf("this test needs an existing previous generation: %v", err)
	}

	primaryInfo, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat primary: %v", err)
	}
	bakInfo, err := os.Stat(bakPath)
	if err != nil {
		t.Fatalf("stat backup: %v", err)
	}

	_, openErr := Open(dbPath, opts)
	if openErr == nil {
		t.Fatal("a corrupt primary file must be refused by default, not silently replaced")
	}
	if !IsCorrupted(openErr) {
		t.Fatalf("expected CorruptedError, got %v", openErr)
	}
	if !strings.Contains(openErr.Error(), bakPath) {
		t.Fatalf("the error must point the operator at the recovery copy, got: %v", openErr)
	}
	if recovered {
		t.Fatal("no automatic recovery may occur while AutoRecoverFromBackup is false")
	}

	// Both generations must be left byte-for-byte as found.
	if fi, statErr := os.Stat(dbPath); statErr != nil || fi.Size() != primaryInfo.Size() {
		t.Fatalf("primary file must be left untouched")
	}
	if fi, statErr := os.Stat(bakPath); statErr != nil || fi.Size() != bakInfo.Size() {
		t.Fatalf("`.bak` must be left untouched")
	}
}

// D1 escape hatch: with AutoRecoverFromBackup enabled the legacy behaviour is
// restored deliberately, including the `onRecoveredFromBackup` warning.
func TestCorruptPrimaryHealsWhenExplicitlyEnabled(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "d1_optin.db")

	recovered := false
	opts := DefaultDbOptions()
	opts.AutoRecoverFromBackup = true
	opts.OnWarning = func(errType, _ string) {
		if errType == "onRecoveredFromBackup" {
			recovered = true
		}
	}

	writeSecondGeneration(t, dbPath, opts)

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("opted-in recovery must succeed: %v", err)
	}
	defer db.Close()

	if !recovered {
		t.Fatal("the recovery warning must fire")
	}
	// The previous generation held "v1"; the damaged generation held "v2".
	got, err := db.GetString("", "version")
	if err != nil || got == nil || *got != "v1" {
		t.Fatalf("expected the previous generation's value v1, got %v (err %v)", got, err)
	}
}

// G1: the recovery source must never be tail-truncated while it is loaded,
// because truncating it destroys the data it is kept for.
//
// WAL mode is used deliberately: that is the mode in which the *live* log may be
// healed by truncation, so without the fix the recovery source inherits that
// behaviour and a torn `.bak` gets silently cut down and then promoted over the
// primary file.
func TestRecoverySourceIsNotTruncated(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "g1.db")
	bakPath := dbPath + ".bak"

	opts := regressionWalOptions(regressionNoCompaction)
	opts.AutoRecoverFromBackup = true

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	// Generation 1, then force a rotation so that `.bak` holds it.
	if err := db.PutString("", "version", "v1"); err != nil {
		t.Fatalf("put v1: %v", err)
	}
	if err := db.Compact(); err != nil {
		t.Fatalf("compact: %v", err)
	}
	// Generation 2 lands in the live log.
	if err := db.PutString("", "version", "v2"); err != nil {
		t.Fatalf("put v2: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	bakInfo, err := os.Stat(bakPath)
	if err != nil {
		t.Fatalf("compaction must have produced a `.bak`: %v", err)
	}
	// Tear the tail of the recovery source.
	tornLen := bakInfo.Size() - 4
	if err := os.Truncate(bakPath, tornLen); err != nil {
		t.Fatalf("truncate backup: %v", err)
	}
	// Corrupt the primary file so the recovery path is taken.
	primaryInfo, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat primary: %v", err)
	}
	flipByteAt(t, dbPath, primaryInfo.Size()-2)

	if _, err := Open(dbPath, opts); err == nil {
		t.Fatal("a torn recovery source cannot be used and must not be silently repaired")
	}

	if fi, statErr := os.Stat(bakPath); statErr != nil || fi.Size() != tornLen {
		t.Fatalf("`.bak` must not be truncated while being loaded (G1)")
	}
	if fi, statErr := os.Stat(dbPath); statErr != nil || fi.Size() != primaryInfo.Size() {
		t.Fatalf("the primary file must be left exactly as found")
	}
}

// D1-9: a read-only open of a corrupt database must fail without writing, and
// must ignore AutoRecoverFromBackup.
func TestReadOnlyNeverWritesOnCorruption(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "ro.db")
	bakPath := dbPath + ".bak"

	writeSecondGeneration(t, dbPath, DefaultDbOptions())

	primaryInfo, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("stat primary: %v", err)
	}
	bakInfo, err := os.Stat(bakPath)
	if err != nil {
		t.Fatalf("stat backup: %v", err)
	}

	roOpts := DefaultDbOptions()
	roOpts.ReadOnly = true
	if _, err := Open(dbPath, roOpts); err == nil {
		t.Fatal("read-only open of a corrupt database must fail instead of self-healing")
	}

	roOptsWithRecovery := roOpts
	roOptsWithRecovery.AutoRecoverFromBackup = true
	if _, err := Open(dbPath, roOptsWithRecovery); err == nil {
		t.Fatal("read-only mode must ignore AutoRecoverFromBackup")
	}

	if fi, statErr := os.Stat(dbPath); statErr != nil || fi.Size() != primaryInfo.Size() {
		t.Fatalf("read-only must not write to the primary file")
	}
	if fi, statErr := os.Stat(bakPath); statErr != nil || fi.Size() != bakInfo.Size() {
		t.Fatalf("read-only must not write to `.bak`")
	}
}

// TestT12RecoveryRejectsInvalidValueTypeOrLength verifies C12:
// corrupt entries (reserved value types or fixed-length types with wrong length)
// must be rejected during recovery rather than loaded into memory.
func TestT12RecoveryRejectsInvalidValueTypeOrLength(t *testing.T) {
	dir := t.TempDir()

	testCases := []struct {
		name  string
		vType uint8
		val   []byte
	}{
		{"reserved_vtype", 0x05, []byte("val")},
		{"bad_int64_len", ValTypeInt64, []byte{1, 2, 3, 4, 5, 6, 7}}, // 7 bytes != 8
		{"bad_bool_len", ValTypeBool, []byte{1, 0, 1}},               // 3 bytes != 1
	}

	for _, tc := range testCases {
		t.Run(tc.name, func(t *testing.T) {
			dbPath := filepath.Join(dir, tc.name+".db")
			f, err := os.Create(dbPath)
			if err != nil {
				t.Fatalf("create: %v", err)
			}
			hdr := CreateFileHeader()
			if _, err := f.Write(hdr[:]); err != nil {
				t.Fatalf("write hdr: %v", err)
			}
			entry := &RawOpEntry{
				OpCode:    OpPut,
				ValueType: tc.vType,
				Namespace: []byte("ns"),
				Key:       []byte("k"),
				Value:     tc.val,
			}
			if err := WriteBatchFrame(f, 1, []*RawOpEntry{entry}); err != nil {
				t.Fatalf("write frame: %v", err)
			}
			_ = f.Close()

			if _, err := Open(dbPath); err == nil {
				t.Fatalf("expected CorruptedError for %s, got nil", tc.name)
			} else if !IsCorrupted(err) {
				t.Fatalf("expected CorruptedError for %s, got %v", tc.name, err)
			}
		})
	}
}

// C17: 重复 open 选项冲突告警
func TestT17ReentrantOpenWarnsOnNonPermissionOptionsConflict(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "c17_conflict.db")

	opts1 := DefaultDbOptions()
	opts1.ReadOnly = false
	db1, err := Open(dbPath, opts1)
	if err != nil {
		t.Fatalf("open db1: %v", err)
	}
	defer db1.Close()

	if err := db1.PutString("", "k", "v"); err != nil {
		t.Fatalf("put string: %v", err)
	}

	// 第二次 Open 只改非权限类选项：告警 + 返回同一单例。
	var warnings []string
	opts2 := DefaultDbOptions()
	opts2.ReadOnly = false
	opts2.SyncMode = SyncModeManual
	opts2.OnWarning = func(code, msg string) {
		warnings = append(warnings, code+": "+msg)
	}

	db2, err := Open(dbPath, opts2)
	if err != nil {
		t.Fatalf("open db2: %v", err)
	}
	defer db2.Close()

	if db2 == db1 || db2.shared != db1.shared {
		t.Fatalf("expected separate handles sharing the same singleton instance")
	}

	if len(warnings) != 1 {
		t.Fatalf("expected 1 warning, got %d: %v", len(warnings), warnings)
	}
	if !strings.HasPrefix(warnings[0], "onOptionsConflict") {
		t.Fatalf("expected onOptionsConflict warning, got %v", warnings[0])
	}
	if !strings.Contains(warnings[0], "syncMode") {
		t.Fatalf("expected warning to mention syncMode, got %v", warnings[0])
	}

	// 修订（批次一 · 条目 11）：ReadOnly 冲突必须返回 ConflictingOptionsError，
	// 不得静默返回一个权限与所请求不一致的实例（静默提权）。
	roOpts := DefaultDbOptions()
	roOpts.ReadOnly = true
	if _, err := Open(dbPath, roOpts); !IsConflictingOptions(err) {
		t.Fatalf("expected ConflictingOptionsError for readOnly conflict, got %v", err)
	}
}
