package ckv

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
	"unicode/utf8"
)

// batchOpClear 是 batch 暂存区的内部操作码，仅存在于内存 Staging 阶段，
// 不进入二进制文件格式（规范仅定义 OpPut/OpDelete/OpDropNamespace 三种磁盘 OpCode）。
const batchOpClear uint8 = 0x80

// Adaptive-compaction thresholds: an under-threshold WAL is compacted once at
// least half of it is dead payload, provided it is large enough for the rewrite
// to be worth its I/O.
const (
	adaptiveMinFileSize  int64 = 2 * 1024 * 1024
	adaptiveDeadRatioPct int64 = 50
)

type keyItem struct {
	key       []byte
	valueType uint8
	value     []byte
}

type namespaceStore struct {
	items []keyItem
	// liveBytes is Σ(len(key) + len(value)) over items. It is maintained by
	// put/delete so the dead-ratio compaction trigger stays O(1) per mutation
	// instead of walking every entry (see maybeCompactAfterWrite).
	liveBytes int64
}

func (ns *namespaceStore) findIndex(key []byte) (int, bool) {
	idx := sort.Search(len(ns.items), func(i int) bool {
		return bytes.Compare(ns.items[i].key, key) >= 0
	})
	if idx < len(ns.items) && bytes.Equal(ns.items[idx].key, key) {
		return idx, true
	}
	return idx, false
}

func (ns *namespaceStore) put(key []byte, vtype uint8, val []byte) {
	idx, found := ns.findIndex(key)
	if found {
		ns.liveBytes += int64(len(val)) - int64(len(ns.items[idx].value))
		ns.items[idx].valueType = vtype
		ns.items[idx].value = val
	} else {
		item := keyItem{key: key, valueType: vtype, value: val}
		ns.items = append(ns.items, keyItem{})
		copy(ns.items[idx+1:], ns.items[idx:])
		ns.items[idx] = item
		ns.liveBytes += int64(len(key) + len(val))
	}
}

func (ns *namespaceStore) delete(key []byte) {
	idx, found := ns.findIndex(key)
	if found {
		ns.liveBytes -= int64(len(ns.items[idx].key) + len(ns.items[idx].value))
		copy(ns.items[idx:], ns.items[idx+1:])
		ns.items = ns.items[:len(ns.items)-1]
	}
}

func (ns *namespaceStore) clone() *namespaceStore {
	newItems := make([]keyItem, len(ns.items))
	copy(newItems, ns.items)
	return &namespaceStore{items: newItems, liveBytes: ns.liveBytes}
}

// recalculateLiveBytes rebuilds the O(1) ledger from items.
//
// Bulk-load paths that append to items directly (the all-PUT fast path in
// applyEntries) bypass put(), so the ledger must be recomputed once afterwards.
// Leaving it at 0 makes every reopened AOL database look like 100% garbage and
// the compaction trigger fires on every write (spec §4.5).
func (ns *namespaceStore) recalculateLiveBytes() {
	var total int64
	for i := range ns.items {
		total += int64(len(ns.items[i].key) + len(ns.items[i].value))
	}
	ns.liveBytes = total
}

// DB 是用户操作数据库的连接句柄（ADR-20261017）。
// 每次 Open 均返回独立的句柄包装，其内部持有对进程内同路径唯一 sharedDB 的引用。
// 单个句柄 Close() 仅关闭当前连接并递减底座引用计数，绝不连带破坏其他活跃句柄。
type DB struct {
	shared *sharedDB
	closed uint32
}

func (db *DB) isClosed() bool {
	if db == nil || db.shared == nil {
		return true
	}
	return atomic.LoadUint32(&db.closed) != 0
}

// IsClosed returns true if this DB handle is closed.
func (db *DB) IsClosed() bool {
	return db.isClosed()
}

func (db *DB) checkClosed() error {
	if db.isClosed() {
		return NewLockedError("Database is closed: operations are refused after close()")
	}
	return nil
}

func (db *DB) checkWritable() error {
	if db.isClosed() {
		return NewLockedError("Database is closed: writes are refused after close()")
	}
	if db.shared.options.ReadOnly {
		return NewLockedError("Database is opened in read-only mode")
	}
	return nil
}

func (db *DB) setFinalizer() {
	runtime.SetFinalizer(db, func(h *DB) {
		if atomic.LoadUint32(&h.closed) == 0 {
			if h.shared != nil && h.shared.options.OnWarning != nil {
				h.shared.options.OnWarning("onFinalizerLeak", "Ckv DB handle was collected without Close()")
			}
			_ = h.Close()
		}
	})
}

// pendingWarning 存储待派发的告警，支持在写锁/读写锁释放后统一派发，消除重入死锁（P1-33）。
type pendingWarning struct {
	code    string
	message string
}

// sharedDB 是进程内同路径唯一的底层物理数据库实例与状态。
type sharedDB struct {
	mu                 sync.RWMutex
	path               string
	canonicalPath      string
	options            DbOptions
	namespaces         map[string]*namespaceStore
	sortedNsNames      []string
	currentMaxTxID     uint64
	snapshotCount      int // 存活快照数量；>0 时写路径执行 CoW 克隆，=0 时原地修改
	lastKnownDiskState struct {
		maxTxID  uint64
		fileSize int64
	}
	watchers      []watcherSlot
	nextWatcherID uint64

	registered bool

	isDirty bool
	closed  bool
	stopCh  chan struct{}
	bgWg    sync.WaitGroup

	flushMu         sync.Mutex
	dirtyGeneration uint64

	needsCompaction              bool
	compactionFailures           uint32
	writesSinceCompactionAttempt uint64

	pendingWarnings []pendingWarning

	fileLock *FileLock

	refCount int
}

func (db *sharedDB) queueWarning(code, msg string) {
	if db.options.OnWarning != nil {
		db.pendingWarnings = append(db.pendingWarnings, pendingWarning{code: code, message: msg})
	}
}

func (db *sharedDB) dispatchPendingWarnings() {
	if len(db.pendingWarnings) == 0 {
		return
	}
	warnings := db.pendingWarnings
	db.pendingWarnings = nil
	for _, w := range warnings {
		if db.options.OnWarning != nil {
			db.options.OnWarning(w.code, w.message)
		}
	}
}

// Open 打开或创建数据库，执行崩溃自愈并做全文件帧校验。
//
// §3.12（API v1.0 冻结）：若同一路径已在本进程打开，返回**同一个实例**
// （进程内单例），options 以首次打开为准——与 Rust / .NET 行为对齐。
// Go 无标准库弱引用，注册表条目由显式 Close() 释放；未 Close 的同路径
// 再次 Open 会拿到旧实例。
// checkOptionsConflict 是 reentrant open 的选项冲突检查（§3.1 冻结）。
//
// **ReadOnly 冲突必须返回错误，不得静默复用。** 命中单例时若只发一条 OnWarning
// 就返回原实例，调用方以 ReadOnly=true 打开却拿到一个读写实例——那是静默提权；
// 而 OnWarning 默认是 nil，默认配置下连告警都看不见。反方向（要写拿到只读实例）
// 同样会让后续写入以 LockedError 的形式在远处失败、误导排查。两个方向都拒绝。
//
// EngineMode / SyncMode 冲突维持告警：它们只造成性能与落盘语义差异，不构成权限问题。
func checkOptionsConflict(existingOpts, newOpts DbOptions) error {
	if existingOpts.ReadOnly != newOpts.ReadOnly {
		return NewConflictingOptionsError(fmt.Sprintf(
			"readOnly conflict on reentrant open: existing=%v, requested=%v. "+
				"The in-process singleton is returned unchanged by contract, so it cannot honour "+
				"the requested permission. Close the other handle first, or open a different path.",
			existingOpts.ReadOnly, newOpts.ReadOnly))
	}

	var conflicts []string
	if existingOpts.EngineMode != newOpts.EngineMode {
		conflicts = append(conflicts, fmt.Sprintf("engineMode (existing=%v, requested=%v)", existingOpts.EngineMode, newOpts.EngineMode))
	}
	if existingOpts.SyncMode != newOpts.SyncMode {
		conflicts = append(conflicts, fmt.Sprintf("syncMode (existing=%v, requested=%v)", existingOpts.SyncMode, newOpts.SyncMode))
	}
	if len(conflicts) > 0 {
		msg := fmt.Sprintf("Options conflict on reentrant open: %s. Using existing singleton options.", strings.Join(conflicts, ", "))
		if newOpts.OnWarning != nil {
			newOpts.OnWarning("onOptionsConflict", msg)
		} else if existingOpts.OnWarning != nil {
			existingOpts.OnWarning("onOptionsConflict", msg)
		}
	}
	return nil
}

// BackupFile 是 §7.4 静态备份接口。
// 以只读模式打开 source 数据库，将其一致性快照原子备份到 dest，完成后自动关闭连接。
func BackupFile(source, dest string, options ...DbOptions) error {
	opts := DefaultDbOptions()
	if len(options) > 0 {
		opts = options[0]
	}
	opts.ReadOnly = true
	db, err := Open(source, opts)
	if err != nil {
		return err
	}
	defer db.Close()
	return db.Backup(dest)
}

func Open(path string, options ...DbOptions) (*DB, error) {
	opts := DefaultDbOptions()
	if len(options) > 0 {
		opts = options[0]
	}

	canonicalPath, err := filepath.Abs(filepath.Clean(path))
	if err != nil {
		canonicalPath = filepath.Clean(path)
	}

	registryMu.Lock()
	if existing, ok := openRegistry[canonicalPath]; ok && existing != nil {
		if err := checkOptionsConflict(existing.options, opts); err != nil {
			registryMu.Unlock()
			return nil, err
		}
		existing.refCount++
		registryMu.Unlock()

		h := &DB{shared: existing}
		h.setFinalizer()
		return h, nil
	}
	registryMu.Unlock()

	db := &sharedDB{
		path:          path,
		canonicalPath: canonicalPath,
		options:       opts,
		namespaces:    make(map[string]*namespaceStore),
		sortedNsNames: make([]string, 0),
		refCount:      1,
	}

	// C9: 跨进程内核锁在任何磁盘动作之前获取，并持有整个连接生命周期。
	lock, err := AcquireLock(db.path, !db.options.ReadOnly, time.Duration(db.options.LockTimeoutMs)*time.Millisecond)
	if err != nil {
		return nil, err
	}
	db.fileLock = lock

	if err := db.initAndRecover(); err != nil {
		db.fileLock.Release()
		db.fileLock = nil
		return nil, err
	}

	registryMu.Lock()
	if existing, ok := openRegistry[canonicalPath]; ok && existing != nil {
		registryMu.Unlock()
		db.fileLock.Release()
		db.fileLock = nil
		if err := checkOptionsConflict(existing.options, opts); err != nil {
			return nil, err
		}
		registryMu.Lock()
		existing.refCount++
		registryMu.Unlock()

		h := &DB{shared: existing}
		h.setFinalizer()
		return h, nil
	}
	openRegistry[canonicalPath] = db
	db.registered = true
	registryMu.Unlock()

	if db.options.SyncMode == SyncModeDeferred && !db.options.ReadOnly {
		db.stopCh = make(chan struct{})
		db.bgWg.Add(1)
		interval := time.Duration(db.options.SyncIntervalMs) * time.Millisecond
		if interval <= 0 {
			interval = 100 * time.Millisecond
		}
		stopCh := db.stopCh
		go func(stop <-chan struct{}) {
			defer db.bgWg.Done()
			ticker := time.NewTicker(interval)
			defer ticker.Stop()
			for {
				select {
				case <-stop:
					return
				case <-ticker.C:
					db.flushIfDirty()
				}
			}
		}(stopCh)
	}

	h := &DB{shared: db}
	h.setFinalizer()
	return h, nil
}

