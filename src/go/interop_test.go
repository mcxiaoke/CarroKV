package ckv

import (
	"bytes"
	"math"
	"os"
	"path/filepath"
	"testing"
)

func TestGoReadsRustDataAndWritesBack(t *testing.T) {
	testDir := filepath.Join("..", "..", "temp", "interop")
	_ = os.MkdirAll(testDir, 0755)

	rustDbPath := filepath.Join(testDir, "rust_data.db")
	if _, err := os.Stat(rustDbPath); os.IsNotExist(err) {
		t.Skip("rust_data.db not found, skipping until rust runs")
	}

	db, err := Open(rustDbPath)
	if err != nil {
		t.Fatalf("Go failed to open rust_data.db: %v", err)
	}

	// 1. Verify data written by Rust
	val, err := db.GetString("", "hello")
	if err != nil || val == nil || *val != "world from rust" {
		t.Fatalf("Unexpected value for 'hello': %v, err: %v", val, err)
	}

	binVal, err := db.GetBytes("bin_ns", "binary_key")
	if err != nil || !bytes.Equal(binVal, []byte{0x01, 0x02, 0x03, 0xFF}) {
		t.Fatalf("Unexpected binary value: %v", binVal)
	}

	emojiVal, err := db.GetString("", "😀")
	if err != nil || emojiVal == nil || *emojiVal != "happy" {
		t.Fatalf("Unexpected emoji value: %v", emojiVal)
	}

	// 2. Verify types written by Rust
	iMin, err := db.GetInt64("types", "int_min")
	if err != nil || iMin == nil || *iMin != math.MinInt64 {
		t.Fatalf("Unexpected int_min: %v, err: %v", iMin, err)
	}
	iMax, err := db.GetInt64("types", "int_max")
	if err != nil || iMax == nil || *iMax != math.MaxInt64 {
		t.Fatalf("Unexpected int_max: %v, err: %v", iMax, err)
	}
	fPi, err := db.GetFloat64("types", "float_pi")
	if err != nil || fPi == nil || *fPi != 3.141592653589793 {
		t.Fatalf("Unexpected float_pi: %v, err: %v", fPi, err)
	}
	fNan, err := db.GetFloat64("types", "float_nan")
	if err != nil || fNan == nil || !math.IsNaN(*fNan) {
		t.Fatalf("Unexpected float_nan: %v, err: %v", fNan, err)
	}
	nanBits := math.Float64bits(*fNan)
	if nanBits != 0x7FF8000000000000 {
		t.Fatalf("Expected canonical quiet NaN 0x7FF8000000000000, got 0x%X", nanBits)
	}
	bTrue, err := db.GetBool("types", "bool_true")
	if err != nil || bTrue == nil || *bTrue != true {
		t.Fatalf("Unexpected bool_true: %v, err: %v", bTrue, err)
	}
	bFalse, err := db.GetBool("types", "bool_false")
	if err != nil || bFalse == nil || *bFalse != false {
		t.Fatalf("Unexpected bool_false: %v, err: %v", bFalse, err)
	}

	// 3. Write new data from Go into a separate file for Rust to read
	goDbPath := filepath.Join(testDir, "go_data.db")
	_ = os.Remove(goDbPath)
	goDb, err := Open(goDbPath)
	if err != nil {
		t.Fatalf("Failed to create go_data.db: %v", err)
	}

	_ = goDb.PutString("", "hello", "world from go")
	_ = goDb.PutString("user_ns", "name", "Bob")
	_ = goDb.PutBytes("bin_ns", "binary_key", []byte{0xAA, 0xBB, 0xCC})

	_ = goDb.PutInt64("types", "go_int", -1234567890987654321)
	_ = goDb.PutFloat64("types", "go_float", 2.718281828459045)
	_ = goDb.PutFloat64("types", "go_nan", math.NaN())
	_ = goDb.PutBool("types", "go_bool", true)

	_ = goDb.Close()
}
