package ckv

// 写路径原子性回归测试（B1 / B2 / B3）
//
// 覆盖 2026-10-16 修复的三个同根因缺陷：写路径原本"先改内存 → 再 flush → flush
// 内部才做只读/锁/磁盘预检"，导致被拒绝的写仍会留下已变更的内存状态。
//
//   - B1：只读实例的写操作返回 LockedError 后，内存必须保持原值
//   - B2：磁盘被外部修改而预检失败时，内存必须保持原值
//   - B3：batch 中对不存在/已空的键与命名空间不得派发幻影 Watcher 事件
//
// Rust / .NET 有同构测试，三者必须保持一致。

import (
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
)

func tempProbePath(t *testing.T, name string) string {
	t.Helper()
	d, err := os.MkdirTemp("", "ckvprobe")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(d) })
	return filepath.Join(d, name)
}

// B1：只读实例执行任何写操作都必须返回错误，且**不得污染内存**。
func TestB1ReadOnlyWriteMustNotMutateMemory(t *testing.T) {
	p := tempProbePath(t, "ro.db")
	{
		db, err := Open(p)
		if err != nil {
			t.Fatal(err)
		}
		if err := db.PutString("", "k", "old"); err != nil {
			t.Fatal(err)
		}
		_ = db.Close()
	}

	ropts := DefaultDbOptions()
	ropts.ReadOnly = true
	ro, err := Open(p, ropts)
	if err != nil {
		t.Fatal(err)
	}

	if err := ro.PutString("", "k", "HACKED"); err == nil {
		t.Fatal("B1: put on read-only instance must fail")
	}
	if v, _ := ro.GetString("", "k"); v == nil || *v != "old" {
		t.Fatalf("B1: read-only put polluted memory, got %v", v)
	}
	if n, _ := ro.Count(""); n != 1 {
		t.Fatalf("B1: count changed after rejected write, got %d", n)
	}
	if has, _ := ro.ContainsKey("", "brand_new"); has {
		t.Fatal("B1: rejected write created a key in memory")
	}

	// 其它写入口同样不得污染
	if err := ro.PutBytes("", "raw", []byte("x")); err == nil {
		t.Fatal("B1: PutBytes on read-only must fail")
	}
	if err := ro.PutInt64("", "n", 1); err == nil {
		t.Fatal("B1: PutInt64 on read-only must fail")
	}
	if err := ro.PutFloat64("", "f", 1.0); err == nil {
		t.Fatal("B1: PutFloat64 on read-only must fail")
	}
	if err := ro.PutBool("", "b", true); err == nil {
		t.Fatal("B1: PutBool on read-only must fail")
	}
	if _, err := ro.IncrementInt64("", "n", 1); err == nil {
		t.Fatal("B1: IncrementInt64 on read-only must fail")
	}
	if _, err := ro.Delete("", "k"); err == nil {
		t.Fatal("B1: Delete on read-only must fail")
	}
	if _, err := ro.DropNamespace(""); err == nil {
		t.Fatal("B1: DropNamespace on read-only must fail")
	}
	if _, err := ro.ClearNamespace(""); err == nil {
		t.Fatal("B1: ClearNamespace on read-only must fail")
	}
	if err := ro.Compact(); err == nil {
		t.Fatal("B1: Compact on read-only must fail")
	}
	if err := ro.Batch(func(b *BatchWriter) error {
		return b.PutString("", "k", "HACKED")
	}); err == nil {
		t.Fatal("B1: Batch on read-only must fail")
	}

	if v, _ := ro.GetString("", "k"); v == nil || *v != "old" {
		t.Fatalf("B1: memory polluted by a later rejected write, got %v", v)
	}
	if n, _ := ro.Count(""); n != 1 {
		t.Fatalf("B1: count changed, got %d", n)
	}
}

// B2：磁盘被外部进程改写后，预检必须失败且不得污染内存。
func TestB2ExternalModificationMustNotMutateMemory(t *testing.T) {
	p := tempProbePath(t, "ext.db")
	other := tempProbePath(t, "other.db")

	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("", "a", "1"); err != nil {
		t.Fatal(err)
	}

	// 用另一个数据库实例构造一份"外部写入"的合法文件
	o, err := Open(other)
	if err != nil {
		t.Fatal(err)
	}
	_ = o.PutString("", "a", "1")
	_ = o.PutString("", "b", "2")
	_ = o.PutString("", "c", "3")
	_ = o.Close()

	// 外部进程直接覆盖磁盘（CKV 平时不持有 .db 句柄，这完全可以发生）
	if err := copyFileRaw(other, p); err != nil {
		t.Fatal(err)
	}

	if err := db.PutString("", "x", "y"); err == nil {
		t.Fatal("B2: expected ConcurrentModificationError, got nil")
	} else if _, ok := err.(*DbError); !ok || err.Error() == "" {
		t.Fatalf("B2: unexpected error %v", err)
	}

	// 关键断言：写被拒绝，内存不得残留 "x"
	if has, _ := db.ContainsKey("", "x"); has {
		t.Fatal("B2: rejected write left 'x' in memory")
	}
	if n, _ := db.Count(""); n != 1 {
		t.Fatalf("B2: count changed after rejected write, got %d", n)
	}
	if v, _ := db.GetString("", "x"); v != nil {
		t.Fatalf("B2: rejected write is readable: %v", *v)
	}
}