// C16: 后台刷盘执行两阶段落盘，避免长时间持有排他写锁阻塞并发读者
func (db *sharedDB) flushIfDirty() {
	// FIX-01：SnapshotAtomic 的后台刷盘会写 `.tmp`，必须与显式路径互斥。
	// AOL 只做 fsync，不碰 `.tmp`，因此不取门（也就不会与持 mu 的压实路径形成循环等待）。
	if db.options.EngineMode == StorageEngineModeSnapshotAtomic {
		db.flushMu.Lock()
		defer db.flushMu.Unlock()
	}

	db.mu.RLock()
	if !db.isDirty || db.options.ReadOnly {
		db.mu.RUnlock()
		return
	}
	engineMode := db.options.EngineMode
	path := db.path
	if engineMode == StorageEngineModeAppendOnlyLog {
		db.mu.RUnlock()
		if f, err := os.OpenFile(path, os.O_WRONLY, 0666); err == nil {
			_ = f.Sync()
			_ = f.Close()
		}
		db.mu.Lock()
		if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
			db.isDirty = false
		}
		db.mu.Unlock()
		return
	}

	diskMaxTxID, err := db.precheckDiskState()
	if err != nil {
		db.mu.RUnlock()
		return
	}
	finalMaxTxID := db.currentMaxTxID
	if diskMaxTxID > finalMaxTxID {
		finalMaxTxID = diskMaxTxID
	}
	generation := db.dirtyGeneration
	clonedSortedNs := make([]string, len(db.sortedNsNames))
	copy(clonedSortedNs, db.sortedNsNames)
	clonedNamespaces := make(map[string]*namespaceStore, len(db.namespaces))
	for _, ns := range clonedSortedNs {
		clonedNamespaces[ns] = db.namespaces[ns].clone()
	}
	db.mu.RUnlock()

	// 锁外执行磁盘写入与 fsync (C16)
	tmpPath := path + ".tmp"
	bakPath := path + ".bak"
	writtenMaxTxID, err := serializeSnapshotToTmp(tmpPath, clonedSortedNs, clonedNamespaces, finalMaxTxID)
	if err != nil {
		return
	}

	// 短写锁完成原子轮转与元数据更新
	db.mu.Lock()
	defer db.mu.Unlock()
	// FIX-01：快照陈旧时丢弃本次 `.tmp`，不轮转、不清脏标记、不动磁盘账本。
	if db.options.ReadOnly || !db.isDirty || db.dirtyGeneration != generation {
		_ = os.Remove(tmpPath)
		return
	}
	if _, err := os.Stat(db.path); err == nil {
		if err := renameWithRetry(db.path, bakPath); err != nil {
			_ = os.Remove(tmpPath)
			return
		}
	}
	if err := renameWithRetry(tmpPath, db.path); err != nil {
		return
	}
	fsyncParentDir(db.path)

	if writtenMaxTxID > db.currentMaxTxID {
		db.currentMaxTxID = writtenMaxTxID
	}
	if fi, statErr := os.Stat(db.path); statErr == nil {
		db.lastKnownDiskState.fileSize = fi.Size()
	}
	db.lastKnownDiskState.maxTxID = db.currentMaxTxID
	db.isDirty = false
}

// Close 关闭当前句柄并递减底层共享实例引用计数（ADR-20261017）。
// 仅当最后一个句柄被关闭时，才真正执行最终落盘与释放跨进程内核锁。幂等。
func (db *DB) Close() error {
	if db == nil {
		return nil
	}
	if !atomic.CompareAndSwapUint32(&db.closed, 0, 1) {
		return nil
	}
	runtime.SetFinalizer(db, nil)
	if db.shared == nil {
		return nil
	}
	return db.shared.release()
}

func (db *sharedDB) release() error {
	registryMu.Lock()
	db.refCount--
	if db.refCount > 0 {
		registryMu.Unlock()
		return nil
	}
	if openRegistry[db.canonicalPath] == db {
		delete(openRegistry, db.canonicalPath)
	}
	db.registered = false
	registryMu.Unlock()

	return db.closeUnderlying()
}

func (db *sharedDB) closeUnderlying() error {
	db.mu.Lock()
	if db.closed {
		db.mu.Unlock()
		return nil
	}
	stop := db.stopCh
	db.stopCh = nil
	db.mu.Unlock()
	if stop != nil {
		close(stop)
		db.bgWg.Wait()
	}

	db.flushMu.Lock()
	defer db.flushMu.Unlock()

	db.mu.Lock()
	defer db.mu.Unlock()
	var finalFlushErr error
	if db.isDirty && !db.options.ReadOnly {
		if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
			if f, err := os.Open(db.path); err == nil {
				_ = f.Sync()
				_ = f.Close()
			}
			db.isDirty = false
		} else if diskMaxTxID, err := db.precheckDiskState(); err == nil {
			finalFlushErr = db.commitFlush(diskMaxTxID)
		} else {
			finalFlushErr = err
		}
		if finalFlushErr != nil && db.options.OnWarning != nil {
			db.options.OnWarning("onFinalFlushFailed",
				"Final flush during Close failed; pending writes may be lost: "+finalFlushErr.Error())
		}
	}

	db.retryPendingCompaction()

	if db.fileLock != nil {
		db.fileLock.Release()
		db.fileLock = nil
	}

	db.watchers = nil
	db.closed = true
	return finalFlushErr
}

// watcherSlot 让回调可被精确注销（API v1.0 §2.2）。
type watcherSlot struct {
	id uint64
	cb KeyChangeCallback
}

// Watcher 是 Watch 返回的订阅句柄。仅显式 Unwatch() 才移除回调，
// 单纯丢弃句柄不注销（与 Rust/.NET 一致，API v1.0 §2.2）。
type Watcher struct {
	shared *sharedDB
	id     uint64
}

// Unwatch 移除关联的回调。幂等。
func (w *Watcher) Unwatch() {
	if w == nil || w.shared == nil {
		return
	}
	w.shared.mu.Lock()
	out := w.shared.watchers[:0]
	for _, slot := range w.shared.watchers {
		if slot.id != w.id {
			out = append(out, slot)
		}
	}
	w.shared.watchers = out
	w.shared.mu.Unlock()
}

// Id 返回该订阅在实例内的 id。
func (w *Watcher) Id() uint64 { return w.id }

// openRegistry 是同路径单例注册表（ADR-20261017）。
var (
	registryMu   sync.Mutex
	openRegistry = make(map[string]*sharedDB)
)

func (db *sharedDB) GetBytes(ns, key string) ([]byte, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	res := make([]byte, len(store.items[idx].value))
	copy(res, store.items[idx].value)
	return res, nil
}

func (db *sharedDB) GetString(ns, key string) (*string, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	item := store.items[idx]
	if item.valueType != ValTypeString {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected string", key, item.valueType))
	}
	if !utf8.Valid(item.value) {
		return nil, NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 in key '%s'", key))
	}
	s := string(item.value)
	return &s, nil
}

func (db *sharedDB) ContainsKey(ns, key string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return false, nil
	}
	_, found := store.findIndex([]byte(key))
	return found, nil
}

func (db *sharedDB) Count(ns string) (int, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return 0, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return 0, nil
	}
	return len(store.items), nil
}

func (db *sharedDB) putHelper(ns string, key []byte, valType uint8, val []byte) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	if err := validateLengths(string(key), val); err != nil {
		return err
	}

	return db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		recordRollback(normNs, db.namespaces, rollbackStores, rollbackExisted)

		entry := &RawOpEntry{
			OpCode:    OpPut,
			ValueType: valType,
			Namespace: []byte(normNs),
			Key:       key,
			Value:     val,
		}
		db.putInternal(normNs, key, valType, val, clonedInTx)

		ev := KeyChangeEvent{
			Op:        OpChangePut,
			Namespace: normNs,
			Key:       string(key),
			ValueType: valType,
		}
		return []*RawOpEntry{entry}, []KeyChangeEvent{ev}, true, nil
	})
}

func (db *sharedDB) PutBytes(ns, key string, val []byte) error {
	var copied []byte
	if val != nil {
		copied = make([]byte, len(val))
		copy(copied, val)
	}
	return db.putHelper(ns, []byte(key), ValTypeBytes, copied)
}

func (db *sharedDB) PutString(ns, key, val string) error {
	return db.putHelper(ns, []byte(key), ValTypeString, []byte(val))
}

func (db *sharedDB) PutInt64(ns, key string, val int64) error {
	return db.putHelper(ns, []byte(key), ValTypeInt64, EncodeInt64(val))
}

