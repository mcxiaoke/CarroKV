package ckv

import (
	"math"
	"path/filepath"
	"testing"
)

func TestGoCommonDataTypesRoundtrip(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "types.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 1. int64 Boundary tests
	i64Tests := []struct {
		key string
		val int64
	}{
		{"i64:zero", 0},
		{"i64:pos", 1234567890123456789},
		{"i64:neg", -987654321098765432},
		{"i64:min", math.MinInt64},
		{"i64:max", math.MaxInt64},
	}
	for _, tc := range i64Tests {
		if err := db.PutInt64("", tc.key, tc.val); err != nil {
			t.Fatalf("PutInt64 failed for %s: %v", tc.key, err)
		}
		got, err := db.GetInt64("", tc.key)
		if err != nil {
			t.Fatalf("GetInt64 failed for %s: %v", tc.key, err)
		}
		if got == nil || *got != tc.val {
			t.Fatalf("GetInt64 mismatch for %s: got %v, want %v", tc.key, got, tc.val)
		}
	}

	// 2. float64 Floating point and canonical NaN
	f64Tests := []struct {
		key string
		val float64
	}{
		{"f64:zero", 0.0},
		{"f64:neg_zero", math.Copysign(0.0, -1.0)},
		{"f64:pi", 3.141592653589793},
		{"f64:inf", math.Inf(1)},
		{"f64:neg_inf", math.Inf(-1)},
		{"f64:nan", math.NaN()},
	}
	for _, tc := range f64Tests {
		if err := db.PutFloat64("", tc.key, tc.val); err != nil {
			t.Fatalf("PutFloat64 failed for %s: %v", tc.key, err)
		}
		got, err := db.GetFloat64("", tc.key)
		if err != nil {
			t.Fatalf("GetFloat64 failed for %s: %v", tc.key, err)
		}
		if got == nil {
			t.Fatalf("GetFloat64 returned nil for %s", tc.key)
		}
		if math.IsNaN(tc.val) {
			if !math.IsNaN(*got) {
				t.Fatalf("Expected NaN for %s, got %v", tc.key, *got)
			}
			bits := math.Float64bits(*got)
			if bits != 0x7FF8000000000000 {
				t.Fatalf("Expected canonical NaN bits 0x7FF8000000000000, got 0x%X", bits)
			}
		} else {
			if math.Float64bits(*got) != math.Float64bits(tc.val) {
				t.Fatalf("Bit mismatch for %s: got %v (0x%X), want %v (0x%X)",
					tc.key, *got, math.Float64bits(*got), tc.val, math.Float64bits(tc.val))
			}
		}
	}

	// 3. bool
	if err := db.PutBool("", "bool:t", true); err != nil {
		t.Fatalf("PutBool failed: %v", err)
	}
	if err := db.PutBool("", "bool:f", false); err != nil {
		t.Fatalf("PutBool failed: %v", err)
	}
	bt, err := db.GetBool("", "bool:t")
	if err != nil || bt == nil || *bt != true {
		t.Fatalf("GetBool bool:t failed: got %v, err %v", bt, err)
	}
	bf, err := db.GetBool("", "bool:f")
	if err != nil || bf == nil || *bf != false {
		t.Fatalf("GetBool bool:f failed: got %v, err %v", bf, err)
	}

	// 4. Batch mixed types
	err = db.Batch(func(b *BatchWriter) error {
		if err := b.PutInt64("metrics", "count", 42); err != nil {
			return err
		}
		if err := b.PutFloat64("metrics", "ratio", 0.99); err != nil {
			return err
		}
		if err := b.PutBool("metrics", "active", true); err != nil {
			return err
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch failed: %v", err)
	}

	cVal, err := db.GetInt64("metrics", "count")
	if err != nil || cVal == nil || *cVal != 42 {
		t.Fatalf("GetInt64 metrics:count failed: got %v, err %v", cVal, err)
	}
	rVal, err := db.GetFloat64("metrics", "ratio")
	if err != nil || rVal == nil || *rVal != 0.99 {
		t.Fatalf("GetFloat64 metrics:ratio failed: got %v, err %v", rVal, err)
	}
	aVal, err := db.GetBool("metrics", "active")
	if err != nil || aVal == nil || *aVal != true {
		t.Fatalf("GetBool metrics:active failed: got %v, err %v", aVal, err)
	}

	// 5. Entry typed accessor verification
	entries, err := db.ScanRange("metrics", nil, nil)
	if err != nil {
		t.Fatalf("ScanRange failed: %v", err)
	}
	if len(entries) != 3 {
		t.Fatalf("Expected 3 entries, got %d", len(entries))
	}
	for _, e := range entries {
		switch e.Key {
		case "count":
			if !e.IsInt64() {
				t.Fatalf("count is not int64")
			}
			v, err := e.AsInt64()
			if err != nil || v != 42 {
				t.Fatalf("count AsInt64 failed: got %v, err %v", v, err)
			}
		case "ratio":
			if !e.IsFloat64() {
				t.Fatalf("ratio is not float64")
			}
			v, err := e.AsFloat64()
			if err != nil || v != 0.99 {
				t.Fatalf("ratio AsFloat64 failed: got %v, err %v", v, err)
			}
		case "active":
			if !e.IsBool() {
				t.Fatalf("active is not bool")
			}
			v, err := e.AsBool()
			if err != nil || v != true {
				t.Fatalf("active AsBool failed: got %v, err %v", v, err)
			}
		default:
			t.Fatalf("Unexpected key: %s", e.Key)
		}
	}

	// 6. Type mismatch error checks
	if _, err := db.GetInt64("metrics", "ratio"); err == nil {
		t.Fatalf("Expected type mismatch error when reading float64 as int64")
	}
	if _, err := db.GetBool("metrics", "count"); err == nil {
		t.Fatalf("Expected type mismatch error when reading int64 as bool")
	}
	if _, err := db.GetFloat64("metrics", "active"); err == nil {
		t.Fatalf("Expected type mismatch error when reading bool as float64")
	}
}
