package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"testing"
)

// Fuzz-lite: deterministic mutation of real database files.
//
// Not a coverage-guided fuzzer. Fixed, reproducible mutations over the golden
// vectors plus one generated AOL file, asserting:
//  1. no panic and no hang - a mutated file is rejected cleanly or opened and
//     fully read;
//  2. structural damage is detected, not silently treated as an empty database.
//
// Mirrors the Rust suite (tests/fuzz_lite_test.rs).

type fuzzSeed struct {
	name  string
	bytes []byte
}

func fuzzSeeds(t *testing.T) []fuzzSeed {
	t.Helper()
	var out []fuzzSeed
	for _, name := range []string{
		"empty_v3.db", "single_put_v3.db", "multi_types_v3.db", "unicode_order_v3.db",
	} {
		data, err := os.ReadFile(filepath.Join("..", "..", "testdata", name))
		if err == nil {
			out = append(out, fuzzSeed{name, data})
		}
	}

	dir := t.TempDir()
	path := filepath.Join(dir, "generated.db")
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineAppendOnlyLog
	opts.SyncMode = SyncModeManual
	if db, err := Open(path, opts); err == nil {
		_ = db.PutString("ns", "alpha", "1")
		_ = db.PutString("ns", "beta", "22")
		_ = db.PutInt64("ns2", "gamma", -9223372036854775808)
		_ = db.Close()
		if data, err := os.ReadFile(path); err == nil {
			out = append(out, fuzzSeed{"generated_aol", data})
		}
	}
	return out
}

func fuzzErrorAccepted(err error) bool {
	dbErr, ok := err.(*DbError)
	if !ok {
		return false
	}
	switch dbErr.Type {
	case "CorruptedError", "UnsupportedVersionError", "InvalidUtf8Error",
		"InvalidNamespaceError", "KeyTooLargeError", "IoError":
		return true
	}
	return false
}

// fuzzExercise returns ("opened"|"rejected", nil) or ("", err) when the handle
// misbehaved. Panics are recovered so one bad mutation reports its offset.
func fuzzExercise(dir string, tag int, data []byte) (outcome string, bad error) {
	defer func() {
		if r := recover(); r != nil {
			bad = fmt.Errorf("panic: %v", r)
		}
	}()

	path := filepath.Join(dir, fmt.Sprintf("case_%d.db", tag))
	if err := os.WriteFile(path, data, 0o644); err != nil {
		return "", fmt.Errorf("write: %w", err)
	}

	db, err := Open(path)
	if err != nil {
		if !fuzzErrorAccepted(err) {
			return "", fmt.Errorf("unexpected error kind: %v", err)
		}
		return "rejected", nil
	}
	defer db.Close()

	for _, ns := range db.Namespaces() {
		_, _ = db.Count(ns)
		if keys, kerr := db.Keys(ns); kerr == nil {
			for _, key := range keys {
				_, _ = db.GetBytes(ns, key)
			}
		}
		_, _ = db.ScanRange(ns, nil, nil)
	}
	db.Stats()
	return "opened", nil
}

func TestFuzzLiteMutatedFilesNeverPanic(t *testing.T) {
	dir := t.TempDir()
	cases, rejected := 0, 0
	tag := 0

	for _, seed := range fuzzSeeds(t) {
		original := seed.bytes
		n := len(original)

		check := func(data []byte, what string) {
			tag++
			cases++
			outcome, bad := fuzzExercise(dir, tag, data)
			if bad != nil {
				t.Fatalf("%s %s: %v", seed.name, what, bad)
			}
			if outcome == "rejected" {
				rejected++
			}
		}

		cuts := []int{0, 1, 15, 31, 32, 33}
		if n > 1 {
			cuts = append(cuts, n-1)
		}
		for _, cut := range cuts {
			if cut > n {
				continue
			}
			check(original[:cut], fmt.Sprintf("truncated to %d bytes", cut))
		}

		for offset := 0; offset < n; offset++ {
			for _, mask := range []byte{0xFF, 0x01, 0x80} {
				mutated := append([]byte(nil), original...)
				mutated[offset] ^= mask
				check(mutated, fmt.Sprintf("flip at %d mask %#x", offset, mask))
			}
		}

		for _, extra := range [][]byte{{0x00}, {0xFF}, {0x00, 0x01, 0x02, 0x03}} {
			mutated := append(append([]byte(nil), original...), extra...)
			check(mutated, fmt.Sprintf("append %d bytes", len(extra)))
		}

		// Structural damage must be detected.
		short := original
		if len(short) > 31 {
			short = short[:31]
		}
		tag++
		cases++
		p := filepath.Join(dir, fmt.Sprintf("case_%d.db", tag))
		if err := os.WriteFile(p, short, 0o644); err != nil {
			t.Fatal(err)
		}
		if db, err := Open(p); err == nil {
			db.Close()
			t.Fatalf("%s truncated below the header was accepted", seed.name)
		}
		if n > 0 {
			magic := append([]byte(nil), original...)
			magic[0] ^= 0xFF
			tag++
			cases++
			p = filepath.Join(dir, fmt.Sprintf("case_%d.db", tag))
			if err := os.WriteFile(p, magic, 0o644); err != nil {
				t.Fatal(err)
			}
			if db, err := Open(p); err == nil {
				db.Close()
				t.Fatalf("%s with corrupted magic was accepted", seed.name)
			}
		}
	}

	if cases <= 1000 {
		t.Fatalf("mutation set unexpectedly small: %d", cases)
	}
	if rejected <= cases/10 {
		t.Fatalf("only %d of %d mutations were rejected - corruption detection looks broken",
			rejected, cases)
	}
}
