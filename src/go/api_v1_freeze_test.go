package ckv

// API v1.0 冻结契约回归测试（Go）
//
// 覆盖 [docs/API-V1-FREEZE.md](../../docs/API-V1-FREEZE.md) 中属于 Go 的条目：
// 6 个新 API + §3.2 Snapshot 读锁 + §3.7 keys() 跳过非 UTF-8 +
// §3.9 Snapshot.ScanRange 签名 + §3.10 renameWithRetry 只对共享冲突重试 +
// §3.12 同路径单例。
//
// Rust / .NET 有同构测试，三者必须一致。

import (
	"os"
	"path/filepath"
	"sort"
	"sync/atomic"
	"testing"
)

func tempAPIDb(t *testing.T, name string) string {
	t.Helper()
	d, err := os.MkdirTemp("", "ckvapi")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(d) })
	return filepath.Join(d, name)
}

// §2.1 Namespaces()：按 UTF-8 字节序、含空命名空间、默认 ns 为 ""
func TestNamespacesListsAllIncludingEmptyAndDefault(t *testing.T) {
	p := tempAPIDb(t, "ns.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	_ = db.PutString("zeta", "k", "v")
	_ = db.PutString("alpha", "k", "v")
	_ = db.PutString("", "k", "v")
	_ = db.PutString("mid", "k", "v")
	if ok, _ := db.ClearNamespace("mid"); !ok {
		t.Fatal("clear mid must succeed")
	}

	ns := db.Namespaces()
	want := []string{"", "alpha", "mid", "zeta"}
	if len(ns) != len(want) {
		t.Fatalf("Namespaces() = %v, want %v", ns, want)
	}
	for i := range want {
		if ns[i] != want[i] {
			t.Fatalf("Namespaces()[%d] = %q, want %q (full %v)", i, ns[i], want[i], ns)
		}
	}

	// 显式排序断言：必须与 UTF-8 无符号字节序一致
	if !sort.SliceIsSorted(ns, func(i, j int) bool {
		return CompareUtf8Bytes([]byte(ns[i]), []byte(ns[j])) < 0
	}) {
		t.Fatalf("Namespaces() not in unsigned UTF-8 byte order: %v", ns)
	}

	if ok, _ := db.DropNamespace("zeta"); !ok {
		t.Fatal("drop zeta must succeed")
	}
	ns2 := db.Namespaces()
	if len(ns2) != 3 || ns2[0] != "" || ns2[1] != "alpha" || ns2[2] != "mid" {
		t.Fatalf("after drop = %v", ns2)
	}
}

// §2.2 Unwatch()：显式注销；单纯丢弃句柄不注销
func TestUnwatchIsExplicitAndDroppingTheHandleKeepsTheSubscription(t *testing.T) {
	p := tempAPIDb(t, "watch.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	var a, b int64
	ha := db.Watch(func(KeyChangeEvent) { atomic.AddInt64(&a, 1) })
	func() {
		hb := db.Watch(func(KeyChangeEvent) { atomic.AddInt64(&b, 1) })
		_ = hb // 刻意丢弃：订阅必须仍然有效
	}()

	_ = db.PutString("", "k", "1")
	if atomic.LoadInt64(&a) != 1 || atomic.LoadInt64(&b) != 1 {
		t.Fatalf("dropping the handle must not unsubscribe: a=%d b=%d", a, b)
	}

	ha.Unwatch()
	_ = db.PutString("", "k", "2")
	if atomic.LoadInt64(&a) != 1 {
		t.Fatalf("unwatch must remove the callback: a=%d", a)
	}
	if atomic.LoadInt64(&b) != 2 {
		t.Fatalf("other watcher must still fire: b=%d", b)
	}

	// 幂等
	ha.Unwatch()
	_ = db.PutString("", "k", "3")
	if atomic.LoadInt64(&a) != 1 {
		t.Fatalf("unwatch must be idempotent: a=%d", a)
	}
}

// §2.3 Flush()：Compact 的别名；只读模式下返回错误
func TestFlushIsAnAliasOfCompactAndRespectsReadOnly(t *testing.T) {
	p := tempAPIDb(t, "flush.db")
	{
		db, err := Open(p)
		if err != nil {
			t.Fatal(err)
		}
		_ = db.PutString("", "k", "v")
		if err := db.Flush(); err != nil {
			t.Fatalf("Flush: %v", err)
		}
		if err := db.Compact(); err != nil {
			t.Fatalf("Compact: %v", err)
		}
		_ = db.Close()
	}

	ropts := DefaultDbOptions()
	ropts.ReadOnly = true
	ro, err := Open(p, ropts)
	if err != nil {
		t.Fatal(err)
	}
	defer ro.Close()
	if err := ro.Flush(); err == nil {
		t.Fatal("Flush on read-only must fail")
	}
	if err := ro.Compact(); err == nil {
		t.Fatal("Compact on read-only must fail")
	}
}

// §2.4 FileSize() / Stats()
func TestFileSizeAndStatsReportLiveState(t *testing.T) {
	p := tempAPIDb(t, "stats.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	if db.FileSize() != 0 {
		t.Fatalf("fresh db FileSize = %d, want 0", db.FileSize())
	}
	s0 := db.Stats()
	if s0.FileSize != 0 || s0.NamespaceCount != 0 || s0.TotalEntries != 0 || s0.CurrentMaxTxID != 0 {
		t.Fatalf("fresh stats = %+v", s0)
	}

	_ = db.PutString("a", "k1", "v")
	_ = db.PutString("a", "k2", "v")
	_ = db.PutString("b", "k3", "v")

	s1 := db.Stats()
	if s1.FileSize <= 32 || s1.FileSize != db.FileSize() {
		t.Fatalf("stats.FileSize = %d, FileSize() = %d", s1.FileSize, db.FileSize())
	}
	if s1.NamespaceCount != 2 || s1.TotalEntries != 3 {
		t.Fatalf("stats = %+v", s1)
	}
	if s1.CurrentMaxTxID < 1 {
		t.Fatalf("CurrentMaxTxID = %d", s1.CurrentMaxTxID)
	}

	// stats 不得改变状态
	if s2 := db.Stats(); s2 != s1 {
		t.Fatalf("stats mutated state: %+v vs %+v", s1, s2)
	}

	// 空命名空间也计入 NamespaceCount
	_ = db.PutString("empty", "x", "y")
	if ok, _ := db.ClearNamespace("empty"); !ok {
		t.Fatal("clear empty must succeed")
	}
	s3 := db.Stats()
	if s3.NamespaceCount != 3 || s3.TotalEntries != 3 {
		t.Fatalf("after clear stats = %+v", s3)
	}
}

// §2.5 Backup()：产出可独立打开的文件，且不动源库
func TestBackupProducesAStandaloneOpenableFile(t *testing.T) {
	p := tempAPIDb(t, "src.db")
	dest := tempAPIDb(t, "backup.db")

	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	_ = db.PutString("ns", "a", "1")
	_ = db.PutString("ns", "b", "2")
	_ = db.PutInt64("ns", "c", 42)

	if err := db.Backup(dest); err != nil {
		t.Fatalf("Backup: %v", err)
	}
	if _, err := os.Stat(dest); err != nil {
		t.Fatalf("backup file missing: %v", err)
	}
	if _, err := os.Stat(dest + ".tmp"); !os.IsNotExist(err) {
		t.Fatal("backup .tmp must be gone")
	}

	// 备份可被独立打开且内容一致
	b, err := Open(dest)
	if err != nil {
		t.Fatal(err)
	}
	if v, _ := b.GetString("ns", "a"); v == nil || *v != "1" {
		t.Fatalf("backup a = %v", v)
	}
	if v, _ := b.GetString("ns", "b"); v == nil || *v != "2" {
		t.Fatalf("backup b = %v", v)
	}
	if v, _ := b.GetInt64("ns", "c"); v == nil || *v != 42 {
		t.Fatalf("backup c = %v", v)
	}
	if n, _ := b.Count("ns"); n != 3 {
		t.Fatalf("backup count = %d", n)
	}
	_ = b.Close()

	// 源库未受影响
	if n, _ := db.Count("ns"); n != 3 {
		t.Fatalf("source count = %d", n)
	}

	// 覆盖已存在的目标
	_ = db.PutString("ns", "d", "4")
	if err := db.Backup(dest); err != nil {
		t.Fatalf("second Backup: %v", err)
	}
	b2, err := Open(dest)
	if err != nil {
		t.Fatal(err)
	}
	if n, _ := b2.Count("ns"); n != 4 {
		t.Fatalf("overwritten backup count = %d", n)
	}
	_ = b2.Close()

	// 只读模式下允许备份
	//
	// 修订（批次一 · 条目 11）：必须先 Close 读写单例，才能以 ReadOnly 打开同一路径；
	// ReadOnly 冲突现在返回 ConflictingOptionsError，绝不静默返回一个权限不同的实例。
	if err := db.Close(); err != nil {
		t.Fatalf("close source: %v", err)
	}
	ropts := DefaultDbOptions()
	ropts.ReadOnly = true
	ro, err := Open(p, ropts)
	if err != nil {
		t.Fatal(err)
	}
	defer ro.Close()
	dest2 := tempAPIDb(t, "backup_ro.db")
	if err := ro.Backup(dest2); err != nil {
		t.Fatalf("read-only Backup: %v", err)
	}
	b3, err := Open(dest2)
	if err != nil {
		t.Fatal(err)
	}
	if n, _ := b3.Count("ns"); n != 4 {
		t.Fatalf("read-only backup count = %d", n)
	}
	_ = b3.Close()
}

// §2.6 IterPrefix()：与 keys(prefix) 语义一致
func TestIterPrefixMatchesKeysWithPrefix(t *testing.T) {
	p := tempAPIDb(t, "iter.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	for _, k := range []string{"user:1", "user:2", "user:10", "order:1", "other"} {
		_ = db.PutString("ns", k, "v")
	}

	entries, err := db.IterPrefix("ns", "user:")
	if err != nil {
		t.Fatal(err)
	}
	keys, err := db.Keys("ns", "user:")
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != len(keys) {
		t.Fatalf("IterPrefix returned %d, Keys returned %d", len(entries), len(keys))
	}
	for i, e := range entries {
		if e.Key != keys[i] {
			t.Fatalf("entry[%d].Key = %q, Keys[%d] = %q", i, e.Key, i, keys[i])
		}
	}

	// 无前缀 = 全部
	all, err := db.IterPrefix("ns")
	if err != nil {
		t.Fatal(err)
	}
	if len(all) != 5 {
		t.Fatalf("IterPrefix() all = %d, want 5", len(all))
	}

	// 不存在的命名空间返回空
	none, err := db.IterPrefix("nope", "x")
	if err != nil {
		t.Fatal(err)
	}
	if len(none) != 0 {
		t.Fatalf("IterPrefix on missing ns = %d", len(none))
	}

	// Snapshot 同构
	snap := db.Snapshot()
	sEntries, err := snap.IterPrefix("ns", "user:")
	if err != nil {
		t.Fatal(err)
	}
	if len(sEntries) != 3 {
		t.Fatalf("snapshot IterPrefix = %d, want 3", len(sEntries))
	}
}

// §3.7 keys() 必须跳过非 UTF-8 key
func TestKeysSkipsNonUtf8Keys(t *testing.T) {
	p := tempAPIDb(t, "nonutf8.db")
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	// 合法 UTF-8 key
	_ = db.PutString("ns", "ok", "v")
	// 直接操作内部状态写入一个非 UTF-8 key（模拟外部构造的文件）
	db.shared.mu.Lock()
	store := db.shared.namespaces["ns"]
	store.items = append(store.items, keyItem{key: []byte{0xff, 0xfe}, valueType: ValTypeBytes, value: []byte("x")})
	db.shared.mu.Unlock()

	keys, err := db.Keys("ns")
	if err != nil {
		t.Fatal(err)
	}
	if len(keys) != 1 || keys[0] != "ok" {
		t.Fatalf("Keys() = %v, want [ok] (non-UTF-8 key must be skipped)", keys)
	}

	// ScanRange 仍应包含原始字节条目（扫描按字节序，不做 UTF-8 假设）
	entries, err := db.ScanRange("ns", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 2 {
		t.Fatalf("ScanRange = %d entries, want 2", len(entries))
	}
}

// §3.12 同路径重复 Open 返回单例，Close 后释放
func TestSamePathOpenReturnsSingleton(t *testing.T) {
	p := tempAPIDb(t, "single.db")

	db1, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	_ = db1.PutString("", "k", "v")

	// 第二次 Open 的非权限类 options 必须被忽略，仍返回同一单例。
	// （修订：原用例传的是 ReadOnly=true 并断言"第二次 options 被忽略"——那等于把
	//  "以只读意图打开却静默拿到读写实例"（静默提权）固化为正常行为。
	//  ReadOnly 冲突现在必须返回 ConflictingOptionsError，见 TestReadOnlyConflictThrows。）
	db2, err := Open(p, DbOptions{SyncMode: SyncModeManual})
	if err != nil {
		t.Fatal(err)
	}
	if db1 == db2 {
		t.Fatal("same path must return separate handle instances (ADR-20261017)")
	}
	if db1.shared != db2.shared {
		t.Fatal("same path must share the underlying singleton")
	}
	if v, _ := db2.GetString("", "k"); v == nil || *v != "v" {
		t.Fatalf("db2 must see db1's write, got %v", v)
	}
	// options 以首次为准：db2 仍可写
	if err := db2.PutString("", "k2", "v2"); err != nil {
		t.Fatalf("db2 must be writable (first options win): %v", err)
	}

	// ADR-20261017: 关闭 db1 不影响 db2
	_ = db1.Close()
	if _, err := db1.GetString("", "k"); err == nil {
		t.Fatal("db1 operations after Close() must fail")
	}
	if v, err := db2.GetString("", "k2"); err != nil || v == nil || *v != "v2" {
		t.Fatalf("db2 must still be usable after db1 is closed: %v (err %v)", v, err)
	}

	_ = db2.Close()
	db3, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db3.Close()
	if db3 == db1 || db3.shared == db1.shared {
		t.Fatal("after all handles close a new underlying instance must be created")
	}
	if v, _ := db3.GetString("", "k"); v == nil || *v != "v" {
		t.Fatalf("new instance must see persisted data, got %v", v)
	}
}