func (db *sharedDB) GetInt64(ns, key string) (*int64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeInt64 {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected int64", key, store.items[idx].valueType))
	}
	v, err := DecodeInt64(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (db *sharedDB) PutFloat64(ns, key string, val float64) error {
	return db.putHelper(ns, []byte(key), ValTypeFloat64, EncodeFloat64(val))
}

func (db *sharedDB) GetFloat64(ns, key string) (*float64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeFloat64 {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected float64", key, store.items[idx].valueType))
	}
	v, err := DecodeFloat64(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (db *sharedDB) PutBool(ns, key string, val bool) error {
	return db.putHelper(ns, []byte(key), ValTypeBool, EncodeBool(val))
}

func (db *sharedDB) GetBool(ns, key string) (*bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeBool {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected bool", key, store.items[idx].valueType))
	}
	v, err := DecodeBool(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (db *sharedDB) IncrementInt64(ns, key string, delta int64) (int64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return 0, err
	}
	keyBytes := []byte(key)
	if err := validateLengths(key, []byte{0, 0, 0, 0, 0, 0, 0, 0}); err != nil {
		return 0, err
	}

	var newVal int64
	err = db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		store, ok := db.namespaces[normNs]
		if ok {
			idx, found := store.findIndex(keyBytes)
			if found {
				if store.items[idx].valueType != ValTypeInt64 {
					return nil, nil, false, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected int64", key, store.items[idx].valueType))
				}
				curr, err := DecodeInt64(store.items[idx].value)
				if err != nil {
					return nil, nil, false, err
				}
				newVal = curr + delta
			} else {
				newVal = delta
			}
		} else {
			newVal = delta
		}

		recordRollback(normNs, db.namespaces, rollbackStores, rollbackExisted)

		encoded := EncodeInt64(newVal)
		entry := &RawOpEntry{
			OpCode:    OpPut,
			ValueType: ValTypeInt64,
			Namespace: []byte(normNs),
			Key:       keyBytes,
			Value:     encoded,
		}
		db.putInternal(normNs, keyBytes, ValTypeInt64, encoded, clonedInTx)

		ev := KeyChangeEvent{
			Op:        OpChangePut,
			Namespace: normNs,
			Key:       key,
			ValueType: ValTypeInt64,
		}
		return []*RawOpEntry{entry}, []KeyChangeEvent{ev}, true, nil
	})
	if err != nil {
		return 0, err
	}
	return newVal, nil
}

// Delete 单条删除指定键，返回是否实际删除了键（规范 §5.2.2）。
func (db *sharedDB) Delete(ns, key string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var deleted bool
	err = db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		store, ok := db.namespaces[normNs]
		if !ok {
			return nil, nil, false, nil
		}
		if _, found := store.findIndex([]byte(key)); !found {
			return nil, nil, false, nil
		}

		recordRollback(normNs, db.namespaces, rollbackStores, rollbackExisted)
		deleted = true

		if clonedInTx != nil {
			if !clonedInTx[normNs] {
				store = store.clone()
				db.namespaces[normNs] = store
				clonedInTx[normNs] = true
			}
		} else {
			store = store.clone()
			db.namespaces[normNs] = store
		}
		store.delete([]byte(key))

		entry := &RawOpEntry{
			OpCode:    OpDelete,
			ValueType: ValTypeBytes,
			Namespace: []byte(normNs),
			Key:       []byte(key),
			Value:     nil,
		}
		ev := KeyChangeEvent{
			Op:        OpChangeDelete,
			Namespace: normNs,
			Key:       key,
			ValueType: 0,
		}
		return []*RawOpEntry{entry}, []KeyChangeEvent{ev}, true, nil
	})
	if err != nil {
		return false, err
	}
	return deleted, nil
}

// DropNamespace 物理彻底删除指定命名空间，返回是否命中存在（规范 §5.2.2）。
// 按规范 §5.2.3 发送单条聚合事件（Op=DropNamespace，Key 为空串）。
func (db *sharedDB) DropNamespace(ns string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var dropped bool
	err = db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		if _, ok := db.namespaces[normNs]; !ok {
			return nil, nil, false, nil
		}

		recordRollback(normNs, db.namespaces, rollbackStores, rollbackExisted)
		dropped = true

		delete(db.namespaces, normNs)
		db.rebuildSortedNs()

		entry := &RawOpEntry{
			OpCode:    OpDropNamespace,
			ValueType: ValTypeBytes,
			Namespace: []byte(normNs),
			Key:       nil,
			Value:     nil,
		}
		ev := KeyChangeEvent{
			Op:        OpChangeDropNamespace,
			Namespace: normNs,
			Key:       "",
			ValueType: 0,
		}
		return []*RawOpEntry{entry}, []KeyChangeEvent{ev}, true, nil
	})
	if err != nil {
		return false, err
	}
	return dropped, nil
}

// ClearNamespace 清空命名空间内的所有条目但保留命名空间自身，
// 返回是否实际清空（命名空间存在且非空）（规范 §5.2.2）。
// 按规范 §5.2.3 发送单条聚合事件（Op=Clear，Key 为空串）。
func (db *sharedDB) ClearNamespace(ns string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var cleared bool
	err = db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		store, ok := db.namespaces[normNs]
		if !ok || len(store.items) == 0 {
			return nil, nil, false, nil
		}

		recordRollback(normNs, db.namespaces, rollbackStores, rollbackExisted)
		cleared = true

		var entries []*RawOpEntry
		for _, item := range store.items {
			entries = append(entries, &RawOpEntry{
				OpCode:    OpDelete,
				ValueType: ValTypeBytes,
				Namespace: []byte(normNs),
				Key:       item.key,
				Value:     nil,
			})
		}

		db.namespaces[normNs] = &namespaceStore{items: make([]keyItem, 0)}
		clonedInTx[normNs] = true

		ev := KeyChangeEvent{
			Op:        OpChangeClear,
			Namespace: normNs,
			Key:       "",
			ValueType: 0,
		}
		return entries, []KeyChangeEvent{ev}, true, nil
	})
	if err != nil {
		return false, err
	}
	return cleared, nil
}

// Watch 注册数据变更回调，返回订阅句柄（API v1.0 §2.2）。
// 仅显式 (*Watcher).Unwatch() 才移除；丢弃句柄不注销。
func (db *sharedDB) Watch(cb KeyChangeCallback) *Watcher {
	db.mu.Lock()
	defer db.mu.Unlock()
	db.nextWatcherID++
	id := db.nextWatcherID
	db.watchers = append(db.watchers, watcherSlot{id: id, cb: cb})
	return &Watcher{shared: db, id: id}
}

// Namespaces 按 UTF-8 无符号字节序返回所有命名空间（API v1.0 §2.1）。
// 默认命名空间为 ""；clear_namespace 之后的空命名空间仍然列出。
// 非 UTF-8 的命名空间名（只能由外部文件引入）跳过。
func (db *sharedDB) Namespaces() []string {
	db.mu.RLock()
	defer db.mu.RUnlock()
	names := make([]string, 0, len(db.namespaces))
	for ns := range db.namespaces {
		if utf8.ValidString(ns) {
			names = append(names, ns)
		}
	}
	sort.Slice(names, func(i, j int) bool {
		return CompareUtf8Bytes([]byte(names[i]), []byte(names[j])) < 0
	})
	return names
}

// FileSize 返回数据库文件当前物理字节数；尚未落盘的新库返回 0（API v1.0 §2.4）。
func (db *sharedDB) FileSize() int64 {
	fi, err := os.Stat(db.path)
	if err != nil {
		return 0
	}
	return fi.Size()
}

// DbStats 是 Stats() 返回的聚合统计（API v1.0 §2.4）。
type DbStats struct {
	FileSize       int64
	NamespaceCount int
	TotalEntries   int
	CurrentMaxTxID uint64
}

// Stats 返回纯内存统计，不触发磁盘 I/O、不修改状态。
func (db *sharedDB) Stats() DbStats {
	db.mu.RLock()
	defer db.mu.RUnlock()
	total := 0
	for _, store := range db.namespaces {
		total += len(store.items)
	}
	return DbStats{
		FileSize:       db.FileSize(),
		NamespaceCount: len(db.namespaces),
		TotalEntries:   total,
		CurrentMaxTxID: db.currentMaxTxID,
	}
}

// Backup 把当前内存状态写入 dest 作为独立数据库文件（API v1.0 §2.5）。
// 产出文件与正常库字节兼容，可被任意 CKV 实现打开。
// 经 dest+".tmp" + fsync + 原子 rename 完成，不触碰源库的 .bak。
// 只读模式下允许执行：备份不修改源库。
func (db *sharedDB) Backup(dest string) error {
	var entries []*RawOpEntry
	db.mu.RLock()
	for _, ns := range db.sortedNsNames {
		store := db.namespaces[ns]
		for _, item := range store.items {
			entries = append(entries, &RawOpEntry{
				OpCode:    OpPut,
				ValueType: item.valueType,
				Namespace: []byte(ns),
				Key:       item.key,
				Value:     item.value,
			})
		}
	}
	db.mu.RUnlock()

	tmpPath := dest + ".tmp"
	tmpFile, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0666)
	if err != nil {
		return err
	}
	header := CreateFileHeader()
	if _, err := tmpFile.Write(header[:]); err != nil {
		_ = tmpFile.Close()
		return err
	}

	finalMaxTxID := db.currentMaxTxID
	if len(entries) > 0 {
		currentTxID := finalMaxTxID + 1
		var frame []*RawOpEntry
		payloadLen := 0
		for _, entry := range entries {
			entrySize := entry.EncodedLen()
			if len(frame) >= MaxOpsPerFrame || (payloadLen+entrySize > MaxPayloadLen) {
				if err := WriteBatchFrame(tmpFile, currentTxID, frame); err != nil {
					_ = tmpFile.Close()
					return err
				}
				currentTxID++
				frame = frame[:0]
				payloadLen = 0
			}
			payloadLen += entrySize
			frame = append(frame, entry)
		}
		if len(frame) > 0 {
			if err := WriteBatchFrame(tmpFile, currentTxID, frame); err != nil {
				_ = tmpFile.Close()
				return err
			}
			finalMaxTxID = currentTxID
		}
	}
	_ = finalMaxTxID

	_ = tmpFile.Sync()
	_ = tmpFile.Close()
	return renameWithRetry(tmpPath, dest)
}

// Flush 显式把内存状态落盘（API v1.0 §2.3）。
// 在 Immediate 模式下做全量紧凑压实；在 Deferred/Manual 模式下落盘未提交的脏数据。
func (db *sharedDB) Flush() error {
	// FIX-01：与后台刷盘共用 `.tmp`（锁序：flushMu -> mu）。
	db.flushMu.Lock()
	defer db.flushMu.Unlock()
	db.mu.Lock()
	defer db.mu.Unlock()
	if db.closed {
		return NewLockedError("Database is closed: writes are refused after Close()")
	}
	if db.options.ReadOnly {
		return NewLockedError("Database is opened in read-only mode")
	}
	if !db.isDirty {
		if _, err := os.Stat(db.path); err == nil {
			return nil
		}
	}
	if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
		if f, err := os.Open(db.path); err == nil {
			_ = f.Sync()
			_ = f.Close()
		}
		db.isDirty = false
		return nil
	}
	diskMaxTxID, err := db.precheckDiskState()
	if err != nil {
		return err
	}
	return db.commitFlush(diskMaxTxID)
}

// IterPrefix 按 UTF-8 无符号字节序返回匹配前缀的条目（API v1.0 §2.6）。
// 缓冲式语义：内部一次性收集后逐个返回（三语言一致的刻意选择）。
func (db *sharedDB) IterPrefix(ns string, prefix ...string) ([]Entry, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	db.mu.RLock()
	defer db.mu.RUnlock()
	store, ok := db.namespaces[normNs]
	if !ok {
		return []Entry{}, nil
	}
	var pfx []byte
	if len(prefix) > 0 && prefix[0] != "" {
		pfx = []byte(prefix[0])
	}
	result := make([]Entry, 0)
	for _, item := range store.items {
		if len(pfx) == 0 || bytes.HasPrefix(item.key, pfx) {
			raw := make([]byte, len(item.value))
			copy(raw, item.value)
			result = append(result, Entry{
				Namespace: normNs,
				Key:       string(item.key),
				ValueType: item.valueType,
				RawBytes:  raw,
			})
		}
	}
	return result, nil
}

// SnapshotNamespaces 与 DB.Namespaces 同构（API v1.0 §2.1）。
func (s *Snapshot) Namespaces() []string {
	names := make([]string, 0, len(s.namespaces))
	for ns := range s.namespaces {
		if utf8.ValidString(ns) {
			names = append(names, ns)
		}
	}
	sort.Slice(names, func(i, j int) bool {
		return CompareUtf8Bytes([]byte(names[i]), []byte(names[j])) < 0
	})
	return names
}

