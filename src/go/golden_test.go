package ckv

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestGoldenEmptyV3Vector(t *testing.T) {
	header := CreateFileHeader()
	if len(header) != 32 {
		t.Fatalf("Expected header len 32, got %d", len(header))
	}

	crc := Crc32(header[0:28])
	if crc != 0x0C44C937 {
		t.Fatalf("Expected CRC 0x0C44C937, got 0x%08X", crc)
	}

	sum := sha256.Sum256(header[:])
	shaHex := hex.EncodeToString(sum[:])
	expectedSha := "06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328"
	if shaHex != expectedSha {
		t.Fatalf("Expected SHA-256 %s, got %s", expectedSha, shaHex)
	}
}

func TestGoldenSinglePutV3Vector(t *testing.T) {
	testDir := filepath.Join("..", "..", "temp", "test_go_golden")
	_ = os.RemoveAll(testDir)
	_ = os.MkdirAll(testDir, 0755)
	defer os.RemoveAll(testDir)

	dbPath := filepath.Join(testDir, "single_put_v3.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Failed to open db: %v", err)
	}

	if err := db.PutString("", "hello", "world"); err != nil {
		t.Fatalf("Failed to put: %v", err)
	}
	_ = db.Close()

	data, err := os.ReadFile(dbPath)
	if err != nil {
		t.Fatalf("Failed to read db file: %v", err)
	}

	if len(data) != 80 {
		t.Fatalf("Expected file size 80, got %d", len(data))
	}

	sum := sha256.Sum256(data)
	shaHex := hex.EncodeToString(sum[:])
	expectedSha := "f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a"
	if shaHex != expectedSha {
		t.Fatalf("Expected SHA-256 %s, got %s", expectedSha, shaHex)
	}
}

func TestCrudAndSorting(t *testing.T) {
	testDir := filepath.Join("..", "..", "temp", "test_go_crud")
	_ = os.RemoveAll(testDir)
	_ = os.MkdirAll(testDir, 0755)
	defer os.RemoveAll(testDir)

	dbPath := filepath.Join(testDir, "crud.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Failed to open db: %v", err)
	}

	// Insert Emoji and Unicode
	_ = db.PutString("", "😀", "emoji")
	_ = db.PutString("", "a", "first")
	_ = db.PutString("", "\uE000", "private_use")
	_ = db.PutString("", "z", "last_ascii")

	keys, err := db.Keys("")
	if err != nil {
		t.Fatalf("Keys failed: %v", err)
	}

	expectedKeys := []string{"a", "z", "\uE000", "😀"}
	if !reflect.DeepEqual(keys, expectedKeys) {
		t.Fatalf("Expected keys %v, got %v", expectedKeys, keys)
	}

	// Test ScanRange
	zKey := "z"
	emojiKey := "😀"
	entries, err := db.ScanRange("", &zKey, &emojiKey)
	if err != nil {
		t.Fatalf("ScanRange failed: %v", err)
	}
	if len(entries) != 2 {
		t.Fatalf("Expected 2 entries, got %d", len(entries))
	}
	if entries[0].Key != "z" || entries[1].Key != "\uE000" {
		t.Fatalf("Unexpected entries: %v", entries)
	}

	// Test batch rollback
	batchErr := db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("", "temp_key", "temp_val")
		return fmt.Errorf("intentional batch failure")
	})
	if batchErr == nil {
		t.Fatal("Expected batch error")
	}

	val, err := db.GetString("", "temp_key")
	if err != nil {
		t.Fatalf("GetString error: %v", err)
	}
	if val != nil {
		t.Fatalf("Expected nil, got %v", *val)
	}

	// Test batch success
	_ = db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("", "k1", "v1")
		_ = b.PutString("", "k2", "v2")
		return nil
	})

	v1, _ := db.GetString("", "k1")
	v2, _ := db.GetString("", "k2")
	if v1 == nil || *v1 != "v1" || v2 == nil || *v2 != "v2" {
		t.Fatalf("Batch values mismatch: k1=%v, k2=%v", v1, v2)
	}
}