// B2 补充：锁被占用时同样不得污染内存。
//
// C9 起锁是"连接生命周期持有的内核锁"，因此"另一个连接正在使用数据库"表现为 **Open
// 被拒绝**（旧协议下表现为写被拒绝）：持锁者必须真的持有内核锁，而不是伪造锁文件内容。
func TestB2LockedMustNotMutateMemory(t *testing.T) {
	p := tempProbePath(t, "locked.db")

	{
		db, err := Open(p)
		if err != nil {
			t.Fatal(err)
		}
		if err := db.PutString("", "k", "old"); err != nil {
			t.Fatal(err)
		}
		if err := db.Close(); err != nil {
			t.Fatal(err)
		}
	}

	// 模拟"另一个连接持锁"：真的取一次 <db>.lock 的内核排他锁
	holder, err := openLockHandle(p+".lock", true)
	if err != nil {
		t.Fatalf("holder must be able to lock: %v", err)
	}

	opts := DefaultDbOptions()
	opts.LockTimeoutMs = 200
	if _, err := Open(p, opts); !IsLocked(err) {
		holderRelease(holder)
		t.Fatalf("expected LockedError while another connection holds the lock, got %v", err)
	}
	holderRelease(holder)

	// 持锁者离开后必须能正常打开，且数据完好
	db, err := Open(p)
	if err != nil {
		t.Fatalf("open after the holder is gone: %v", err)
	}
	defer db.Close()
	if err := db.PutString("", "k", "HACKED"); err != nil {
		t.Fatalf("write after the holder is gone: %v", err)
	}
	if v, _ := db.GetString("", "k"); v == nil || *v != "HACKED" {
		t.Fatalf("expected HACKED, got %v", v)
	}
	if n, _ := db.Count(""); n != 1 {
		t.Fatalf("count changed, got %d", n)
	}
}

func holderRelease(holder *os.File) {
	if holder != nil {
		_ = closeLockHandle(holder)
	}
}

// B3：batch 中对不存在的键/命名空间不得派发幻影事件；对真实生效的操作正常派发。
func TestB3BatchMustNotDispatchPhantomEvents(t *testing.T) {
	p := tempProbePath(t, "batch.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}

	var putCount, delCount, dropCount, clearCount int64
	db.Watch(func(ev KeyChangeEvent) {
		switch ev.Op {
		case OpChangePut:
			atomic.AddInt64(&putCount, 1)
		case OpChangeDelete:
			atomic.AddInt64(&delCount, 1)
		case OpChangeDropNamespace:
			atomic.AddInt64(&dropCount, 1)
		case OpChangeClear:
			atomic.AddInt64(&clearCount, 1)
		}
	})

	// 准备：一个含键的 ns、一个已清空的 ns、一个已删除的 ns
	_ = db.PutString("live", "k", "v")
	_ = db.PutString("emptied", "k", "v")
	if ok, _ := db.ClearNamespace("emptied"); !ok {
		t.Fatal("setup: clear emptied must succeed")
	}
	_ = db.PutString("gone", "k", "v")
	if ok, _ := db.DropNamespace("gone"); !ok {
		t.Fatal("setup: drop gone must succeed")
	}

	before := []int64{
		atomic.LoadInt64(&putCount),
		atomic.LoadInt64(&delCount),
		atomic.LoadInt64(&dropCount),
		atomic.LoadInt64(&clearCount),
	}

	// batch 里全部是 no-op：不得产生任何事件
	if err := db.Batch(func(b *BatchWriter) error {
		_ = b.Delete("live", "does_not_exist")
		_ = b.ClearNamespace("emptied") // 已空
		_ = b.DropNamespace("gone")     // 已删
		_ = b.ClearNamespace("never_existed")
		_ = b.DropNamespace("never_existed")
		_ = b.Delete("never_existed", "k")
		return nil
	}); err != nil {
		t.Fatalf("no-op batch failed: %v", err)
	}

	after := []int64{
		atomic.LoadInt64(&putCount),
		atomic.LoadInt64(&delCount),
		atomic.LoadInt64(&dropCount),
		atomic.LoadInt64(&clearCount),
	}
	for i := range before {
		if before[i] != after[i] {
			t.Fatalf("B3: no-op batch ops dispatched events (put/del/drop/clear %v -> %v)", before, after)
		}
	}

	// 数据未被破坏
	if has, _ := db.ContainsKey("live", "k"); !has {
		t.Fatal("B3: no-op batch destroyed data")
	}

	// 真实生效的操作必须正常派发
	if err := db.Batch(func(b *BatchWriter) error {
		_ = b.Delete("live", "k")
		return nil
	}); err != nil {
		t.Fatalf("effective batch failed: %v", err)
	}
	_ = db.PutString("emptied", "k2", "v")
	if err := db.Batch(func(b *BatchWriter) error {
		_ = b.ClearNamespace("emptied")
		return nil
	}); err != nil {
		t.Fatalf("effective clear batch failed: %v", err)
	}

	if got := atomic.LoadInt64(&delCount) - before[1]; got != 1 {
		t.Fatalf("B3: real batch delete must dispatch exactly 1 event, got %d", got)
	}
	if got := atomic.LoadInt64(&clearCount) - before[3]; got != 1 {
		t.Fatalf("B3: real batch clear must dispatch exactly 1 event, got %d", got)
	}
	if got := atomic.LoadInt64(&dropCount) - before[2]; got != 0 {
		t.Fatalf("B3: no drop happened, got %d events", got)
	}
}

// copyFileRaw 以普通文件拷贝模拟"另一个进程"改写数据库文件。
func copyFileRaw(src, dst string) error {
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, data, 0600)
}