// SnapshotIterPrefix 与 DB.IterPrefix 同构（API v1.0 §2.6）。
func (s *Snapshot) IterPrefix(ns string, prefix ...string) ([]Entry, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return []Entry{}, nil
	}
	var pfx []byte
	if len(prefix) > 0 && prefix[0] != "" {
		pfx = []byte(prefix[0])
	}
	result := make([]Entry, 0)
	for _, item := range store.items {
		if len(pfx) == 0 || bytes.HasPrefix(item.key, pfx) {
			raw := make([]byte, len(item.value))
			copy(raw, item.value)
			result = append(result, Entry{
				Namespace: normNs,
				Key:       string(item.key),
				ValueType: item.valueType,
				RawBytes:  raw,
			})
		}
	}
	return result, nil
}

func (db *sharedDB) Keys(ns string, prefix ...string) ([]string, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return []string{}, nil
	}

	var pfx []byte
	if len(prefix) > 0 && prefix[0] != "" {
		pfx = []byte(prefix[0])
	}

	// §3.7（API v1.0 冻结）：非 UTF-8 字节构成的 key 只能由外部构造的文件引入，
	// 无法用 string 安全表达，统一跳过（Rust 既有行为，.NET 对齐）。
	// Go 原先原样转换，会让调用方拿到无法 round-trip 的 key。
	var keys []string
	for _, item := range store.items {
		if len(pfx) == 0 || bytes.HasPrefix(item.key, pfx) {
			if utf8.Valid(item.key) {
				keys = append(keys, string(item.key))
			}
		}
	}
	return keys, nil
}

func (db *sharedDB) ScanRange(ns string, startKey, endKey *string) ([]Entry, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}

	db.mu.RLock()
	defer db.mu.RUnlock()

	store, ok := db.namespaces[normNs]
	if !ok {
		return []Entry{}, nil
	}

	var startBytes, endBytes []byte
	if startKey != nil {
		startBytes = []byte(*startKey)
	}
	if endKey != nil {
		endBytes = []byte(*endKey)
	}

	if startBytes != nil && endBytes != nil && bytes.Compare(startBytes, endBytes) >= 0 {
		return []Entry{}, nil
	}

	startIdx := 0
	if startBytes != nil {
		startIdx = sort.Search(len(store.items), func(i int) bool {
			return bytes.Compare(store.items[i].key, startBytes) >= 0
		})
	}

	var entries []Entry
	for i := startIdx; i < len(store.items); i++ {
		item := store.items[i]
		if endBytes != nil && bytes.Compare(item.key, endBytes) >= 0 {
			break
		}
		raw := make([]byte, len(item.value))
		copy(raw, item.value)
		entries = append(entries, Entry{
			Namespace: normNs,
			Key:       string(item.key),
			ValueType: item.valueType,
			RawBytes:  raw,
		})
	}

	return entries, nil
}

type BatchWriter struct {
	db  *DB
	ops []batchOp
}

type batchOp struct {
	opType    uint8
	namespace string
	key       []byte
	valType   uint8
	val       []byte
}

func (bw *BatchWriter) PutBytes(ns, key string, val []byte) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	if err := validateLengths(key, val); err != nil {
		return err
	}
	var copied []byte
	if val != nil {
		copied = make([]byte, len(val))
		copy(copied, val)
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeBytes,
		val:       copied,
	})
	return nil
}

func (bw *BatchWriter) PutString(ns, key, val string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	valBytes := []byte(val)
	if err := validateLengths(key, valBytes); err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeString,
		val:       valBytes,
	})
	return nil
}

func (bw *BatchWriter) PutInt64(ns, key string, val int64) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bytes := EncodeInt64(val)
	if err := validateLengths(key, bytes); err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeInt64,
		val:       bytes,
	})
	return nil
}

func (bw *BatchWriter) PutFloat64(ns, key string, val float64) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bytes := EncodeFloat64(val)
	if err := validateLengths(key, bytes); err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeFloat64,
		val:       bytes,
	})
	return nil
}

func (bw *BatchWriter) PutBool(ns, key string, val bool) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bytes := EncodeBool(val)
	if err := validateLengths(key, bytes); err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeBool,
		val:       bytes,
	})
	return nil
}

func (bw *BatchWriter) Delete(ns, key string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpDelete,
		namespace: normNs,
		key:       []byte(key),
	})
	return nil
}

func (bw *BatchWriter) DropNamespace(ns string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    OpDropNamespace,
		namespace: normNs,
	})
	return nil
}

// ClearNamespace 暂存"清空命名空间"操作（规范 §5.2.2 batch 契约）。
// 该操作仅在内存 Staging 区存在，提交时合入主状态，不产生磁盘 OpCode。
func (bw *BatchWriter) ClearNamespace(ns string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	bw.ops = append(bw.ops, batchOp{
		opType:    batchOpClear,
		namespace: normNs,
	})
	return nil
}

// GetBytes reads a key as this batch sees it: the most recent staged operation
// wins, a staged tombstone (Delete / ClearNamespace / DropNamespace) reports
// "absent" even when the database still holds the key, and only a key untouched
// by this batch falls through to the committed state.
//
// The fallback takes the database read lock, so it observes commits made by
// other goroutines during the batch. Callers that need a frozen base can take a
// Snapshot first.
func (bw *BatchWriter) GetBytes(ns, key string) ([]byte, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	keyBytes := []byte(key)

	for i := len(bw.ops) - 1; i >= 0; i-- {
		op := bw.ops[i]
		if op.namespace != normNs {
			continue
		}
		switch op.opType {
		case OpPut:
			if bytes.Equal(op.key, keyBytes) {
				return append([]byte(nil), op.val...), nil
			}
		case OpDelete:
			if bytes.Equal(op.key, keyBytes) {
				return nil, nil
			}
		case OpDropNamespace, batchOpClear:
			return nil, nil
		}
	}

	if bw.db == nil {
		return nil, nil
	}
	bw.db.shared.mu.RLock()
	defer bw.db.shared.mu.RUnlock()
	store, ok := bw.db.shared.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex(keyBytes)
	if !found {
		return nil, nil
	}
	return append([]byte(nil), store.items[idx].value...), nil
}

// GetString is the string flavour of GetBytes. It enforces the same type rule as
// DB.GetString: the value must have been written as a string, otherwise a
// TypeMismatchError is returned.
func (bw *BatchWriter) GetString(ns, key string) (*string, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	keyBytes := []byte(key)

	valType := ValTypeString
	var raw []byte
	found := false

	for i := len(bw.ops) - 1; i >= 0; i-- {
		op := bw.ops[i]
		if op.namespace != normNs {
			continue
		}
		switch op.opType {
		case OpPut:
			if bytes.Equal(op.key, keyBytes) {
				valType, raw, found = op.valType, op.val, true
			}
		case OpDelete:
			if bytes.Equal(op.key, keyBytes) {
				return nil, nil
			}
		case OpDropNamespace, batchOpClear:
			return nil, nil
		}
		if found {
			break
		}
	}

	if !found {
		if bw.db == nil {
			return nil, nil
		}
		bw.db.shared.mu.RLock()
		defer bw.db.shared.mu.RUnlock()
		store, ok := bw.db.shared.namespaces[normNs]
		if !ok {
			return nil, nil
		}
		idx, ok := store.findIndex(keyBytes)
		if !ok {
			return nil, nil
		}
		valType, raw = store.items[idx].valueType, store.items[idx].value
	}

	if valType != ValTypeString {
		return nil, NewTypeMismatchError(fmt.Sprintf(
			"Key '%s' has value_type %d, expected string", key, valType))
	}
	if !utf8.Valid(raw) {
		return nil, NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 in key '%s'", key))
	}
	s := string(raw)
	return &s, nil
}

func (db *sharedDB) applyBatch(writer *BatchWriter) error {
	if len(writer.ops) == 0 {
		return nil
	}

	return db.executeMutation(func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error) {
		var rawEntries []*RawOpEntry
		for _, op := range writer.ops {
			switch op.opType {
			case OpPut:
				rawEntries = append(rawEntries, &RawOpEntry{
					OpCode:    OpPut,
					ValueType: op.valType,
					Namespace: []byte(op.namespace),
					Key:       op.key,
					Value:     op.val,
				})
			case OpDelete:
				rawEntries = append(rawEntries, &RawOpEntry{
					OpCode:    OpDelete,
					ValueType: ValTypeBytes,
					Namespace: []byte(op.namespace),
					Key:       op.key,
					Value:     nil,
				})
			case OpDropNamespace:
				rawEntries = append(rawEntries, &RawOpEntry{
					OpCode:    OpDropNamespace,
					ValueType: ValTypeBytes,
					Namespace: []byte(op.namespace),
					Key:       nil,
					Value:     nil,
				})
			case batchOpClear:
				// C3: a Put issued earlier in this same batch is not in db.namespaces
				// yet, so expanding the clear from memory alone would leave its OpPut
				// in the log with no matching delete -- replaying the log would bring
				// that key back to life. Drop the shadowed puts (the clear removes them
				// again by definition) before collecting the deletes below.
				kept := rawEntries[:0]
				for _, entry := range rawEntries {
					if entry.OpCode != OpPut || string(entry.Namespace) != op.namespace {
						kept = append(kept, entry)
					}
				}
				rawEntries = kept

				if store, ok := db.namespaces[op.namespace]; ok {
					for _, item := range store.items {
						rawEntries = append(rawEntries, &RawOpEntry{
							OpCode:    OpDelete,
							ValueType: ValTypeBytes,
							Namespace: []byte(op.namespace),
							Key:       item.key,
							Value:     nil,
						})
					}
				}
			}
		}

		var events []KeyChangeEvent
		for _, op := range writer.ops {
			recordRollback(op.namespace, db.namespaces, rollbackStores, rollbackExisted)
			switch op.opType {
			case OpPut:
				events = append(events, KeyChangeEvent{
					Op:        OpChangePut,
					Namespace: op.namespace,
					Key:       string(op.key),
					ValueType: op.valType,
				})
				db.putInternal(op.namespace, op.key, op.valType, op.val, clonedInTx)
			case OpDelete:
				if store, ok := db.namespaces[op.namespace]; ok {
					if _, found := store.findIndex(op.key); found {
						events = append(events, KeyChangeEvent{
							Op:        OpChangeDelete,
							Namespace: op.namespace,
							Key:       string(op.key),
							ValueType: 0,
						})
						if !clonedInTx[op.namespace] {
							store = store.clone()
							db.namespaces[op.namespace] = store
							clonedInTx[op.namespace] = true
						}
						store.delete(op.key)

					}
				}
			case OpDropNamespace:
				if _, ok := db.namespaces[op.namespace]; ok {
					events = append(events, KeyChangeEvent{
						Op:        OpChangeDropNamespace,
						Namespace: op.namespace,
						Key:       "",
						ValueType: 0,
					})
					delete(db.namespaces, op.namespace)
					db.rebuildSortedNs()
					delete(clonedInTx, op.namespace)
				}
			case batchOpClear:
				if store, ok := db.namespaces[op.namespace]; ok && len(store.items) > 0 {
					events = append(events, KeyChangeEvent{
						Op:        OpChangeClear,
						Namespace: op.namespace,
						Key:       "",
						ValueType: 0,
					})
					db.namespaces[op.namespace] = &namespaceStore{items: make([]keyItem, 0)}
					clonedInTx[op.namespace] = true
				}
			}
		}

		return rawEntries, events, true, nil
	})
}

