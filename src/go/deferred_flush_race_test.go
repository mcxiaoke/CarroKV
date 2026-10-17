package ckv

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

// FIX-01 regression: the background flush and the explicit Flush paths must not
// write the shared `<db>.tmp` concurrently, and a background snapshot that went
// stale must never be rotated over newer data.
//
// Both variants surface as the same observable failure: a confirmed write that is
// gone after Close() + reopen. A 15MB value widens the serialisation window, and
// deleting it before Flush makes the explicit snapshot tiny - so the stale
// background snapshot is guaranteed to land *after* the flushed one instead of
// relying on timing luck. Mirrors the Rust/.NET suites (Dart is single-isolate
// and its flush is synchronous, so it cannot interleave).

func raceOptions(intervalMs uint32) DbOptions {
	o := DefaultDbOptions()
	o.EngineMode = StorageEngineSnapshotAtomic
	o.SyncMode = SyncModeDeferred
	o.SyncIntervalMs = intervalMs
	return o
}

func TestExplicitFlushDuringBackgroundFlushKeepsBothGenerations(t *testing.T) {
	big := strings.Repeat("x", 15*1024*1024)

	for round := 0; round < 5; round++ {
		path := t.TempDir() + "/race.db"
		db, err := Open(path, raceOptions(1))
		if err != nil {
			t.Fatalf("round %d: open: %v", round, err)
		}

		if err := db.PutString("", "big", big); err != nil {
			t.Fatalf("round %d: put big: %v", round, err)
		}
		// Let the worker clone the snapshot and start serialising it.
		time.Sleep(5 * time.Millisecond)

		if _, err := db.Delete("", "big"); err != nil {
			t.Fatalf("round %d: delete big: %v", round, err)
		}
		if err := db.PutString("", "w", "after"); err != nil {
			t.Fatalf("round %d: put w: %v", round, err)
		}
		if err := db.Flush(); err != nil {
			t.Fatalf("round %d: flush: %v", round, err)
		}

		// Give a stale worker all the time it needs to rotate.
		time.Sleep(150 * time.Millisecond)
		if err := db.Close(); err != nil {
			t.Fatalf("round %d: close: %v", round, err)
		}

		reopened, err := Open(path)
		if err != nil {
			t.Fatalf("round %d: reopen: %v", round, err)
		}
		got, err := reopened.GetString("", "w")
		if err != nil {
			reopened.Close()
			t.Fatalf("round %d: get w: %v", round, err)
		}
		if got == nil || *got != "after" {
			reopened.Close()
			t.Fatalf("round %d: an explicitly flushed write was overwritten by a stale background snapshot (got %v)",
				round, got)
		}
		if _, err := reopened.GetString("", "big"); err != nil {
			reopened.Close()
			t.Fatalf("round %d: get big: %v", round, err)
		}
		reopened.Close()
	}
}

func TestRapidWritesWithBackgroundFlushAreNotSilentlyDropped(t *testing.T) {
	for round := 0; round < 3; round++ {
		path := t.TempDir() + "/rapid.db"
		db, err := Open(path, raceOptions(1))
		if err != nil {
			t.Fatalf("round %d: open: %v", round, err)
		}
		for i := 0; i < 200; i++ {
			if err := db.PutString("ns", fmt.Sprintf("k%04d", i), "value"); err != nil {
				db.Close()
				t.Fatalf("round %d: put %d: %v", round, i, err)
			}
		}
		if err := db.Close(); err != nil {
			t.Fatalf("round %d: close: %v", round, err)
		}

		reopened, err := Open(path)
		if err != nil {
			t.Fatalf("round %d: reopen: %v", round, err)
		}
		count, err := reopened.Count("ns")
		if err != nil {
			reopened.Close()
			t.Fatalf("round %d: count: %v", round, err)
		}
		if count != 200 {
			reopened.Close()
			t.Fatalf("round %d: lost writes after Close(): %d of 200", round, count)
		}
		reopened.Close()
	}
}
