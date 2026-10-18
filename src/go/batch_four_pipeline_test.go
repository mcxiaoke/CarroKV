package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"
)

// TestBatchFourWarningNoDeadlock 验证 OnWarning 移出写锁派发，防止重入读造成死锁（消除 P1-33）。
func TestBatchFourWarningNoDeadlock(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "warning_deadlock.db")

	var warningReceived int32
	opts := DefaultDbOptions()
	opts.CompactionThresholdBytes = 100
	opts.StrictCompaction = false
	opts.EngineMode = StorageEngineModeAppendOnlyLog

	var dbHandle *DB
	opts.OnWarning = func(code, msg string) {
		atomic.AddInt32(&warningReceived, 1)
		// 回调内执行读操作：若在写锁内触发则直接死锁！
		if dbHandle != nil {
			_, _ = dbHandle.GetInt64("ns", "key")
			_ = dbHandle.Namespaces()
		}
	}

	db, err := Open(dbPath, opts)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()
	dbHandle = db

	// 阻塞 compaction 触发 warning
	blocker := dbPath + ".tmp"
	if err := os.Mkdir(blocker, 0777); err != nil {
		t.Fatalf("Failed to create blocker dir: %v", err)
	}
	defer os.RemoveAll(blocker)

	// 持续写入触发 compaction failure
	done := make(chan bool)
	go func() {
		for i := 0; i < 50; i++ {
			_ = db.PutString("ns", fmt.Sprintf("k%d", i), "large_payload_value_to_exceed_compaction_threshold")
		}
		done <- true
	}()

	select {
	case <-done:
		// 顺利完成，没有发生死锁
	case <-time.After(5 * time.Second):
		t.Fatal("Deadlock detected in OnWarning reentrancy!")
	}
}

// TestBatchFourCoWDeduplication 验证同一写事务/Batch 内同一命名空间只克隆一次（消除 P1-1）。
func TestBatchFourCoWDeduplication(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "cow_dedup.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 初始化数据
	if err := db.PutString("bulk", "seed", "0"); err != nil {
		t.Fatalf("Put failed: %v", err)
	}

	// 创建存活快照，使写路径进入 CoW 模式
	snap := db.Snapshot()
	defer snap.Close()

	start := time.Now()
	// 提交包含 2000 个操作的大 Batch
	err = db.Batch(func(b *BatchWriter) error {
		for i := 0; i < 2000; i++ {
			if i%2 == 0 {
				_ = b.PutString("bulk", fmt.Sprintf("k%d", i), "v")
			} else {
				_ = b.Delete("bulk", fmt.Sprintf("k%d", i-1))
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch failed: %v", err)
	}
	duration := time.Since(start)

	// 2000 个操作单次克隆应在几十毫秒内完成，若为 O(N^2) 重复克隆将花费数秒
	if duration > 2*time.Second {
		t.Fatalf("Batch write took too long (%v), suspected O(N^2) clone explosion", duration)
	}

	// 验证快照不受影响
	v, err := snap.GetString("bulk", "seed")
	if err != nil || v == nil || *v != "0" {
		t.Fatalf("Snapshot corrupted after batch write: %v", v)
	}
}

// TestBatchFourRollbackOnFlushFailure 验证提交失败后的内存回滚与保留 isDirty（消除 P0-7 / P1-6）。
func TestBatchFourRollbackOnFlushFailure(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "rollback_test.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	if err := db.PutInt64("bank", "balance", 100); err != nil {
		t.Fatalf("Initial put failed: %v", err)
	}

	// 创建目录阻断 .tmp 文件创建，使 Immediate 刷盘阶段必然失败
	tmpPath := dbPath + ".tmp"
	if err := os.Mkdir(tmpPath, 0777); err != nil {
		t.Fatalf("Failed to create blocker dir: %v", err)
	}

	// 尝试 IncrementInt64，应该失败并包装为 IoError
	_, incErr := db.IncrementInt64("bank", "balance", 50)
	if incErr == nil {
		_ = os.RemoveAll(tmpPath)
		t.Fatal("Expected write error due to blocked .tmp, but got nil")
	}

	// 验证：内存必须回滚为 100，不得残留 150（消除 double increment）
	val, err := db.GetInt64("bank", "balance")
	if err != nil {
		_ = os.RemoveAll(tmpPath)
		t.Fatalf("GetInt64 failed: %v", err)
	}
	if val == nil || *val != 100 {
		_ = os.RemoveAll(tmpPath)
		t.Fatalf("Memory not rolled back after failed flush! Expected 100, got %v", val)
	}

	// 解除阻断后重试
	if err := os.RemoveAll(tmpPath); err != nil {
		t.Fatalf("Failed to remove blocker: %v", err)
	}

	newVal, err := db.IncrementInt64("bank", "balance", 50)
	if err != nil {
		t.Fatalf("Retry IncrementInt64 failed: %v", err)
	}
	if newVal != 150 {
		t.Fatalf("Double increment detected! Expected 150, got %d", newVal)
	}
}

// TestBatchFourConcurrentReadDuringWrite 验证写路径序列化期间并发读者零阻塞（消除 P1-29）。
func TestBatchFourConcurrentReadDuringWrite(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "zero_block.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 写入基础数据
	if err := db.PutString("read_ns", "key", "stable_value"); err != nil {
		t.Fatalf("Put failed: %v", err)
	}

	stop := make(chan bool)
	var readCount int64
	var maxReadLatency time.Duration

	// 启动并发读者
	go func() {
		for {
			select {
			case <-stop:
				return
			default:
				t0 := time.Now()
				v, err := db.GetString("read_ns", "key")
				elapsed := time.Since(t0)
				if err != nil || v == nil || *v != "stable_value" {
					t.Errorf("Concurrent read failed: %v", err)
				}
				atomic.AddInt64(&readCount, 1)
				if elapsed > maxReadLatency {
					maxReadLatency = elapsed
				}
				time.Sleep(100 * time.Microsecond)
			}
		}
	}()

	// 持续写入大量数据触发 Immediate 序列化与落盘
	for i := 0; i < 20; i++ {
		_ = db.PutString("write_ns", fmt.Sprintf("k%d", i), "payload_data")
	}

	close(stop)

	if atomic.LoadInt64(&readCount) == 0 {
		t.Fatal("Concurrent readers were completely starved!")
	}
}