func (db *sharedDB) Compact() error {
	// FIX-01：与后台刷盘共用 `.tmp`。
	db.flushMu.Lock()
	defer db.flushMu.Unlock()
	db.mu.Lock()
	defer db.mu.Unlock()
	if db.closed {
		return NewLockedError("Database is closed: writes are refused after Close()")
	}
	diskMaxTxID, err := db.precheckDiskState()
	if err != nil {
		return err
	}
	return db.commitFlush(diskMaxTxID)
}

func (db *sharedDB) getOrCreateStoreForWrite(ns string, clonedInTx map[string]bool) *namespaceStore {
	store, ok := db.namespaces[ns]
	if !ok {
		store = &namespaceStore{}
		db.namespaces[ns] = store
		db.rebuildSortedNs()
		if clonedInTx != nil {
			clonedInTx[ns] = true
		}
		return store
	}
	if clonedInTx != nil {
		if !clonedInTx[ns] {
			store = store.clone()
			db.namespaces[ns] = store
			clonedInTx[ns] = true
		}
	} else {
		store = store.clone()
		db.namespaces[ns] = store
	}
	return store
}

func (db *sharedDB) putInternal(ns string, key []byte, vtype uint8, val []byte, clonedInTx map[string]bool) {
	store := db.getOrCreateStoreForWrite(ns, clonedInTx)
	store.put(key, vtype, val)
}

func (db *sharedDB) rebuildSortedNs() {
	names := make([]string, 0, len(db.namespaces))
	for ns := range db.namespaces {
		names = append(names, ns)
	}
	sort.Slice(names, func(i, j int) bool {
		return CompareUtf8Bytes([]byte(names[i]), []byte(names[j])) < 0
	})
	db.sortedNsNames = names
}

// initAndRecover opens the database.
//
// The caller (Open) has already acquired the connection lock and holds it for the
// whole lifetime of the connection, so every repair performed here -- promoting or
// deleting `.tmp`, healing a torn log tail, rolling a `.bak` forward -- writes to
// the database files while no other process can append to them. That is what used
// to be C10: tail healing ran without the lock and could truncate frames a
// concurrent appender had just written.
func (db *sharedDB) initAndRecover() error {

	tmpPath := db.path + ".tmp"
	bakPath := db.path + ".bak"

	// 1. Clean unfinished tmp
	if _, err := os.Stat(tmpPath); err == nil {
		if _, dbErr := os.Stat(db.path); os.IsNotExist(dbErr) && verifyFile(tmpPath) == nil {
			// 主库不存在且 `.tmp` 已通过校验 —— 它是唯一完好的数据。晋升失败必须让
			// Open 失败：旧实现丢弃错误后流程继续，主库/`.bak` 都不存在，于是落到
			// 「新建空库」分支，Open **成功返回空库**，而唯一的好数据还躺在 `.tmp` 里
			// （C1-C3 之后的第 4 处静默数据丢失）。Windows 上 renameWithRetry 因杀软/
			// 索引器/预览窗格的瞬时占用而失败的概率不低，这条路径尤其危险。
			if err := renameWithRetry(tmpPath, db.path); err != nil {
				return newDbError("IoError", fmt.Sprintf(
					"failed to promote validated .tmp to primary; the only intact copy is still at '%s'", tmpPath), err)
			}
		} else {
			_ = os.Remove(tmpPath)
		}
	}

	// 2. Try loading primary file
	if _, err := os.Stat(db.path); err == nil {
		entries, maxTxID, size, loadErr := db.loadFile(db.path)
		if loadErr == nil {
			if applyErr := db.applyEntries(entries); applyErr != nil {
				return applyErr
			}
			db.currentMaxTxID = maxTxID
			db.lastKnownDiskState.maxTxID = maxTxID
			db.lastKnownDiskState.fileSize = size
			return nil
		}
		// Primary corrupted. Decision D1: `.bak` holds the generation *before*
		// the last rotation, so replacing the primary with it silently rolls back
		// every write made since. It is used only when the caller explicitly
		// opted in; otherwise the error reports where that copy is kept.
		_, bakStatErr := os.Stat(bakPath)
		bakAvailable := bakStatErr == nil

		if db.options.AutoRecoverFromBackup && !db.options.ReadOnly && bakAvailable {
			// `.bak` is a recovery source: never tail-truncate it (G1).
			bakEntries, bakTxID, bakSize, bakLoadErr := db.loadFileWith(bakPath, false)
			if bakLoadErr == nil {
				_ = copyFile(bakPath, db.path)
				if db.options.OnWarning != nil {
					db.options.OnWarning("onRecoveredFromBackup", "Recovered database from .bak")
				}
				if applyErr := db.applyEntries(bakEntries); applyErr != nil {
					return applyErr
				}
				db.currentMaxTxID = bakTxID
				db.lastKnownDiskState.maxTxID = bakTxID
				db.lastKnownDiskState.fileSize = bakSize
				return nil
			}
			return NewCorruptedError(fmt.Sprintf(
				"%v; the previous generation kept at '%s' could not be loaded either",
				loadErr, bakPath))
		}

		if bakAvailable && IsCorrupted(loadErr) {
			return NewCorruptedError(fmt.Sprintf(
				"%v; a previous generation is kept at '%s': copy it over the primary file to recover, or open with AutoRecoverFromBackup = true",
				loadErr, bakPath))
		}

		return loadErr
	}

	// Branch ③: primary does not exist but a previous generation does.
	//
	// This state cannot be produced by an interrupted rotation -- that always
	// leaves a complete `.tmp` behind, which step 1 promotes -- so it means the
	// primary file disappeared (deleted or replaced externally). Promoting the
	// `.bak` on top of it is a silent downgrade to an older generation, and when
	// the `.bak` itself is unreadable the open used to succeed as an *empty*
	// database: the fourth silent data loss after C1-C3 (D1-7/D1-8). Both cases
	// are refused unless the caller explicitly opted into `.bak` recovery.
	if _, err := os.Stat(bakPath); err == nil {
		entries, maxTxID, size, bakLoadErr := db.loadFileWith(bakPath, false)
		if bakLoadErr == nil {
			if db.options.AutoRecoverFromBackup && !db.options.ReadOnly {
				_ = copyFile(bakPath, db.path)
				if db.options.OnWarning != nil {
					db.options.OnWarning("onRecoveredFromBackup", "Recovered database from .bak")
				}
				if applyErr := db.applyEntries(entries); applyErr != nil {
					return applyErr
				}
				db.currentMaxTxID = maxTxID
				db.lastKnownDiskState.maxTxID = maxTxID
				db.lastKnownDiskState.fileSize = size
				return nil
			}
			return NewCorruptedError(fmt.Sprintf(
				"Primary file '%s' is missing while a previous generation is kept at '%s': "+
					"copy it over the primary file to recover, or open with AutoRecoverFromBackup = true",
				db.path, bakPath))
		}
		return NewCorruptedError(fmt.Sprintf(
			"Primary file '%s' is missing and the previous generation kept at '%s' could not be loaded either: %v",
			db.path, bakPath, bakLoadErr))
	}

	// New database
	db.currentMaxTxID = 0
	db.lastKnownDiskState.maxTxID = 0
	db.lastKnownDiskState.fileSize = 0
	return nil
}

// allowTailTruncation reports whether the *live* log may be healed by discarding
// a torn tail.
func (db *sharedDB) allowTailTruncation() bool {
	return db.options.EngineMode == StorageEngineModeAppendOnlyLog && !db.options.ReadOnly
}

func (db *sharedDB) loadFile(path string) ([]*RawOpEntry, uint64, int64, error) {
	return db.loadFileWith(path, db.allowTailTruncation())
}

// loadFileWith loads a file under an explicit tail-truncation policy.
//
// Recovery sources (`.bak`) are always loaded with allowTailTruncation = false:
// truncating a recovery source would destroy the data it is kept for (G1).
func (db *sharedDB) loadFileWith(path string, allowTailTruncation bool) ([]*RawOpEntry, uint64, int64, error) {
	var f *os.File
	var err error
	if allowTailTruncation {
		f, err = os.OpenFile(path, os.O_RDWR, 0666)
	} else {
		f, err = os.Open(path)
	}
	if err != nil {
		return nil, 0, 0, err
	}
	defer f.Close()

	fi, err := f.Stat()
	if err != nil {
		return nil, 0, 0, err
	}
	size := fi.Size()

	if size < int64(FileHeaderLen) {
		return nil, 0, 0, NewCorruptedError("File size smaller than header")
	}

	var header [FileHeaderLen]byte
	if _, err := io.ReadFull(f, header[:]); err != nil {
		return nil, 0, 0, err
	}
	if err := VerifyFileHeader(header[:]); err != nil {
		return nil, 0, 0, err
	}

	if size == int64(FileHeaderLen) {
		return []*RawOpEntry{}, 0, size, nil
	}

	entries, maxTxID, effectiveSize, err := LoadAllFramesWithRecovery(f, size, allowTailTruncation, db.options.OnWarning)
	if err != nil {
		return nil, 0, 0, err
	}

	return entries, maxTxID, effectiveSize, nil
}

// applyEntries 将文件重放出的 Op 序列合入内存状态。
// 快路径：全部为 OpPut（快照文件的常态）时按命名空间分组追加，
// 再做一次稳定排序 + 同键去重（保留后写入者，与逐条覆盖语义一致），
// 整体 O(N log N)，替代旧实现逐条 clone 的 O(N^2)。
// 慢路径：含 OpDelete/OpDropNamespace 时保持顺序重放；加载期尚无快照，
// 直接原地修改，跳过 CoW clone。
// 未知 OpCode（规范 v3.2 仅定义 OpPut/OpDelete/OpDropNamespace 三种磁盘
// OpCode，且文件头版本检查已挡掉高版本文件）一律判损坏拒绝，与 .NET 对齐。
func (db *sharedDB) applyEntries(entries []*RawOpEntry) error {
	hasNonPut := false
	for _, e := range entries {
		if e.OpCode != OpPut {
			hasNonPut = true
			break
		}
	}

	if !hasNonPut {
		for _, e := range entries {
			ns := string(e.Namespace)
			store, ok := db.namespaces[ns]
			if !ok {
				store = &namespaceStore{}
				db.namespaces[ns] = store
				db.rebuildSortedNs()
			}
			store.items = append(store.items, keyItem{key: e.Key, valueType: e.ValueType, value: e.Value})
		}
		for _, store := range db.namespaces {
			sort.SliceStable(store.items, func(i, j int) bool {
				return bytes.Compare(store.items[i].key, store.items[j].key) < 0
			})
			// 稳定排序后同键块保持原先后顺序，逐块保留最后一个（后写覆盖先写）
			w := 0
			for i := 0; i < len(store.items); i++ {
				if w > 0 && bytes.Equal(store.items[w-1].key, store.items[i].key) {
					store.items[w-1] = store.items[i]
					continue
				}
				store.items[w] = store.items[i]
				w++
			}
			store.items = store.items[:w]
			// The fast path bypassed put(), so rebuild the ledger once here.
			store.recalculateLiveBytes()
		}
		return nil
	}

	for _, e := range entries {
		ns := string(e.Namespace)
		switch e.OpCode {
		case OpPut:
			store, ok := db.namespaces[ns]
			if !ok {
				store = &namespaceStore{}
				db.namespaces[ns] = store
				db.rebuildSortedNs()
			}
			store.put(e.Key, e.ValueType, e.Value)
		case OpDelete:
			if store, ok := db.namespaces[ns]; ok {
				store.delete(e.Key)
			}
		case OpDropNamespace:
			delete(db.namespaces, ns)
			db.rebuildSortedNs()
		default:
			return NewCorruptedError(fmt.Sprintf(
				"Unknown op code 0x%02X in namespace %q (spec v3.2 defines only OpPut/OpDelete/OpDropNamespace)",
				e.OpCode, ns))
		}
	}
	return nil
}

