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

	db.Watch(func(ev KeyChangeEvent) {
		switch ev.Op {
		case OpChangePut:
			atomic.AddInt64(&putCount, 1)
		case OpChangeDelete:
			atomic.AddInt64(&delCount, 1)
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

	// 4. ClearNamespace
	_ = db.PutString("temp_ns", "k1", "v1")
	_ = db.PutString("temp_ns", "k2", "v2")
	cnt, _ = db.Count("temp_ns")
	if cnt != 2 {
		t.Fatalf("Expected temp_ns count 2, got %d", cnt)
	}

	if err := db.ClearNamespace("temp_ns"); err != nil {
		t.Fatalf("ClearNamespace failed: %v", err)
	}
	cnt, _ = db.Count("temp_ns")
	if cnt != 0 {
		t.Fatalf("Expected temp_ns count 0 after clear, got %d", cnt)
	}
	has, _ = db.ContainsKey("temp_ns", "k1")
	if has {
		t.Fatalf("Expected k1 cleared")
	}

	// 5. Batch Watcher
	err = db.Batch(func(b *BatchWriter) error {
		_ = b.PutString("users", "user2", "Bob")
		_ = b.Delete("users", "user1")
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
}
