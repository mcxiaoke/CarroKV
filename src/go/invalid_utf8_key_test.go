package ckv

// API-SPEC §3.3.3：非法 UTF-8 键在扫描接口里必须跳过，不得返回 U+FFFD 伪键。
// 写 API 的 key 是 string，非法键只能来自损坏/外部构造的文件，这里直接改字节并重算两级 CRC。

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"testing"
)

func TestScansSkipInvalidUtf8Keys(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "bad_key.db")

	db, err := Open(path, DefaultDbOptions())
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}
	db.Close()

	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	b[70] = 0xFF // 32B 头 + 28B 帧头 + 10B 条目定长头；ns 空、key 长 1
	binary.LittleEndian.PutUint32(b[56:60], Crc32(b[60:]))
	binary.LittleEndian.PutUint32(b[36:40], Crc32(b[40:60]))
	if err := os.WriteFile(path, b, 0o666); err != nil {
		t.Fatalf("write: %v", err)
	}

	reopened, err := Open(path, DefaultDbOptions())
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer reopened.Close()

	if keys, _ := reopened.Keys(""); len(keys) != 0 {
		t.Fatalf("keys() surfaced an invalid UTF-8 key: %v", keys)
	}
	if entries, _ := reopened.IterPrefix(""); len(entries) != 0 {
		t.Fatalf("IterPrefix() returned a U+FFFD ghost entry: %+v", entries)
	}
	// §3.3.3 只约束 keys / iter_prefix；ScanRange 按字节序枚举，v1.0 冻结语义是
	// "条目仍在列表里"（见 api_v1_freeze_test），这里只锁住条数。
	if entries, _ := reopened.ScanRange("", nil, nil); len(entries) != 1 {
		t.Fatalf("ScanRange() = %d entries, want 1 (byte-order scan keeps the entry)", len(entries))
	}
	if n, _ := reopened.Count(""); n != 1 {
		t.Fatalf("the entry itself must still be stored, count=%d", n)
	}

	// 快照是另一套实现，同一份数据必须给出同一结论。
	snap := reopened.Snapshot()
	if keys, _ := snap.Keys(""); len(keys) != 0 {
		t.Fatalf("snapshot keys() surfaced an invalid UTF-8 key: %v", keys)
	}
	if entries, _ := snap.IterPrefix(""); len(entries) != 0 {
		t.Fatalf("snapshot IterPrefix() returned a ghost entry: %+v", entries)
	}
	if entries, _ := snap.ScanRange("", nil, nil); len(entries) != 1 {
		t.Fatalf("snapshot ScanRange() = %d entries, want 1", len(entries))
	}
}
