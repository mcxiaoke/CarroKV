package ckv

import (
	"fmt"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Adaptive compaction: reclaim a WAL whose live payload has collapsed, even when
// the file never crosses CompactionThresholdBytes.
//
// Pinned properties:
//  1. dead-ratio trigger compacts an under-threshold log without losing live keys;
//  2. the 2 MB baseline keeps small logs from being rewritten for being dead;
//  3. CompactionThresholdBytes == 0 still disables every automatic path.

const adaptiveBaseline int64 = 2 * 1024 * 1024

func adaptiveOptions(threshold uint64) DbOptions {
	o := DefaultDbOptions()
	o.EngineMode = StorageEngineAppendOnlyLog
	o.SyncMode = SyncModeDeferred
	o.SyncIntervalMs = 3600000 // keep the background flush out of the way
	o.CompactionThresholdBytes = threshold
	return o
}

func fillStore(t *testing.T, db *DB, ns string, count, valueLen int) {
	t.Helper()
	value := strings.Repeat("v", valueLen)
	for i := 0; i < count; i++ {
		if err := db.PutString(ns, fmt.Sprintf("k%05d", i), value); err != nil {
			t.Fatalf("PutString(%d): %v", i, err)
		}
	}
}

func fileSizeOf(t *testing.T, path string) int64 {
	t.Helper()
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	return fi.Size()
}

func TestAdaptiveCompactionDeadRatioBelowSizeThreshold(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "adaptive.db")

	db, err := Open(path, adaptiveOptions(math.MaxUint64/2))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	fillStore(t, db, "ns", 300, 10*1024)
	grown := fileSizeOf(t, path)
	if grown <= adaptiveBaseline {
		t.Fatalf("log should exceed the adaptive baseline, got %d bytes", grown)
	}

	// The trigger fires as soon as the dead ratio crosses 50%, i.e. part-way
	// through this loop, so the file ends up near live size rather than at 3 MB.
	for i := 0; i < 250; i++ {
		deleted, delErr := db.Delete("ns", fmt.Sprintf("k%05d", i))
		if delErr != nil || !deleted {
			t.Fatalf("Delete(%d): deleted=%v err=%v", i, deleted, delErr)
		}
	}

	after := fileSizeOf(t, path)
	if after >= adaptiveBaseline {
		t.Fatalf("dead-ratio compaction should have reclaimed the log: %d -> %d bytes", grown, after)
	}

	if err := db.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	reopened, err := Open(path, adaptiveOptions(math.MaxUint64/2))
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer reopened.Close()

	if n, _ := reopened.Count("ns"); n != 50 {
		t.Fatalf("count after reopen: want 50, got %d", n)
	}
	for i := 250; i < 300; i++ {
		key := fmt.Sprintf("k%05d", i)
		v, getErr := reopened.GetString("ns", key)
		if getErr != nil || v == nil || len(*v) != 10*1024 {
			t.Fatalf("key %s lost by compaction (err=%v)", key, getErr)
		}
	}
	for i := 0; i < 250; i++ {
		if v, _ := reopened.GetString("ns", fmt.Sprintf("k%05d", i)); v != nil {
			t.Fatalf("deleted key k%05d came back", i)
		}
	}
}

func TestAdaptiveCompactionSkipsSmallLogs(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "small.db")

	db, err := Open(path, adaptiveOptions(math.MaxUint64/2))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	fillStore(t, db, "ns", 50, 1024)
	grown := fileSizeOf(t, path)
	if grown >= adaptiveBaseline {
		t.Fatalf("log must stay under the baseline, got %d bytes", grown)
	}

	for i := 0; i < 45; i++ {
		if _, delErr := db.Delete("ns", fmt.Sprintf("k%05d", i)); delErr != nil {
			t.Fatalf("Delete(%d): %v", i, delErr)
		}
	}

	after := fileSizeOf(t, path)
	if after < grown || after >= adaptiveBaseline {
		t.Fatalf("sub-baseline log must not be rewritten for being mostly dead: %d -> %d bytes", grown, after)
	}
	if n, _ := db.Count("ns"); n != 5 {
		t.Fatalf("count: want 5, got %d", n)
	}
}

func TestAdaptiveCompactionDisabledByZeroThreshold(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "disabled.db")

	db, err := Open(path, adaptiveOptions(0))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	fillStore(t, db, "ns", 300, 10*1024)
	grown := fileSizeOf(t, path)

	for i := 0; i < 270; i++ {
		if _, delErr := db.Delete("ns", fmt.Sprintf("k%05d", i)); delErr != nil {
			t.Fatalf("Delete(%d): %v", i, delErr)
		}
	}

	after := fileSizeOf(t, path)
	if after <= grown || after <= adaptiveBaseline {
		t.Fatalf("threshold 0 must disable automatic compaction entirely: %d -> %d bytes", grown, after)
	}
	if n, _ := db.Count("ns"); n != 30 {
		t.Fatalf("count: want 30, got %d", n)
	}
}
