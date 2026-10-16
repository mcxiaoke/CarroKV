package ckv

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"testing"
)

// P0/P1 修复回归测试（与 Rust/.NET 同构）：
//   - 墓碑（OpDelete/OpDropNamespace）重放
//   - TxID 单调性 / OpCount 范围 / 未知 OpCode 拒绝
//   - 锁行为已随 C9 迁移至 lock_protocol_test.go（内核锁 + 连接生命周期持有）
//   - 外部磁盘篡改检测（写前预检）

// ---------- 测试文件构建工具（按 v3.2 规范手工构造帧） ----------

func tWriteU16(buf []byte, off int, val uint16) {
	binary.LittleEndian.PutUint16(buf[off:off+2], val)
}

func tWriteU32(buf []byte, off int, val uint32) {
	binary.LittleEndian.PutUint32(buf[off:off+4], val)
}

func tWriteU64(buf []byte, off int, val uint64) {
	binary.LittleEndian.PutUint64(buf[off:off+8], val)
}

func tBuildEntry(opCode, valType uint8, ns string, key, val []byte) []byte {
	nsBytes := []byte(ns)
	entry := make([]byte, EntryFixedHeader+len(nsBytes)+len(key)+len(val))
	entry[0] = opCode
	entry[1] = valType
	tWriteU16(entry, 2, uint16(len(nsBytes)))
	tWriteU16(entry, 4, uint16(len(key)))
	tWriteU32(entry, 6, uint32(len(val)))
	off := EntryFixedHeader
	copy(entry[off:], nsBytes)
	off += len(nsBytes)
	copy(entry[off:], key)
	off += len(key)
	copy(entry[off:], val)
	return entry
}

// tBuildFrameRaw 手工构造帧（绕过 WriteBatchFrame 的 OpCount 校验，
// 用于注入 OpCount=0 等非法帧）。
func tBuildFrameRaw(txID uint64, opCount uint32, payload []byte) []byte {
	var tail20 [20]byte
	tWriteU64(tail20[:], 0, txID)
	tWriteU32(tail20[:], 8, opCount)
	tWriteU32(tail20[:], 12, uint32(len(payload)))
	tWriteU32(tail20[:], 16, Crc32(payload))

	frame := make([]byte, FrameHeaderLen+len(payload))
	tWriteU32(frame, 0, FrameMagic)
	tWriteU32(frame, 4, Crc32(tail20[:]))
	copy(frame[8:28], tail20[:])
	copy(frame[28:], payload)
	return frame
}

func tBuildDb(frames ...[]byte) []byte {
	var out []byte
	header := CreateFileHeader()
	out = append(out, header[:]...)
	for _, f := range frames {
		out = append(out, f...)
	}
	return out
}

func isConcurrentModification(err error) bool {
	var de *DbError
	if e, ok := err.(*DbError); ok {
		de = e
	}
	return de != nil && de.Type == "ConcurrentModificationError"
}

// ---------- P0-2：墓碑必须被重放 ----------

func TestTombstonesAreReplayedOnLoad(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "tombstone.db")

	// 帧1：put keep/dead（ns1）+ put x（ns_gone）
	// 帧2：delete dead（ns1）
	// 帧3：drop 整个 ns_gone
	// 帧4：put after_delete（验证 delete 之后的 put 生效）
	frame1 := tBuildFrameRaw(1, 3, concatEntries(
		tBuildEntry(OpPut, ValTypeString, "ns1", []byte("keep"), []byte("v_keep")),
		tBuildEntry(OpPut, ValTypeString, "ns1", []byte("dead"), []byte("v_dead")),
		tBuildEntry(OpPut, ValTypeString, "ns_gone", []byte("x"), []byte("v_x")),
	))
	frame2 := tBuildFrameRaw(2, 1, concatEntries(
		tBuildEntry(OpDelete, 0, "ns1", []byte("dead"), nil),
	))
	frame3 := tBuildFrameRaw(3, 1, concatEntries(
		tBuildEntry(OpDropNamespace, 0, "ns_gone", nil, nil),
	))
	frame4 := tBuildFrameRaw(4, 1, concatEntries(
		tBuildEntry(OpPut, ValTypeString, "ns1", []byte("after_delete"), []byte("v_ad")),
	))

	if err := os.WriteFile(dbPath, tBuildDb(frame1, frame2, frame3, frame4), 0o600); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	if has, _ := db.ContainsKey("ns1", "keep"); !has {
		t.Errorf("Expected keep to survive")
	}
	if has, _ := db.ContainsKey("ns1", "dead"); has {
		t.Errorf("Expected dead to be tombstoned")
	}
	if has, _ := db.ContainsKey("ns1", "after_delete"); !has {
		t.Errorf("Expected after_delete put to apply")
	}
	if cnt, _ := db.Count("ns_gone"); cnt != 0 {
		t.Errorf("Expected ns_gone dropped, got count %d", cnt)
	}
	v, _ := db.GetString("ns1", "keep")
	if v == nil || *v != "v_keep" {
		t.Errorf("Expected keep=v_keep, got %v", v)
	}
}

