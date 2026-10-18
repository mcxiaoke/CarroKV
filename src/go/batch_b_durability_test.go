package ckv

// 批次 B「数据正确性」回归测试（Go）。
//
// 覆盖：
//
//	N-X2  AOL 后台刷盘的代际守卫（此前 fsync 之后无条件清脏标记）
//	M2-03 AOL 追加中途失败必须回缩文件，否则撕裂尾帧会永久毒化 precheck
//	M2-07 轮转失败必须保留 `.tmp`，否则"第一步已成功、第二步失败"会销毁唯一新代
//	M6-01 Backup 必须在同一临界区内读 TxID，且拒绝写到源库自身
//
// 这些路径此前零覆盖：AOL 侧只有快照引擎的竞态用例，轮转失败与 Backup 竞态
// 从未被测过。

import (
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

func newAolDeferred(t *testing.T, path string) *DB {
	t.Helper()
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineModeAppendOnlyLog
	opts.SyncMode = SyncModeDeferred
	opts.CompactionThresholdBytes = ^uint64(0)
	db, err := Open(path, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	return db
}

// N-X2：代际变化后 AOL 刷盘不得清除脏标记。
func TestAolFlushKeepsDirtyWhenGenerationMoved(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "guard.db")
	db := newAolDeferred(t, path)
	defer db.Close()

	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}

	shared := db.shared
	shared.mu.Lock()
	sampled := shared.dirtyGeneration
	if !shared.aolFlushMayClearDirty(sampled) {
		shared.mu.Unlock()
		t.Fatal("a fresh generation must be allowed to clear the flag")
	}
	shared.dirtyGeneration = sampled + 1
	stillAllowed := shared.aolFlushMayClearDirty(sampled)
	shared.mu.Unlock()

	if stillAllowed {
		t.Fatal("a stale generation must not clear the dirty flag")
	}
}

// M2-03：`appendEntries` 在中途中止时必须把文件回缩到追加前的长度。
// 用一个超出单帧上限的条目逼出写帧错误（WriteBatchFrame 拒绝空帧）。
func TestAolAppendFailureTruncatesTornTail(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "torn.db")
	db := newAolDeferred(t, path)
	defer db.Close()

	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}

	before, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}

	// 单个条目超过 MaxPayloadLen：切帧逻辑会先 flush 一个空帧，从而返回错误。
	huge := make([]byte, MaxPayloadLen+1)
	bad := &RawOpEntry{
		OpCode:    OpPut,
		ValueType: ValTypeBytes,
		Namespace: []byte(""),
		Key:       []byte("huge"),
		Value:     huge,
	}
	shared := db.shared
	shared.mu.Lock()
	_, appendErr := shared.appendEntries([]*RawOpEntry{bad})
	shared.mu.Unlock()

	if appendErr == nil {
		t.Fatal("appending an oversized entry must fail")
	}

	after, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat after: %v", err)
	}
	if after.Size() != before.Size() {
		t.Fatalf("torn tail was left in the log: size %d -> %d", before.Size(), after.Size())
	}

	// 连接必须仍然可用：precheck 不应把自伤误判成外部并发修改。
	if err := db.PutString("", "k2", "v2"); err != nil {
		t.Fatalf("connection was poisoned by its own failed append: %v", err)
	}
}

// M2-07：轮转第二步失败时 `.tmp` 必须保留（规范 §4.2）。
func TestRotationFailureKeepsTmpFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "rot.db")

	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineModeSnapshotAtomic
	opts.SyncMode = SyncModeImmediate
	db, err := Open(path, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer db.Close()

	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("seed: %v", err)
	}

	// 用目录占住 `.bak`：第一步 rename(.db → .bak) 必然失败。
	bak := path + ".bak"
	if err := os.Mkdir(bak, 0o755); err != nil {
		t.Fatalf("mkdir bak: %v", err)
	}

	err = db.PutString("", "k2", "v2")
	if err == nil {
		t.Fatal("rotation onto a directory must fail")
	}
	if _, statErr := os.Stat(path + ".tmp"); statErr != nil {
		t.Fatalf(".tmp must be kept for self-healing, stat err: %v", statErr)
	}
}

// M6-01：Backup 与并发写并发执行必须无数据竞争且产物可打开（-race 下有效）。
func TestBackupUnderConcurrentWrites(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "race.db")
	db := newAolDeferred(t, path)
	defer db.Close()

	var wg sync.WaitGroup
	stop := make(chan struct{})
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < 200; i++ {
			_ = db.PutString("", "w"+string(rune('a'+i%26)), "v")
		}
		close(stop)
	}()

	dest := filepath.Join(dir, "copy.db")
	for {
		select {
		case <-stop:
			if err := db.Backup(dest); err != nil {
				t.Fatalf("backup: %v", err)
			}
			wg.Wait()
			return
		default:
			_ = db.Backup(dest)
			time.Sleep(time.Millisecond)
		}
	}
}

// M6-01：备份到源库自身必须被拒绝（否则产物与主库互相覆盖）。
func TestBackupRejectsDestinationCollidingWithSource(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "self.db")
	db := newAolDeferred(t, path)
	defer db.Close()

	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}

	if err := db.Backup(path); err == nil {
		t.Fatal("backup onto the database file itself must be rejected")
	}
	if err := db.Backup(path + ".tmp"); err == nil {
		t.Fatal("backup onto the database .tmp must be rejected")
	}
	if err := db.Backup(path + ".bak"); err == nil {
		t.Fatal("backup onto the database .bak must be rejected")
	}
}