// beginFlush 是写路径的第一阶段：在任何内存变更**之前**执行只读校验、获取
// 进程间文件锁并做规范 §4.1.3 的磁盘预检。返回错误时尚未触碰任何内存状态，
// 因此调用方看到的错误与内存状态永远不会分叉。
//
// 成功时返回文件锁（必须交给 commitFlush，由它在 defer 中释放）与磁盘上当前
// 的最大 TxID。
// precheckDiskState 是写路径的第一阶段：所有会拒绝本次写的校验都在这里、且都在任何内存
// 修改之前完成。它返回磁盘上当前的 maxTxID。
//
// 自 C9 起这里不再涉及取锁：连接锁由 Open 持有整个生命周期，写路径不会再与自身冲突
// （那正是 C2），预检失败也不会留下"内存已改、磁盘未落"的状态。
func (db *sharedDB) precheckDiskState() (uint64, error) {
	if db.options.ReadOnly {
		return 0, NewLockedError("Database is opened in read-only mode")
	}

	// Pre-check disk state
	var diskMaxTxID uint64
	var diskFileSize int64
	if fi, statErr := os.Stat(db.path); statErr == nil {
		diskFileSize = fi.Size()
		if (db.lastKnownDiskState.maxTxID != 0 || db.lastKnownDiskState.fileSize != 0) && diskFileSize == db.lastKnownDiskState.fileSize {
			diskMaxTxID = db.lastKnownDiskState.maxTxID
		} else if diskFileSize >= int64(FileHeaderLen) {
			f, oErr := os.Open(db.path)
			if oErr == nil {
				diskMaxTxID, _ = ScanMaxTxID(f, diskFileSize)
				_ = f.Close()
			}
		}
		// B1 修复：空库（仅 32B 文件头）在磁盘上没有任何帧，ScanMaxTxID 返回 0，
		// 与 lastKnownDiskState.maxTxID（清空后仍保留递增 TxID）不可比。
		// 空文件场景只比对文件大小，避免清空后再写入误报并发修改。
		stateMatches := diskFileSize == db.lastKnownDiskState.fileSize &&
			(diskFileSize == int64(FileHeaderLen) || diskMaxTxID == db.lastKnownDiskState.maxTxID)
		if (db.lastKnownDiskState.maxTxID != 0 || db.lastKnownDiskState.fileSize != 0) && !stateMatches {
			return 0, NewConcurrentModificationError(fmt.Sprintf(
				"Disk state changed externally: expected (%d, %d), found (%d, %d)",
				db.lastKnownDiskState.maxTxID, db.lastKnownDiskState.fileSize,
				diskMaxTxID, diskFileSize,
			))
		}
	}
	return diskMaxTxID, nil
}

func serializeSnapshotToTmp(tmpPath string, sortedNsNames []string, namespaces map[string]*namespaceStore, startTxID uint64) (uint64, error) {
	tmpFile, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0666)
	if err != nil {
		return 0, err
	}

	// Write 32B file header
	header := CreateFileHeader()
	if _, err := tmpFile.Write(header[:]); err != nil {
		_ = tmpFile.Close()
		return 0, err
	}

	// Collect sorted alive entries
	var sortedEntries []*RawOpEntry
	for _, ns := range sortedNsNames {
		store := namespaces[ns]
		if store == nil {
			continue
		}
		for _, item := range store.items {
			sortedEntries = append(sortedEntries, &RawOpEntry{
				OpCode:    OpPut,
				ValueType: item.valueType,
				Namespace: []byte(ns),
				Key:       item.key,
				Value:     item.value,
			})
		}
	}

	finalMaxTxID := startTxID
	if len(sortedEntries) > 0 {
		currentTxID := finalMaxTxID + 1
		var currentFrame []*RawOpEntry
		currentPayloadLen := 0

		for _, entry := range sortedEntries {
			entrySize := entry.EncodedLen()
			if len(currentFrame) >= MaxOpsPerFrame || (currentPayloadLen+entrySize > MaxPayloadLen) {
				if err := WriteBatchFrame(tmpFile, currentTxID, currentFrame); err != nil {
					_ = tmpFile.Close()
					return 0, err
				}
				currentTxID++
				currentFrame = currentFrame[:0]
				currentPayloadLen = 0
			}
			currentPayloadLen += entrySize
			currentFrame = append(currentFrame, entry)
		}

		if len(currentFrame) > 0 {
			if err := WriteBatchFrame(tmpFile, currentTxID, currentFrame); err != nil {
				_ = tmpFile.Close()
				return 0, err
			}
			finalMaxTxID = currentTxID
		}
	}

	if err := tmpFile.Sync(); err != nil {
		_ = tmpFile.Close()
		return 0, err
	}
	if err := tmpFile.Close(); err != nil {
		return 0, err
	}

	return finalMaxTxID, nil
}

// commitFlush 是写路径的第二阶段：把内存状态序列化进 .tmp、fsync 后经
// .tmp -> .db 原子轮转换入（上一代保留为 .bak）。跨进程锁已由连接持有（C9），不再在此获取。
func (db *sharedDB) commitFlush(diskMaxTxID uint64) error {
	tmpPath := db.path + ".tmp"
	bakPath := db.path + ".bak"

	finalMaxTxID := db.currentMaxTxID
	if diskMaxTxID > finalMaxTxID {
		finalMaxTxID = diskMaxTxID
	}

	writtenMaxTxID, err := serializeSnapshotToTmp(tmpPath, db.sortedNsNames, db.namespaces, finalMaxTxID)
	if err != nil {
		db.isDirty = true
		return err
	}
	finalMaxTxID = writtenMaxTxID

	// Atomic rotation
	if _, err := os.Stat(db.path); err == nil {
		if err := renameWithRetry(db.path, bakPath); err != nil {
			db.isDirty = true
			return err
		}
	}
	if err := renameWithRetry(tmpPath, db.path); err != nil {
		db.isDirty = true
		return err
	}

	// C18: fsync parent directory on POSIX (Windows skips)
	fsyncParentDir(db.path)

	db.currentMaxTxID = finalMaxTxID
	if fi, statErr := os.Stat(db.path); statErr == nil {
		db.lastKnownDiskState.fileSize = fi.Size()
	}
	db.lastKnownDiskState.maxTxID = finalMaxTxID
	db.isDirty = false

	return nil
}

// compactionBackoff returns the number of writes to skip before retrying a failed
// automatic compaction. priorFailures is zero-based: the first failure waits 64
// writes, doubling per consecutive failure up to a cap of 4096.
func compactionBackoff(priorFailures uint32) uint64 {
	const base uint64 = 64
	const maxWindow uint64 = 4096

	shift := priorFailures
	if shift > 6 {
		shift = 6
	}
	window := base << shift
	if window > maxWindow {
		return maxWindow
	}
	return window
}

// maybeCompactAfterWrite applies the automatic-compaction policy to a mutation
// that has just been applied: compact when the log crossed the threshold, and
// retry a previously failed compaction once its backoff window has elapsed.
func (db *sharedDB) maybeCompactAfterWrite(newFileSize int64) error {
	if db.options.CompactionThresholdBytes == 0 {
		return nil
	}

	if db.needsCompaction {
		db.writesSinceCompactionAttempt++
		var priorFailures uint32
		if db.compactionFailures > 0 {
			priorFailures = db.compactionFailures - 1
		}
		if db.writesSinceCompactionAttempt < compactionBackoff(priorFailures) {
			return nil
		}
		return db.attemptCompaction()
	}

	// FIX-21: trigger on garbage, not on the absolute file size.
	//
	// A compaction leaves the file at exactly the live size, so the old
	// `newFileSize >= threshold` test was true on *every* write once live data
	// reached the threshold (measured at 6MB live: 26ms/write instead of ~0.2ms).
	// `garbage` comes from the per-namespace ledger, so no extra state is needed
	// and the condition stays correct after a reopen (an in-process "bytes
	// appended" counter would reset).
	live := db.livePayloadBytes()
	if live > newFileSize {
		live = newFileSize
	}
	garbage := uint64(newFileSize - live)
	effectiveThreshold := db.options.CompactionThresholdBytes
	if scaled := uint64(live) / 4; scaled > effectiveThreshold {
		effectiveThreshold = scaled
	}
	if garbage >= effectiveThreshold {
		return db.attemptCompaction()
	}

	// Adaptive trigger: a log dominated by tombstones or overwritten values never
	// reaches the size threshold, so reclaim it by dead ratio instead.
	if db.shouldCompactByDeadRatio(newFileSize) {
		return db.attemptCompaction()
	}

	return nil
}

// livePayloadBytes returns Σ(len(key)+len(value)) over all namespaces. It is
// O(#namespaces) because each store keeps its own ledger; no entry is walked.
func (db *sharedDB) livePayloadBytes() int64 {
	var total int64
	for _, store := range db.namespaces {
		total += store.liveBytes
	}
	return total
}

// shouldCompactByDeadRatio reports whether an under-threshold log is mostly
// garbage. liveBytes only counts payload, so it is always below the file size
// (headers and frame overhead account for the difference); clamping keeps the
// ratio sane if the ledger were ever off.
func (db *sharedDB) shouldCompactByDeadRatio(fileSize int64) bool {
	if fileSize < adaptiveMinFileSize {
		return false
	}
	live := db.livePayloadBytes()
	if live > fileSize {
		live = fileSize
	}
	return (fileSize-live)*100 >= fileSize*adaptiveDeadRatioPct
}

// attemptCompaction performs one automatic compaction and records the outcome.
//
// A failure never fails the caller's write unless StrictCompaction is enabled:
// the mutation is already durable, so reporting an error would invite a retry of
// a write that succeeded, and Increment (a read-modify-write) would then apply
// its delta twice (decision D2).
func (db *sharedDB) attemptCompaction() error {
	db.writesSinceCompactionAttempt = 0

	var compactErr error
	if diskMax, lockErr := db.precheckDiskState(); lockErr != nil {
		compactErr = lockErr
	} else {
		compactErr = db.commitFlush(diskMax)
	}

	if compactErr == nil {
		db.needsCompaction = false
		db.compactionFailures = 0
		return nil
	}

	db.compactionFailures++
	db.needsCompaction = true

	// The warning fires in both modes; StrictCompaction decides whether the error
	// additionally reaches the caller.
	db.queueWarning("onCompactionFailed", fmt.Sprintf(
		"Compaction failed (size %d bytes, threshold %d bytes, consecutive failures %d): %v",
		db.lastKnownDiskState.fileSize, db.options.CompactionThresholdBytes,
		db.compactionFailures, compactErr))

	if db.options.StrictCompaction {
		return compactErr
	}
	return nil
}

