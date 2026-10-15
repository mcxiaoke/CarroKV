package ckv

import (
	"bytes"
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

	// Verify data written by Rust
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

	// Write new data from Go into a separate file for Rust to read
	goDbPath := filepath.Join(testDir, "go_data.db")
	_ = os.Remove(goDbPath)
	goDb, err := Open(goDbPath)
	if err != nil {
		t.Fatalf("Failed to create go_data.db: %v", err)
	}

	_ = goDb.PutString("", "hello", "world from go")
	_ = goDb.PutString("user_ns", "name", "Bob")
	_ = goDb.PutBytes("bin_ns", "binary_key", []byte{0xAA, 0xBB, 0xCC})
	_ = goDb.Close()
}
