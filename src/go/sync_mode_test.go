package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestSyncModeManualFlushAndClose(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "manual.db")

	// 1. Manual 模式
	opts := DefaultDbOptions()
	opts.SyncMode = SyncModeManual
	db, err := Open(p, opts)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}

	if err := db.PutString("", "k1", "v1"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}
	if err := db.PutString("", "k2", "v2"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}

	// 内存立即可读
	v1, err := db.GetString("", "k1")
	if err != nil || v1 == nil || *v1 != "v1" {
		t.Fatalf("GetString k1 expected v1, got %v, err %v", v1, err)
	}

	// 显式 flush
	if err := db.Flush(); err != nil {
		t.Fatalf("Flush failed: %v", err)
	}
	if _, err := os.Stat(p); err != nil {
		t.Fatalf("Database file should exist after flush: %v", err)
	}
	_ = db.Close()

	// 重新打开验证
	db2, err := Open(p)
	if err != nil {
		t.Fatalf("Reopen failed: %v", err)
	}
	v1, err = db2.GetString("", "k1")
	if err != nil || v1 == nil || *v1 != "v1" {
		t.Fatalf("Reopen GetString k1 expected v1, got %v", v1)
	}
	_ = db2.Close()

	// 2. 测试无需显式 flush，Close 自动刷盘
	p2 := filepath.Join(dir, "manual_close.db")
	db3, err := Open(p2, opts)
	if err != nil {
		t.Fatalf("Open p2 failed: %v", err)
	}
	if err := db3.PutString("", "k_auto", "v_auto"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}
	if err := db3.Close(); err != nil {
		t.Fatalf("Close failed: %v", err)
	}

	db4, err := Open(p2)
	if err != nil {
		t.Fatalf("Reopen p2 failed: %v", err)
	}
	vAuto, err := db4.GetString("", "k_auto")
	if err != nil || vAuto == nil || *vAuto != "v_auto" {
		t.Fatalf("Expected v_auto, got %v", vAuto)
	}
	_ = db4.Close()
}

func TestSyncModeDeferredBackgroundFlush(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "deferred.db")

	opts := DefaultDbOptions()
	opts.SyncMode = SyncModeDeferred
	opts.SyncIntervalMs = 50

	db, err := Open(p, opts)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}

	if err := db.PutString("", "key_async", "val_async"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}

	// 等待后台定时器刷盘 (至少 120ms)
	time.Sleep(150 * time.Millisecond)

	if _, err := os.Stat(p); err != nil {
		t.Fatalf("Database file should exist after deferred flush: %v", err)
	}
	_ = db.Close()

	// 重新打开校验
	db2, err := Open(p)
	if err != nil {
		t.Fatalf("Reopen failed: %v", err)
	}
	v, err := db2.GetString("", "key_async")
	if err != nil || v == nil || *v != "val_async" {
		t.Fatalf("Expected val_async, got %v", v)
	}
	_ = db2.Close()
}

func TestSyncModeHighFrequencyWriteCoalescing(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "coalesce.db")

	opts := DefaultDbOptions()
	opts.SyncMode = SyncModeManual
	db, err := Open(p, opts)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}

	for i := 0; i < 1000; i++ {
		if err := db.PutInt64("", fmt.Sprintf("num_%d", i), int64(i)); err != nil {
			t.Fatalf("PutInt64 failed at %d: %v", i, err)
		}
	}

	c, err := db.Count("")
	if err != nil || c != 1000 {
		t.Fatalf("Count expected 1000, got %d", c)
	}

	if err := db.Flush(); err != nil {
		t.Fatalf("Flush failed: %v", err)
	}
	_ = db.Close()

	db2, err := Open(p)
	if err != nil {
		t.Fatalf("Reopen failed: %v", err)
	}
	c2, _ := db2.Count("")
	if c2 != 1000 {
		t.Fatalf("Expected 1000 after reopen, got %d", c2)
	}
	num999, err := db2.GetInt64("", "num_999")
	if err != nil || num999 == nil || *num999 != 999 {
		t.Fatalf("Expected 999, got %v", num999)
	}
	_ = db2.Close()
}