// retryPendingCompaction makes one best-effort attempt, called from Close so a
// transient failure does not outlive the handle. The outcome is never returned:
// Close is a cleanup path and a failed attempt is already reported via OnWarning.
func (db *sharedDB) retryPendingCompaction() {
	if !db.needsCompaction || db.options.ReadOnly ||
		db.options.EngineMode != StorageEngineModeAppendOnlyLog {
		return
	}
	_ = db.attemptCompaction()
}

// appendEntries appends the mutation's frames to the log and returns the
// resulting file size. Compaction is deliberately *not* triggered here: the
// snapshot a compaction writes is taken from the current in-memory state, so it
// may only run once the mutation has been applied to memory (C1).
func (db *sharedDB) appendEntries(entries []*RawOpEntry) (int64, error) {
	if len(entries) == 0 {
		return 0, nil
	}

	diskMaxTxID, err := db.precheckDiskState()
	if err != nil {
		return 0, err
	}

	// RC2：热路径不再带 O_CREATE。O_CREATE 每次打开都要多做一次"不存在则创建"的
	// 判定；文件在除首次写之外的所有情况下都已存在，把它挪到 NotFound 的回退分支。
	// 同时删掉重复的 os.Stat：文件是否为空由 Seek(0, io.SeekEnd) 的返回值判定，
	// 新文件（刚创建）与既有空文件同样返回 0，两者本来就走同一条写文件头分支。
	f, err := os.OpenFile(db.path, os.O_RDWR, 0666)
	if err != nil {
		if !os.IsNotExist(err) {
			return 0, err
		}
		f, err = os.OpenFile(db.path, os.O_CREATE|os.O_RDWR, 0666)
		if err != nil {
			return 0, err
		}
	}
	defer f.Close()

	pos, err := f.Seek(0, io.SeekEnd)
	if err != nil {
		return 0, err
	}
	if pos == 0 {
		hdr := CreateFileHeader()
		if _, err := f.Write(hdr[:]); err != nil {
			return 0, err
		}
	}

	currentTxID := db.currentMaxTxID
	if diskMaxTxID > currentTxID {
		currentTxID = diskMaxTxID
	}
	currentTxID++

	var currentFrame []*RawOpEntry
	currentPayloadLen := 0

	for _, entry := range entries {
		entrySize := entry.EncodedLen()
		if len(currentFrame) >= MaxOpsPerFrame || (currentPayloadLen+entrySize > MaxPayloadLen) {
			if err := WriteBatchFrame(f, currentTxID, currentFrame); err != nil {
				return 0, err
			}
			currentTxID++
			currentFrame = currentFrame[:0]
			currentPayloadLen = 0
		}
		currentPayloadLen += entrySize
		currentFrame = append(currentFrame, entry)
	}

	if len(currentFrame) > 0 {
		if err := WriteBatchFrame(f, currentTxID, currentFrame); err != nil {
			return 0, err
		}
	} else {
		currentTxID--
	}

	if db.options.SyncMode == SyncModeImmediate {
		_ = f.Sync()
		db.isDirty = false
	} else {
		db.isDirty = true
	}

	// RC2：追加结束后的当前偏移就是新文件大小，无需再走一次句柄 Stat。
	newFileSize, err := f.Seek(0, io.SeekEnd)
	if err != nil {
		return 0, err
	}
	db.currentMaxTxID = currentTxID
	db.lastKnownDiskState.fileSize = newFileSize
	db.lastKnownDiskState.maxTxID = currentTxID

	return newFileSize, nil
}

func recordRollback(ns string, namespaces map[string]*namespaceStore, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) {
	if _, recorded := rollbackExisted[ns]; !recorded {
		if store, ok := namespaces[ns]; ok {
			rollbackStores[ns] = store
			rollbackExisted[ns] = true
		} else {
			rollbackStores[ns] = nil
			rollbackExisted[ns] = false
		}
	}
}

func (db *sharedDB) rollbackMutation(rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) {
	for ns, oldStore := range rollbackStores {
		if rollbackExisted[ns] {
			db.namespaces[ns] = oldStore
		} else {
			delete(db.namespaces, ns)
		}
	}
	db.rebuildSortedNs()
}

func (db *sharedDB) executeMutation(
	mutateFn func(clonedInTx map[string]bool, rollbackStores map[string]*namespaceStore, rollbackExisted map[string]bool) ([]*RawOpEntry, []KeyChangeEvent, bool, error),
) error {
	db.flushMu.Lock()
	defer db.flushMu.Unlock()

	var (
		rawEntries       []*RawOpEntry
		events           []KeyChangeEvent
		watchers         []KeyChangeCallback
		shouldMutate     bool
		err              error
		isAOL            bool
		isImmediate      bool
		snapshotNs       map[string]*namespaceStore
		snapshotSortedNs []string
		targetTxID       uint64
		rollbackStores   = make(map[string]*namespaceStore)
		rollbackExisted  = make(map[string]bool)
		clonedInTx       = make(map[string]bool)
	)

	// 阶段 1：短写锁执行内存修改并捕获快照
	db.mu.Lock()
	if db.closed {
		db.mu.Unlock()
		return NewLockedError("Database is closed: writes are refused after Close()")
	}
	if db.options.ReadOnly {
		db.mu.Unlock()
		return NewLockedError("Database is opened in read-only mode")
	}

	rawEntries, events, shouldMutate, err = mutateFn(clonedInTx, rollbackStores, rollbackExisted)
	if err != nil || !shouldMutate {
		db.mu.Unlock()
		return err
	}

	isAOL = db.options.EngineMode == StorageEngineModeAppendOnlyLog
	isImmediate = db.options.SyncMode == SyncModeImmediate

	if isAOL {
		// 在 AOL 模式下：appendEntries 写日志 (C1: durability first)
		newFileSize, appendErr := db.appendEntries(rawEntries)
		if appendErr != nil {
			db.rollbackMutation(rollbackStores, rollbackExisted)
			db.isDirty = true
			db.mu.Unlock()
			return newDbError("IoError", "append entries failed", appendErr)
		}
		db.dirtyGeneration++
		compactErr := db.maybeCompactAfterWrite(newFileSize)
		if len(db.watchers) > 0 {
			watchers = make([]KeyChangeCallback, len(db.watchers))
			for i, slot := range db.watchers {
				watchers[i] = slot.cb
			}
		}
		db.mu.Unlock()
		db.dispatchPendingWarnings()
		if compactErr != nil {
			return compactErr
		}
	} else if isImmediate {
		// Snapshot 模式下的两阶段 Immediate 提交：
		diskMaxTxID, preErr := db.precheckDiskState()
		if preErr != nil {
			db.rollbackMutation(rollbackStores, rollbackExisted)
			db.isDirty = true
			db.mu.Unlock()
			return preErr
		}
		targetTxID = db.currentMaxTxID
		if diskMaxTxID > targetTxID {
			targetTxID = diskMaxTxID
		}

		snapshotNs = make(map[string]*namespaceStore, len(db.namespaces))
		for k, v := range db.namespaces {
			snapshotNs[k] = v
		}
		snapshotSortedNs = make([]string, len(db.sortedNsNames))
		copy(snapshotSortedNs, db.sortedNsNames)

		if len(db.watchers) > 0 {
			watchers = make([]KeyChangeCallback, len(db.watchers))
			for i, slot := range db.watchers {
				watchers[i] = slot.cb
			}
		}
		db.mu.Unlock()
		// 阶段 1 结束！写锁释放，并发读操作完全畅通无阻！

		// 阶段 2：锁外序列化到 .tmp 并 fsync
		tmpPath := db.path + ".tmp"
		bakPath := db.path + ".bak"

		writtenMaxTxID, serErr := serializeSnapshotToTmp(tmpPath, snapshotSortedNs, snapshotNs, targetTxID)
		if serErr != nil {
			db.mu.Lock()
			db.rollbackMutation(rollbackStores, rollbackExisted)
			db.isDirty = true
			db.mu.Unlock()
			return newDbError("IoError", fmt.Sprintf("Failed to write snapshot to '%s'", tmpPath), serErr)
		}

		// 阶段 3：短写锁原子轮转与元数据更新
		db.mu.Lock()
		var rotErr error
		if _, statErr := os.Stat(db.path); statErr == nil {
			rotErr = renameWithRetry(db.path, bakPath)
		}
		if rotErr == nil {
			rotErr = renameWithRetry(tmpPath, db.path)
		}

		if rotErr != nil {
			_ = os.Remove(tmpPath)
			db.rollbackMutation(rollbackStores, rollbackExisted)
			db.isDirty = true
			db.mu.Unlock()
			return newDbError("IoError", fmt.Sprintf("Failed to rotate snapshot files: %v", rotErr), rotErr)
		}

		fsyncParentDir(db.path)

		if writtenMaxTxID > db.currentMaxTxID {
			db.currentMaxTxID = writtenMaxTxID
		}
		if fi, statErr := os.Stat(db.path); statErr == nil {
			db.lastKnownDiskState.fileSize = fi.Size()
		}
		db.lastKnownDiskState.maxTxID = db.currentMaxTxID
		db.isDirty = false
		db.dirtyGeneration++
		db.mu.Unlock()
	} else {
		// SyncModeBatch (Deferred / Manual)
		db.currentMaxTxID++
		db.isDirty = true
		db.dirtyGeneration++
		if len(db.watchers) > 0 {
			watchers = make([]KeyChangeCallback, len(db.watchers))
			for i, slot := range db.watchers {
				watchers[i] = slot.cb
			}
		}
		db.mu.Unlock()
	}

	// 阶段 4：锁外派发 watchers 与 warnings
	for _, ev := range events {
		for _, w := range watchers {
			w(ev)
		}
	}
	db.dispatchPendingWarnings()
	return nil
}

func validateLengths(key string, val []byte) error {
	if len(key) > MaxKeyLen {
		return NewKeyTooLargeError(fmt.Sprintf("Key length %d exceeds max %d", len(key), MaxKeyLen))
	}
	if len(val) > MaxValLen {
		return NewKeyTooLargeError(fmt.Sprintf("Value length %d exceeds max %d", len(val), MaxValLen))
	}
	return nil
}

func verifyFile(path string) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		return err
	}
	if fi.Size() < int64(FileHeaderLen) {
		return NewCorruptedError("File too small")
	}
	var hdr [FileHeaderLen]byte
	if _, err := io.ReadFull(f, hdr[:]); err != nil {
		return err
	}
	if err := VerifyFileHeader(hdr[:]); err != nil {
		return err
	}
	if fi.Size() > int64(FileHeaderLen) {
		_, _, err := LoadAllFrames(f, fi.Size())
		return err
	}
	return nil
}