func concatEntries(entries ...[]byte) []byte {
	var out []byte
	for _, e := range entries {
		out = append(out, e...)
	}
	return out
}

// ---------- P0-3：TxID 单调性 ----------

func TestNonMonotonicTxIdIsRejected(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "nonmono.db")

	f1 := tBuildFrameRaw(1, 1, concatEntries(
		tBuildEntry(OpPut, ValTypeString, "ns", []byte("k1"), []byte("v1")),
	))
	f2 := tBuildFrameRaw(1, 1, concatEntries( // 相同 TxID
		tBuildEntry(OpPut, ValTypeString, "ns", []byte("k2"), []byte("v2")),
	))
	if err := os.WriteFile(dbPath, tBuildDb(f1, f2), 0o600); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	if _, err := Open(dbPath); !IsCorrupted(err) {
		t.Fatalf("Expected CorruptedError for non-monotonic TxID, got %v", err)
	}
}

func TestDescendingTxIdIsRejected(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "descending.db")

	f1 := tBuildFrameRaw(5, 1, concatEntries(
		tBuildEntry(OpPut, ValTypeString, "ns", []byte("k1"), []byte("v1")),
	))
	f2 := tBuildFrameRaw(3, 1, concatEntries( // TxID 递减
		tBuildEntry(OpPut, ValTypeString, "ns", []byte("k2"), []byte("v2")),
	))
	if err := os.WriteFile(dbPath, tBuildDb(f1, f2), 0o600); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	if _, err := Open(dbPath); !IsCorrupted(err) {
		t.Fatalf("Expected CorruptedError for descending TxID, got %v", err)
	}
}

// ---------- P0-3：OpCount 范围 ----------

func TestZeroOpCountFrameIsRejected(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "zeroops.db")

	f1 := tBuildFrameRaw(1, 0, nil) // OpCount = 0
	if err := os.WriteFile(dbPath, tBuildDb(f1), 0o600); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	if _, err := Open(dbPath); !IsCorrupted(err) {
		t.Fatalf("Expected CorruptedError for zero OpCount, got %v", err)
	}
}

// ---------- P0-3：未知 OpCode 拒绝（与 .NET 对齐） ----------

func TestUnknownOpCodeIsRejected(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "unknownop.db")

	f1 := tBuildFrameRaw(1, 1, concatEntries(
		tBuildEntry(0x7F, 0, "ns", []byte("k"), nil), // 规范外 OpCode
	))
	if err := os.WriteFile(dbPath, tBuildDb(f1), 0o600); err != nil {
		t.Fatalf("WriteFile failed: %v", err)
	}

	if _, err := Open(dbPath); !IsCorrupted(err) {
		t.Fatalf("Expected CorruptedError for unknown op code, got %v", err)
	}
}

// ---------- P0-8：锁文件 32B 布局行为验证 ----------
// 布局：PID(u64) @0..8 | LockUUID(16B) @8..24 | CreateTimeMs(u64) @24..32
// 若实现误读偏移则无法识别陈旧锁，写入将超时失败。

func TestExternalDiskModificationIsDetected(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "tamper.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()
	if err := db.PutString("ns", "k1", "v1"); err != nil {
		t.Fatalf("PutString k1 failed: %v", err)
	}
	if err := db.PutString("ns", "k2", "v2"); err != nil {
		t.Fatalf("PutString k2 failed: %v", err)
	}

	// 外部"合法"写入：追加一个 TxID 更高的有效帧，改变 (maxTxID, fileSize) 基线
	f, err := os.OpenFile(dbPath, os.O_RDWR|os.O_APPEND, 0o600)
	if err != nil {
		t.Fatalf("OpenFile append failed: %v", err)
	}
	if err := WriteBatchFrame(f, 999, []*RawOpEntry{
		{OpCode: OpPut, ValueType: ValTypeString, Namespace: []byte("ns"), Key: []byte("evil"), Value: []byte("x")},
	}); err != nil {
		t.Fatalf("WriteBatchFrame failed: %v", err)
	}
	f.Close()

	// 下一次写入必须检测到磁盘状态变化
	if err := db.PutString("ns", "k3", "v3"); !isConcurrentModification(err) {
		t.Fatalf("Expected ConcurrentModificationError, got %v", err)
	}
}
