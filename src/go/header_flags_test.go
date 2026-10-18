package ckv

// M1-01：非零 HeaderFlags 必须拒绝打开（规范 KV-DB-DESIGN-SPEC:79）。

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"testing"
)

func patchHeaderFlags(t *testing.T, path string, flags uint32) {
	t.Helper()
	bytes, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	binary.LittleEndian.PutUint32(bytes[12:16], flags)
	// 重算头部 CRC：模拟"写入方有意设置未知必选位"；不重算会被正确判成 Corrupted。
	binary.LittleEndian.PutUint32(bytes[28:32], Crc32(bytes[0:28]))
	if err := os.WriteFile(path, bytes, 0o666); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func TestNonZeroHeaderFlagsAreRejected(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "flags.db")

	db, err := Open(path, DefaultDbOptions())
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}
	db.Close()

	patchHeaderFlags(t, path, 1)

	if _, err := Open(path, DefaultDbOptions()); err == nil {
		t.Fatal("a file with unknown header flags must not open")
	} else if IsCorrupted(err) {
		// 语义上归"版本/特性不支持"，不能退化成"文件损坏"（否则调用方会去走修复路径）。
		t.Fatalf("expected UnsupportedVersionError, got a CorruptedError: %v", err)
	}
}

func TestZeroHeaderFlagsStillOpen(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "ok.db")

	db, err := Open(path, DefaultDbOptions())
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}
	db.Close()

	reopened, err := Open(path, DefaultDbOptions())
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer reopened.Close()
	if got, _ := reopened.GetString("", "k"); got == nil || *got != "v" {
		t.Fatalf("value lost: %v", got)
	}
}