// §3.10（API v1.0 冻结）：仅对 Windows 共享冲突重试，其余错误立即返回，
// 与 Rust rename_with_retry 对齐。旧实现对一切错误无脑重试 5 次，会把
// 权限不足等永久性错误放大 230ms 并掩盖真实原因。
func renameWithRetry(src, dst string) error {
	backoffs := []time.Duration{5 * time.Millisecond, 15 * time.Millisecond, 30 * time.Millisecond, 60 * time.Millisecond, 120 * time.Millisecond}
	var lastErr error
	for _, wait := range backoffs {
		err := os.Rename(src, dst)
		if err == nil {
			return nil
		}
		lastErr = err
		if !isWindowsSharingViolation(err) {
			return err
		}
		time.Sleep(wait)
	}
	return lastErr
}

// isWindowsSharingViolation 判断是否为 Windows 文件共享冲突。
//
// 必须比较 Win32 **裸码**：ERROR_ACCESS_DENIED=5 / ERROR_SHARING_VIOLATION=32 /
// ERROR_LOCK_VIOLATION=33。操作系统通过 errno 传回的正是这三个裸值。
//
// 旧实现比较 syscall.EACCES / EBUSY / ETXTBSY —— 那是 Go 放在
// APPLICATION_ERROR+n 合成区间的值（EACCES=536870913 / EBUSY=536870928 /
// ETXTBSY=536871035），与 Win32 真实码分处完全不同的数值区间，谓词恒为 false，
// 规范 §4.2 要求的 [5ms,15ms,30ms,60ms,120ms] 共 230ms 退避一次都不会执行。
// 同一仓库的 lock_windows.go 也正确地手工定义了 errSharingViolation=32。
//
// 本文件禁止再用 syscall.E* 系列判断共享冲突。
func isWindowsSharingViolation(err error) bool {
	if runtime.GOOS != "windows" {
		return false
	}
	var errno syscall.Errno
	if errors.As(err, &errno) {
		// 裸 Win32 码，与 Rust lock.rs / db.rs 对齐。
		return errno == errAccessDenied || errno == errSharingViolation || errno == errLockViolation
	}
	return false
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()

	out, err := os.OpenFile(dst, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0666)
	if err != nil {
		return err
	}
	defer out.Close()

	if _, err := io.Copy(out, in); err != nil {
		return err
	}
	return out.Sync()
}

// Snapshot provides a point-in-time immutable read-only view of the database.
// CoW 隔离：Snapshot 只共享 *namespaceStore 指针，写路径在 snapshotCount > 0
// 时才克隆，因此快照创建本身是 O(命名空间数)。
// 计数通过 Close() 显式释放；若用户遗忘 Close，由 GC finalizer 兜底释放，
// 避免快照计数泄漏导致写路径永久退化为克隆模式。
type Snapshot struct {
	shared     *sharedDB
	txID       uint64
	namespaces map[string]*namespaceStore
	released   int32 // CAS 保护，防止 Close 与 finalizer 双重释放
}

func (db *sharedDB) Snapshot() *Snapshot {
	db.mu.Lock()
	defer db.mu.Unlock()

	snapNs := make(map[string]*namespaceStore, len(db.namespaces))
	for k, v := range db.namespaces {
		snapNs[k] = v
	}
	snap := &Snapshot{
		shared:     db,
		txID:       db.currentMaxTxID,
		namespaces: snapNs,
	}
	db.snapshotCount++
	runtime.SetFinalizer(snap, func(s *Snapshot) { s.release() })
	return snap
}

func (s *Snapshot) release() {
	if s.shared != nil && atomic.CompareAndSwapInt32(&s.released, 0, 1) {
		s.shared.mu.Lock()
		if s.shared.snapshotCount > 0 {
			s.shared.snapshotCount--
		}
		s.shared.mu.Unlock()
	}
}

func (s *Snapshot) TxID() uint64 {
	return s.txID
}

func (s *Snapshot) ContainsKey(ns, key string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return false, nil
	}
	_, found := store.findIndex([]byte(key))
	return found, nil
}

func (s *Snapshot) Count(ns string) (int, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return 0, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return 0, nil
	}
	return len(store.items), nil
}

func (s *Snapshot) GetBytes(ns, key string) ([]byte, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	res := make([]byte, len(store.items[idx].value))
	copy(res, store.items[idx].value)
	return res, nil
}

func (s *Snapshot) GetString(ns, key string) (*string, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	item := store.items[idx]
	if item.valueType != ValTypeString {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected string", key, item.valueType))
	}
	if !utf8.Valid(item.value) {
		return nil, NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 sequence in key '%s'", key))
	}
	str := string(item.value)
	return &str, nil
}

func (s *Snapshot) GetInt64(ns, key string) (*int64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeInt64 {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected int64", key, store.items[idx].valueType))
	}
	v, err := DecodeInt64(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (s *Snapshot) GetFloat64(ns, key string) (*float64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeFloat64 {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected float64", key, store.items[idx].valueType))
	}
	v, err := DecodeFloat64(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (s *Snapshot) GetBool(ns, key string) (*bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return nil, nil
	}
	idx, found := store.findIndex([]byte(key))
	if !found {
		return nil, nil
	}
	if store.items[idx].valueType != ValTypeBool {
		return nil, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected bool", key, store.items[idx].valueType))
	}
	v, err := DecodeBool(store.items[idx].value)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (s *Snapshot) Keys(ns string, prefix ...string) ([]string, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return []string{}, nil
	}
	var prefixBytes []byte
	if len(prefix) > 0 && prefix[0] != "" {
		prefixBytes = []byte(prefix[0])
	}
	// §3.7：与 DB.Keys 一致，跳过非 UTF-8 key
	result := make([]string, 0)
	for _, item := range store.items {
		if prefixBytes == nil || bytes.HasPrefix(item.key, prefixBytes) {
			if utf8.Valid(item.key) {
				result = append(result, string(item.key))
			}
		}
	}
	return result, nil
}

// §3.9（API v1.0 冻结）：签名与 DB.ScanRange 统一为 *string。
// nil 表示无界；空串 "" 是有效的边界键（与旧语义的区别已写入规范）。
func (s *Snapshot) ScanRange(ns string, startKey, endKey *string) ([]Entry, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return nil, err
	}
	store, ok := s.namespaces[normNs]
	if !ok {
		return []Entry{}, nil
	}
	var startBytes, endBytes []byte
	if startKey != nil {
		startBytes = []byte(*startKey)
	}
	if endKey != nil {
		endBytes = []byte(*endKey)
	}
	if startBytes != nil && endBytes != nil && bytes.Compare(startBytes, endBytes) >= 0 {
		return []Entry{}, nil
	}

	startIdx := 0
	if startBytes != nil {
		idx, _ := store.findIndex(startBytes)
		startIdx = idx
	}

	result := make([]Entry, 0)
	for i := startIdx; i < len(store.items); i++ {
		item := store.items[i]
		if endBytes != nil && bytes.Compare(item.key, endBytes) >= 0 {
			break
		}
		valCopy := make([]byte, len(item.value))
		copy(valCopy, item.value)
		result = append(result, Entry{
			Namespace: normNs,
			Key:       string(item.key),
			ValueType: item.valueType,
			RawBytes:  valCopy,
		})
	}
	return result, nil
}

func (s *Snapshot) Close() error {
	s.release()
	s.namespaces = nil
	return nil
}

// ==================== DB Handle Forwarding Methods ====================

func (db *DB) GetBytes(ns, key string) ([]byte, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.GetBytes(ns, key)
}

func (db *DB) GetString(ns, key string) (*string, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.GetString(ns, key)
}

func (db *DB) ContainsKey(ns, key string) (bool, error) {
	if err := db.checkClosed(); err != nil {
		return false, err
	}
	return db.shared.ContainsKey(ns, key)
}

func (db *DB) Count(ns string) (int, error) {
	if err := db.checkClosed(); err != nil {
		return 0, err
	}
	return db.shared.Count(ns)
}

func (db *DB) PutBytes(ns, key string, val []byte) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.PutBytes(ns, key, val)
}

func (db *DB) PutString(ns, key, val string) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.PutString(ns, key, val)
}

func (db *DB) PutInt64(ns, key string, val int64) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.PutInt64(ns, key, val)
}

func (db *DB) GetInt64(ns, key string) (*int64, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.GetInt64(ns, key)
}

func (db *DB) PutFloat64(ns, key string, val float64) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.PutFloat64(ns, key, val)
}

func (db *DB) GetFloat64(ns, key string) (*float64, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.GetFloat64(ns, key)
}

func (db *DB) PutBool(ns, key string, val bool) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.PutBool(ns, key, val)
}

func (db *DB) GetBool(ns, key string) (*bool, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.GetBool(ns, key)
}

func (db *DB) IncrementInt64(ns, key string, delta int64) (int64, error) {
	if err := db.checkWritable(); err != nil {
		return 0, err
	}
	return db.shared.IncrementInt64(ns, key, delta)
}

func (db *DB) Delete(ns, key string) (bool, error) {
	if err := db.checkWritable(); err != nil {
		return false, err
	}
	return db.shared.Delete(ns, key)
}

func (db *DB) DropNamespace(ns string) (bool, error) {
	if err := db.checkWritable(); err != nil {
		return false, err
	}
	return db.shared.DropNamespace(ns)
}

func (db *DB) ClearNamespace(ns string) (bool, error) {
	if err := db.checkWritable(); err != nil {
		return false, err
	}
	return db.shared.ClearNamespace(ns)
}

func (db *DB) Watch(cb KeyChangeCallback) *Watcher {
	if err := db.checkClosed(); err != nil {
		return nil
	}
	return db.shared.Watch(cb)
}

func (db *DB) Namespaces() []string {
	if err := db.checkClosed(); err != nil {
		return nil
	}
	return db.shared.Namespaces()
}

func (db *DB) FileSize() int64 {
	if err := db.checkClosed(); err != nil {
		return 0
	}
	return db.shared.FileSize()
}

func (db *DB) Stats() DbStats {
	if err := db.checkClosed(); err != nil {
		return DbStats{}
	}
	return db.shared.Stats()
}

func (db *DB) Backup(dest string) error {
	if err := db.checkClosed(); err != nil {
		return err
	}
	return db.shared.Backup(dest)
}

func (db *DB) Flush() error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.Flush()
}

func (db *DB) IterPrefix(ns string, prefix ...string) ([]Entry, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.IterPrefix(ns, prefix...)
}

func (db *DB) Keys(ns string, prefix ...string) ([]string, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.Keys(ns, prefix...)
}

func (db *DB) ScanRange(ns string, startKey, endKey *string) ([]Entry, error) {
	if err := db.checkClosed(); err != nil {
		return nil, err
	}
	return db.shared.ScanRange(ns, startKey, endKey)
}

func (db *DB) Batch(fn func(b *BatchWriter) error) error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	bw := &BatchWriter{db: db}
	if err := fn(bw); err != nil {
		return err
	}
	return db.shared.applyBatch(bw)
}

func (db *DB) Compact() error {
	if err := db.checkWritable(); err != nil {
		return err
	}
	return db.shared.Compact()
}

func (db *DB) Snapshot() *Snapshot {
	if err := db.checkClosed(); err != nil {
		return nil
	}
	return db.shared.Snapshot()
}
