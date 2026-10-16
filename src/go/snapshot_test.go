package ckv

import (
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
)

func TestGoSnapshotPointInTimeIsolation(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "ckv_go_snap_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	dbPath := filepath.Join(tempDir, "snap_iso.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	// 1. Initial dataset
	if err := db.PutString("users", "user:1", "Alice"); err != nil {
		t.Fatal(err)
	}
	if err := db.PutInt64("users", "user:1:score", 100); err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("users", "user:2", "Bob"); err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("orders", "order:100", "Pending"); err != nil {
		t.Fatal(err)
	}

	snap1 := db.Snapshot()
	defer snap1.Close()

	if snap1.TxID() == 0 {
		t.Fatalf("Expected snap1 TxID > 0")
	}

	// 2. Extensively mutate primary db
	if err := db.PutString("users", "user:1", "Alice_Updated"); err != nil {
		t.Fatal(err)
	}
	if _, err := db.IncrementInt64("users", "user:1:score", 899); err != nil { // 100 + 899 = 999
		t.Fatal(err)
	}
	if _, err := db.Delete("users", "user:2"); err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("users", "user:3", "Charlie"); err != nil {
		t.Fatal(err)
	}
	if _, err := db.DropNamespace("orders"); err != nil {
		t.Fatal(err)
	}

	// 3. Primary db state check
	sVal, err := db.GetString("users", "user:1")
	if err != nil || sVal == nil || *sVal != "Alice_Updated" {
		t.Fatalf("Unexpected primary val: %v, err: %v", sVal, err)
	}
	iVal, err := db.GetInt64("users", "user:1:score")
	if err != nil || iVal == nil || *iVal != 999 {
		t.Fatalf("Unexpected primary score: %v", iVal)
	}
	u2, _ := db.GetString("users", "user:2")
	if u2 != nil {
		t.Fatalf("Expected user:2 deleted")
	}
	ordCount, _ := db.Count("orders")
	if ordCount != 0 {
		t.Fatalf("Expected orders dropped in primary")
	}

	// 4. Verify snap1 remains completely frozen
	snapU1, err := snap1.GetString("users", "user:1")
	if err != nil || snapU1 == nil || *snapU1 != "Alice" {
		t.Fatalf("Expected Alice in snap1, got %v", snapU1)
	}
	snapScore, err := snap1.GetInt64("users", "user:1:score")
	if err != nil || snapScore == nil || *snapScore != 100 {
		t.Fatalf("Expected 100 in snap1, got %v", snapScore)
	}
	snapU2, err := snap1.GetString("users", "user:2")
	if err != nil || snapU2 == nil || *snapU2 != "Bob" {
		t.Fatalf("Expected Bob in snap1, got %v", snapU2)
	}
	snapU3, err := snap1.GetString("users", "user:3")
	if err != nil || snapU3 != nil {
		t.Fatalf("Expected user:3 not in snap1")
	}
	snapOrdCount, _ := snap1.Count("orders")
	if snapOrdCount != 1 {
		t.Fatalf("Expected orders count 1 in snap1, got %d", snapOrdCount)
	}
	snapOrd, _ := snap1.GetString("orders", "order:100")
	if snapOrd == nil || *snapOrd != "Pending" {
		t.Fatalf("Expected Pending in snap1 orders")
	}

	// 5. Create snap2 and verify it sees latest mutations
	snap2 := db.Snapshot()
	defer snap2.Close()
	if snap2.TxID() <= snap1.TxID() {
		t.Fatalf("Expected snap2 TxID > snap1 TxID")
	}
	snap2U1, _ := snap2.GetString("users", "user:1")
	if snap2U1 == nil || *snap2U1 != "Alice_Updated" {
		t.Fatalf("Expected Alice_Updated in snap2")
	}
	snap2OrdCount, _ := snap2.Count("orders")
	if snap2OrdCount != 0 {
		t.Fatalf("Expected orders count 0 in snap2")
	}
}

func TestGoSnapshotConcurrentReadersAndWriters(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "ckv_go_snap_concur_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	dbPath := filepath.Join(tempDir, "concur.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	for i := 0; i < 100; i++ {
		k := fmt.Sprintf("key:%04d", i)
		v := fmt.Sprintf("val:%04d", i)
		_ = db.PutString("data", k, v)
	}

	snap := db.Snapshot()
	defer snap.Close()

	var stopFlag int32
	var wg sync.WaitGroup

	// Background writer mutating primary DB
	wg.Add(1)
	go func() {
		defer wg.Done()
		counter := 0
		for atomic.LoadInt32(&stopFlag) == 0 {
			k := fmt.Sprintf("key:%04d", counter%100)
			v := fmt.Sprintf("mutated:%d", counter)
			_ = db.PutString("data", k, v)
			counter++
		}
	}()

	// Reader reading frozen snapshot concurrently
	for round := 0; round < 50; round++ {
		start, end := "key:0010", "key:0020"
		entries, err := snap.ScanRange("data", &start, &end)
		if err != nil {
			t.Fatal(err)
		}
		if len(entries) != 10 {
			t.Fatalf("Expected 10 entries in range, got %d", len(entries))
		}
		for i, e := range entries {
			expectedKey := fmt.Sprintf("key:%04d", 10+i)
			expectedVal := fmt.Sprintf("val:%04d", 10+i)
			if e.Key != expectedKey {
				t.Fatalf("Expected key %s, got %s", expectedKey, e.Key)
			}
			s, err := e.AsString()
			if err != nil || s != expectedVal {
				t.Fatalf("Expected val %s, got %s", expectedVal, s)
			}
		}
	}

	atomic.StoreInt32(&stopFlag, 1)
	wg.Wait()

	// Verify snap is still 100
	c, _ := snap.Count("data")
	if c != 100 {
		t.Fatalf("Expected snapshot count 100, got %d", c)
	}
}

func TestGoSnapshotTypeSafety(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "ckv_go_snap_types_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	dbPath := filepath.Join(tempDir, "types.db")
	db, err := Open(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	_ = db.PutBytes("ns", "k_bytes", []byte{1, 2, 3})
	_ = db.PutString("ns", "k_str", "hello")
	_ = db.PutInt64("ns", "k_i64", -123)
	_ = db.PutFloat64("ns", "k_f64", 3.14159)
	_ = db.PutBool("ns", "k_bool", true)

	snap := db.Snapshot()
	defer snap.Close()

	b, _ := snap.GetBytes("ns", "k_bytes")
	if len(b) != 3 || b[0] != 1 {
		t.Fatalf("Unexpected bytes")
	}
	s, _ := snap.GetString("ns", "k_str")
	if s == nil || *s != "hello" {
		t.Fatalf("Unexpected string")
	}
	i, _ := snap.GetInt64("ns", "k_i64")
	if i == nil || *i != -123 {
		t.Fatalf("Unexpected int64")
	}
	f, _ := snap.GetFloat64("ns", "k_f64")
	if f == nil || *f != 3.14159 {
		t.Fatalf("Unexpected float64")
	}
	bo, _ := snap.GetBool("ns", "k_bool")
	if bo == nil || !*bo {
		t.Fatalf("Unexpected bool")
	}

	// Type mismatch intercept
	_, err = snap.GetString("ns", "k_i64")
	if err == nil {
		t.Fatalf("Expected TypeMismatchError")
	}
}
