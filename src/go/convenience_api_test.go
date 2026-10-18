package ckv

import (
	"path/filepath"
	"testing"
)

func TestGoNamespaceScopeConvenience(t *testing.T) {
	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "test_convenience.db")

	db, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open failed: %v", err)
	}
	defer db.Close()

	// 1. Default() 作用域门面（默认命名空间）
	def := db.Default()
	if def.Namespace() != "" {
		t.Fatalf("expected empty namespace, got %q", def.Namespace())
	}

	if err := def.PutString("strKey", "strVal"); err != nil {
		t.Fatalf("PutString failed: %v", err)
	}

	strVal, err := def.GetString("strKey")
	if err != nil || strVal == nil || *strVal != "strVal" {
		t.Fatalf("GetString failed: val=%v, err=%v", strVal, err)
	}

	// 验证与底层通过 "" 读取等价
	rootVal, err := db.GetString("", "strKey")
	if err != nil || rootVal == nil || *rootVal != "strVal" {
		t.Fatalf("db.GetString(\"\") failed: val=%v, err=%v", rootVal, err)
	}

	if err := def.PutBytes("byteKey", []byte("byteVal")); err != nil {
		t.Fatalf("PutBytes failed: %v", err)
	}
	bVal, err := def.GetBytes("byteKey")
	if err != nil || string(bVal) != "byteVal" {
		t.Fatalf("GetBytes failed: %v", err)
	}

	if err := def.PutInt64("intKey", 42); err != nil {
		t.Fatalf("PutInt64 failed: %v", err)
	}
	newInt, err := def.IncrementInt64("intKey", 8)
	if err != nil || newInt != 50 {
		t.Fatalf("IncrementInt64 failed: %v", err)
	}

	if err := def.PutFloat64("floatKey", 3.14159); err != nil {
		t.Fatalf("PutFloat64 failed: %v", err)
	}
	fVal, err := def.GetFloat64("floatKey")
	if err != nil || fVal == nil || *fVal < 3.14 {
		t.Fatalf("GetFloat64 failed: %v", err)
	}

	if err := def.PutBool("boolKey", true); err != nil {
		t.Fatalf("PutBool failed: %v", err)
	}
	boolVal, err := def.GetBool("boolKey")
	if err != nil || boolVal == nil || !*boolVal {
		t.Fatalf("GetBool failed: %v", err)
	}

	hasKey, err := def.ContainsKey("strKey")
	if err != nil || !hasKey {
		t.Fatalf("ContainsKey failed: %v", err)
	}

	cnt, err := def.Count()
	if err != nil || cnt != 5 {
		t.Fatalf("Count failed: %d, err=%v", cnt, err)
	}

	keys, err := def.Keys()
	if err != nil || len(keys) != 5 {
		t.Fatalf("Keys failed: %v, err=%v", keys, err)
	}

	prefixItems, err := def.IterPrefix("str")
	if err != nil || len(prefixItems) != 1 {
		t.Fatalf("IterPrefix failed: %v", err)
	}

	rangeItems, err := def.ScanRange(nil, nil)
	if err != nil || len(rangeItems) != 5 {
		t.Fatalf("ScanRange failed: %v", err)
	}

	delOk, err := def.Delete("strKey")
	if err != nil || !delOk {
		t.Fatalf("Delete failed: %v", err)
	}

	if _, err := def.Clear(); err != nil {
		t.Fatalf("Clear failed: %v", err)
	}
	cntAfter, _ := def.Count()
	if cntAfter != 0 {
		t.Fatalf("expected count 0 after Clear, got %d", cntAfter)
	}

	// 2. NS("custom") 自定义命名空间门面
	userScope := db.NS("users")
	if err := userScope.PutString("user:1", "Alice"); err != nil {
		t.Fatalf("userScope.PutString failed: %v", err)
	}
	userVal, err := userScope.GetString("user:1")
	if err != nil || userVal == nil || *userVal != "Alice" {
		t.Fatalf("userScope.GetString failed: %v", err)
	}
	directVal, _ := db.GetString("users", "user:1")
	if directVal == nil || *directVal != "Alice" {
		t.Fatalf("db.GetString(\"users\") mismatch: %v", directVal)
	}

	// 3. BatchWriter Scope
	err = db.Batch(func(b *BatchWriter) error {
		bScope := b.Default()
		if err := bScope.PutString("batchKey", "batchVal"); err != nil {
			return err
		}
		// 读己所写
		v, err := bScope.GetString("batchKey")
		if err != nil || v == nil || *v != "batchVal" {
			t.Fatalf("batch read-your-own-writes failed: %v, err=%v", v, err)
		}
		return nil
	})
	if err != nil {
		t.Fatalf("Batch failed: %v", err)
	}

	// 4. Snapshot Scope
	snap := db.Snapshot()
	if snap == nil {
		t.Fatalf("Snapshot returned nil")
	}
	defer snap.Close()

	snapScope := snap.Default()
	snapVal, err := snapScope.GetString("batchKey")
	if err != nil || snapVal == nil || *snapVal != "batchVal" {
		t.Fatalf("snapScope.GetString failed: %v, err=%v", snapVal, err)
	}
	if snapCount, _ := snapScope.Count(); snapCount != 1 {
		t.Fatalf("snapScope.Count failed: %d", snapCount)
	}
}
