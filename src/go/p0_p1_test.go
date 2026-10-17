package ckv

import (
	"path/filepath"
	"sync/atomic"
	"testing"
)

func TestGoP0P1ApisAndWatchers(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "p0_p1.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	var putCount int64
	var delCount int64
	var dropCount int64
	var clearCount int64
	var lastAggregateKey string

	db.Watch(func(ev KeyChangeEvent) {
		switch ev.Op {
		case OpChangePut:
			atomic.AddInt64(&putCount, 1)
		case OpChangeDelete:
			atomic.AddInt64(&delCount, 1)
		case OpChangeDropNamespace:
			atomic.AddInt64(&dropCount, 1)
			lastAggregateKey = ev.Key
		case OpChangeClear:
			atomic.AddInt64(&clearCount, 1)
			lastAggregateKey = ev.Key
		}
	})

	// 1. ContainsKey & Count
	has, err := db.ContainsKey("users", "user1")
	if err != nil || has {
		t.Fatalf("Expected user1 not found, got %v", has)
	}
	cnt, err := db.Count("users")
	if err != nil || cnt != 0 {
		t.Fatalf("Expected count 0, got %d", cnt)
	}

	if err := db.PutString("users", "user1", "Alice"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}
	has, err = db.ContainsKey("users", "user1")
	if err != nil || !has {
		t.Fatalf("Expected user1 found")
	}
	cnt, err = db.Count("users")
	if err != nil || cnt != 1 {
		t.Fatalf("Expected count 1, got %d", cnt)
	}
	if atomic.LoadInt64(&putCount) != 1 {
		t.Fatalf("Expected putCount 1, got %d", putCount)
	}

	// 2. IncrementInt64
	val1, err := db.IncrementInt64("counters", "page_views", 10)
	if err != nil || val1 != 10 {
		t.Fatalf("Expected increment 10, got %d, err %v", val1, err)
	}
	val2, err := db.IncrementInt64("counters", "page_views", 5)
	if err != nil || val2 != 15 {
		t.Fatalf("Expected increment 15, got %d, err %v", val2, err)
	}
	readVal, err := db.GetInt64("counters", "page_views")
	if err != nil || readVal == nil || *readVal != 15 {
		t.Fatalf("GetInt64 mismatch: got %v", readVal)
	}

	// 3. Increment 类型不匹配拦截
	_ = db.PutString("counters", "not_an_int", "hello")
	if _, err := db.IncrementInt64("counters", "not_an_int", 1); err == nil {
		t.Fatalf("Expected type mismatch error on incrementing string")
	}

	// 4. ClearNamespace：返回 bool + 单条聚合事件（Key 为空串，规范 §5.2.3）
	_ = db.PutString("temp_ns", "k1", "v1")
	_ = db.PutString("temp_ns", "k2", "v2")
	cnt, _ = db.Count("temp_ns")
	if cnt != 2 {
		t.Fatalf("Expected temp_ns count 2, got %d", cnt)
	}

	cleared, err := db.ClearNamespace("temp_ns")
	if err != nil || !cleared {
		t.Fatalf("Expected ClearNamespace to return true, got %v, err %v", cleared, err)
	}
	cnt, _ = db.Count("temp_ns")
	if cnt != 0 {
		t.Fatalf("Expected temp_ns count 0 after clear, got %d", cnt)
	}
	has, _ = db.ContainsKey("temp_ns", "k1")
	if has {
		t.Fatalf("Expected k1 cleared")
	}
	// 重复清空（命名空间存在但已空）返回 false 且不产生事件
	clearedAgain, err := db.ClearNamespace("temp_ns")
	if err != nil || clearedAgain {
		t.Fatalf("Expected second ClearNamespace to return false, got %v, err %v", clearedAgain, err)
	}
	if atomic.LoadInt64(&clearCount) != 1 {
		t.Fatalf("Expected exactly 1 aggregate Clear event, got %d", clearCount)
	}
	if lastAggregateKey != "" {
		t.Fatalf("Expected aggregate event Key to be empty string, got %q", lastAggregateKey)
	}
	// 清空不得产生逐键 Delete 事件
	if atomic.LoadInt64(&delCount) != 0 {
		t.Fatalf("Expected 0 Delete events from clear, got %d", delCount)
	}

	// 4b. Delete 返回 bool（规范 §5.2.2）
	_ = db.PutString("temp_ns", "k3", "v3")
	deleted, err := db.Delete("temp_ns", "k3")
	if err != nil || !deleted {
		t.Fatalf("Expected Delete to return true for existing key, got %v, err %v", deleted, err)
	}
	deletedMissing, err := db.Delete("temp_ns", "nonexistent")
	if err != nil || deletedMissing {
		t.Fatalf("Expected Delete to return false for missing key, got %v, err %v", deletedMissing, err)
	}
	if atomic.LoadInt64(&delCount) != 1 {
		t.Fatalf("Expected 1 Delete event, got %d", delCount)
	}

	// 4c. DropNamespace：返回 bool + 单条聚合事件
	_ = db.PutString("drop_ns", "d1", "v1")
	dropped, err := db.DropNamespace("drop_ns")
	if err != nil || !dropped {
		t.Fatalf("Expected DropNamespace to return true, got %v, err %v", dropped, err)
	}
	droppedAgain, err := db.DropNamespace("drop_ns")
	if err != nil || droppedAgain {
		t.Fatalf("Expected second DropNamespace to return false, got %v, err %v", droppedAgain, err)
	}
	if atomic.LoadInt64(&dropCount) != 1 {
		t.Fatalf("Expected exactly 1 aggregate DropNamespace event, got %d", dropCount)
	}
	if lastAggregateKey != "" {
		t.Fatalf("Expected aggregate event Key to be empty string, got %q", lastAggregateKey)
	}
	if atomic.LoadInt64(&delCount) != 1 {
		t.Fatalf("Expected drop to not emit per-key Delete events, delCount=%d", delCount)
	}

	// 4d. B1 回归：整库清空（落盘为仅含 32B 文件头的空库）后再次写入，
	// 不得误报 ConcurrentModificationError（空文件磁盘 maxTxID=0 与内存不可比）
	if _, err := db.DropNamespace("users"); err != nil {
		t.Fatalf("DropNamespace users failed: %v", err)
	}
	if _, err := db.DropNamespace("counters"); err != nil {
		t.Fatalf("DropNamespace counters failed: %v", err)
	}
	// 此刻整库为空，落盘文件仅含 32B 文件头
	if err := db.PutString("temp_ns", "k1", "v1"); err != nil {
		t.Fatalf("B1 regression: write after full clear failed: %v", err)
	}
	if err := db.PutString("temp_ns", "k2", "v2"); err != nil {
		t.Fatalf("B1 regression: second write after full clear failed: %v", err)
	}
	if err := db.PutString("temp_ns", "k3", "v3"); err != nil {
		t.Fatalf("B1 regression: third write after full clear failed: %v", err)
	}
	cnt, _ = db.Count("temp_ns")
	if cnt != 3 {
		t.Fatalf("Expected temp_ns count 3 after rewrite, got %d", cnt)
	}

	// 5. Batch Watcher：put/delete 逐键事件 + clear/drop 单条聚合事件（Key 为空串）
	_ = db.PutString("batch_clear_ns", "bc1", "bv1")
	_ = db.PutString("batch_drop_ns", "bd1", "bdv1")
	// users 在 4d 已被 DropNamespace，重新写入一个真实存在的键，
	// 使 batch 中的 Delete 有真实目标（B3：no-op 不得派发事件）。
	_ = db.PutString("users", "user1", "Alice")
	delBefore := atomic.LoadInt64(&delCount)
	dropBefore := atomic.LoadInt64(&dropCount)
	clearBefore := atomic.LoadInt64(&clearCount)

	err = db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("users", "user2", "Bob")
		_ = b.Delete("users", "user1")
		_ = b.ClearNamespace("batch_clear_ns")
		_ = b.DropNamespace("batch_drop_ns")
		return nil
	})
	if err != nil {
		t.Fatalf("Batch failed: %v", err)
	}

	has1, _ := db.ContainsKey("users", "user1")
	has2, _ := db.ContainsKey("users", "user2")
	if has1 || !has2 {
		t.Fatalf("Batch verify failed: has1=%v, has2=%v", has1, has2)
	}
	bcCnt, _ := db.Count("batch_clear_ns")
	if bcCnt != 0 {
		t.Fatalf("Expected batch_clear_ns cleared, got count %d", bcCnt)
	}
	bdHas, _ := db.ContainsKey("batch_drop_ns", "bd1")
	if bdHas {
		t.Fatalf("Expected batch_drop_ns dropped")
	}
	if got := atomic.LoadInt64(&delCount) - delBefore; got != 1 {
		t.Fatalf("Expected 1 Delete event from batch, got %d", got)
	}
	if got := atomic.LoadInt64(&dropCount) - dropBefore; got != 1 {
		t.Fatalf("Expected 1 aggregate DropNamespace event from batch, got %d", got)
	}
	if got := atomic.LoadInt64(&clearCount) - clearBefore; got != 1 {
		t.Fatalf("Expected 1 aggregate Clear event from batch, got %d", got)
	}
	if lastAggregateKey != "" {
		t.Fatalf("Expected aggregate event Key to be empty string, got %q", lastAggregateKey)
	}

	// 5b. B3 回归：batch 中针对不存在/已空的目标均为 no-op，不得派发幻影事件
	delBefore = atomic.LoadInt64(&delCount)
	dropBefore = atomic.LoadInt64(&dropCount)
	clearBefore = atomic.LoadInt64(&clearCount)
	if err := db.Batch(func(b *BatchWriter) error {
		_ = b.Delete("users", "does_not_exist")
		_ = b.ClearNamespace("batch_clear_ns") // 上一步已清空
		_ = b.DropNamespace("batch_drop_ns")   // 上一步已删除
		_ = b.ClearNamespace("never_existed")
		_ = b.DropNamespace("never_existed")
		_ = b.Delete("never_existed", "k")
		return nil
	}); err != nil {
		t.Fatalf("no-op batch failed: %v", err)
	}
	if got := atomic.LoadInt64(&delCount) - delBefore; got != 0 {
		t.Fatalf("B3: no-op batch delete must not dispatch Delete event, got %d", got)
	}
	if got := atomic.LoadInt64(&dropCount) - dropBefore; got != 0 {
		t.Fatalf("B3: no-op batch drop must not dispatch DropNamespace event, got %d", got)
	}
	if got := atomic.LoadInt64(&clearCount) - clearBefore; got != 0 {
		t.Fatalf("B3: no-op batch clear must not dispatch Clear event, got %d", got)
	}

	// 6. 重载验证：清空后重写的数据完整持久化，且重载后写入不误报
	if err := db.Close(); err != nil {
		t.Fatalf("Close failed: %v", err)
	}
	db2, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Reopen failed: %v", err)
	}
	defer db2.Close()
	cnt, _ = db2.Count("temp_ns")
	if cnt != 3 {
		t.Fatalf("Expected temp_ns count 3 after reload, got %d", cnt)
	}
	v3, err := db2.GetString("temp_ns", "k3")
	if err != nil || v3 == nil || *v3 != "v3" {
		t.Fatalf("Expected k3=v3 after reload, got %v, err %v", v3, err)
	}
	// 重载后继续写入同样不得误报（lastKnownDiskState 来自加载结果）
	if err := db2.PutString("temp_ns", "k4", "v4"); err != nil {
		t.Fatalf("B1 regression: write after reload failed: %v", err)
	}
	cnt, _ = db2.Count("temp_ns")
	if cnt != 4 {
		t.Fatalf("Expected temp_ns count 4 after post-reload write, got %d", cnt)
	}
}
