package ckv

import (
	"os"
	"path/filepath"
	"testing"
)

// FIX-22 regression: a handle kept past Close() must not write.
//
// Close() releases the inter-process connection lock (C9), so a write that still
// succeeded afterwards would land in a database that another process may already
// hold, i.e. exactly the two-writer situation the lock exists to prevent. Rust
// (.closed), .NET (_isDisposed) and Kotlin (isDisposed) already refuse; Go and
// Dart silently accepted the write before this fix.
func TestWriteAfterCloseIsRefused(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "closed.db")

	db, err := Open(path)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "before", "1"); err != nil {
		t.Fatalf("put before close: %v", err)
	}
	if err := db.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	before, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}

	if err := db.PutString("", "after", "2"); err == nil {
		t.Fatal("PutString after Close() must fail, got nil")
	} else if dbErr, ok := err.(*DbError); !ok || dbErr.Type != "LockedError" {
		t.Fatalf("PutString after Close() must return LockedError, got %T: %v", err, err)
	}
	if _, err := db.Delete("", "before"); err == nil {
		t.Fatal("Delete after Close() must fail, got nil")
	}
	if err := db.Flush(); err == nil {
		t.Fatal("Flush after Close() must fail, got nil")
	}
	if err := db.Compact(); err == nil {
		t.Fatal("Compact after Close() must fail, got nil")
	}

	// P1-12 读操作在 Close 之后同样必须全部失败，禁止静默读取内存旧状态
	if _, err := db.GetString("", "before"); err == nil {
		t.Fatal("GetString after Close() must fail, got nil")
	} else if dbErr, ok := err.(*DbError); !ok || dbErr.Type != "LockedError" {
		t.Fatalf("GetString after Close() must return LockedError, got %T: %v", err, err)
	}
	if _, err := db.GetBytes("", "before"); err == nil {
		t.Fatal("GetBytes after Close() must fail")
	}
	if _, err := db.ContainsKey("", "before"); err == nil {
		t.Fatal("ContainsKey after Close() must fail")
	}
	if _, err := db.Count(""); err == nil {
		t.Fatal("Count after Close() must fail")
	}
	if _, err := db.IterPrefix(""); err == nil {
		t.Fatal("IterPrefix after Close() must fail")
	}
	if _, err := db.ScanRange("", nil, nil); err == nil {
		t.Fatal("ScanRange after Close() must fail")
	}
	if db.Snapshot() != nil {
		t.Fatal("Snapshot after Close() must return nil")
	}

	after, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat after failed writes: %v", err)
	}
	if after.Size() != before.Size() || !after.ModTime().Equal(before.ModTime()) {
		t.Fatalf("file was modified by refused writes: %d/%v -> %d/%v",
			before.Size(), before.ModTime(), after.Size(), after.ModTime())
	}

	// Close is idempotent.
	if err := db.Close(); err != nil {
		t.Fatalf("second Close: %v", err)
	}

	// A fresh open still sees the pre-close state.
	reopened, err := Open(path)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer reopened.Close()
	if got, _ := reopened.GetString("", "after"); got != nil {
		t.Fatalf("a refused write reached the file: %q", *got)
	}
}

// ADR-20261017: 两个持有者打开同一路径，其中一个 Close 不影响另一个持有者继续读写。
func TestSharedInstance_CloseFromOneHolder_DoesNotCloseOtherHolders(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "shared_handles.db")

	h1, err := Open(path)
	if err != nil {
		t.Fatalf("Open h1: %v", err)
	}
	h2, err := Open(path)
	if err != nil {
		t.Fatalf("Open h2: %v", err)
	}

	if h1 == h2 {
		t.Fatal("Each Open must return a distinct handle wrapper")
	}

	if err := h1.PutString("ns", "k", "v1"); err != nil {
		t.Fatalf("h1 put: %v", err)
	}
	if v, err := h2.GetString("ns", "k"); err != nil || v == nil || *v != "v1" {
		t.Fatalf("h2 read: %v, err %v", v, err)
	}

	// h1 关闭
	if err := h1.Close(); err != nil {
		t.Fatalf("h1 close: %v", err)
	}

	// h1 自身所有操作全部报错
	if _, err := h1.GetString("ns", "k"); err == nil {
		t.Fatal("h1 read after close must fail")
	}
	if err := h1.PutString("ns", "k", "v2"); err == nil {
		t.Fatal("h1 write after close must fail")
	}

	// h2 完全不受影响，依然能读能写
	if v, err := h2.GetString("ns", "k"); err != nil || v == nil || *v != "v1" {
		t.Fatalf("h2 should remain unaffected: %v (err %v)", v, err)
	}
	if err := h2.PutString("ns", "k2", "v2"); err != nil {
		t.Fatalf("h2 put: %v", err)
	}

	// h2 关闭后底座才真正销毁
	if err := h2.Close(); err != nil {
		t.Fatalf("h2 close: %v", err)
	}
	if _, err := h2.GetString("ns", "k2"); err == nil {
		t.Fatal("h2 read after close must fail")
	}
}

// §7.4 静态 BackupFile 测试
func TestStaticBackupFile(t *testing.T) {
	dir := t.TempDir()
	src := filepath.Join(dir, "src.db")
	dst := filepath.Join(dir, "dst.db")

	db, err := Open(src)
	if err != nil {
		t.Fatal(err)
	}
	_ = db.PutString("ns", "hello", "world")
	_ = db.Close()

	if err := BackupFile(src, dst); err != nil {
		t.Fatalf("BackupFile: %v", err)
	}

	reopened, err := Open(dst)
	if err != nil {
		t.Fatalf("Open backup: %v", err)
	}
	defer reopened.Close()

	v, err := reopened.GetString("ns", "hello")
	if err != nil || v == nil || *v != "world" {
		t.Fatalf("BackupFile read = %v, err %v", v, err)
	}
}

func TestIsClosed(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "is_closed.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	if db.IsClosed() {
		t.Fatal("newly opened db should not be closed")
	}
	_ = db.Close()
	if !db.IsClosed() {
		t.Fatal("closed db should report IsClosed() == true")
	}
}

func TestPutBytesDefensiveCopy(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "defensive_copy.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	buf := []byte{10, 20, 30}
	if err := db.PutBytes("ns", "key", buf); err != nil {
		t.Fatal(err)
	}
	buf[0] = 99 // modify outside

	got, err := db.GetBytes("ns", "key")
	if err != nil {
		t.Fatal(err)
	}
	if len(got) == 0 || got[0] != 10 {
		t.Fatalf("expected 10, got %v", got)
	}
}
