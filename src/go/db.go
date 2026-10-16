package ckv

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"os"
	"runtime"
	"sort"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
	"unicode/utf8"
)

// batchOpClear 是 batch 暂存区的内部操作码，仅存在于内存 Staging 阶段，
// 不进入二进制文件格式（规范仅定义 OpPut/OpDelete/OpDropNamespace 三种磁盘 OpCode）。
const batchOpClear uint8 = 0x80

type keyItem struct {
	key       []byte
	valueType uint8
	value     []byte
}

type namespaceStore struct {
	items []keyItem
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
		ns.items[idx].valueType = vtype
		ns.items[idx].value = val
	} else {
		item := keyItem{key: key, valueType: vtype, value: val}
		ns.items = append(ns.items, keyItem{})
		copy(ns.items[idx+1:], ns.items[idx:])
		ns.items[idx] = item
	}
}

func (ns *namespaceStore) delete(key []byte) {
	idx, found := ns.findIndex(key)
	if found {
		copy(ns.items[idx:], ns.items[idx+1:])
		ns.items = ns.items[:len(ns.items)-1]
	}
}

func (ns *namespaceStore) clone() *namespaceStore {
	newItems := make([]keyItem, len(ns.items))
	copy(newItems, ns.items)
	return &namespaceStore{items: newItems}
}

type DB struct {
	mu                 sync.RWMutex
	path               string
	options            DbOptions
	namespaces         map[string]*namespaceStore
	sortedNsNames      []string
	currentMaxTxID     uint64
	snapshotCount      int // 存活快照数量；>0 时写路径执行 CoW 克隆，=0 时原地修改
	lastKnownDiskState struct {
		maxTxID  uint64
		fileSize int64
	}
	watchers []watcherSlot
	nextWatcherID uint64

	// §3.12（API v1.0 冻结）：同路径重复 Open 返回进程内单例（与 Rust/.NET 对齐）。
	// Go 无标准库弱引用，条目依赖显式 Close() 释放。
	registered bool

	isDirty bool
	stopCh  chan struct{}
	bgWg    sync.WaitGroup
}

// Open 打开或创建数据库，执行崩溃自愈并做全文件帧校验。
//
// §3.12（API v1.0 冻结）：若同一路径已在本进程打开，返回**同一个实例**
// （进程内单例），options 以首次打开为准——与 Rust / .NET 行为对齐。
// Go 无标准库弱引用，注册表条目由显式 Close() 释放；未 Close 的同路径
// 再次 Open 会拿到旧实例。
func Open(path string, options ...DbOptions) (*DB, error) {
	opts := DefaultDbOptions()
	if len(options) > 0 {
		opts = options[0]
	}

	if actual, loaded := openRegistry.Load(path); loaded {
		if existing, ok := actual.(*DB); ok && existing != nil {
			return existing, nil
		}
	}

	db := &DB{
		path:          path,
		options:       opts,
		namespaces:    make(map[string]*namespaceStore),
		sortedNsNames: make([]string, 0),
	}

	if err := db.initAndRecover(); err != nil {
		return nil, err
	}

	actual, loaded := openRegistry.LoadOrStore(path, db)
	if loaded {
		if existing, ok := actual.(*DB); ok && existing != nil {
			// 竞态：另一个 goroutine 先注册成功，复用它的实例
			return existing, nil
		}
	}
	db.registered = true

	if db.options.SyncMode == SyncModeDeferred && !db.options.ReadOnly {
		db.stopCh = make(chan struct{})
		db.bgWg.Add(1)
		interval := time.Duration(db.options.SyncIntervalMs) * time.Millisecond
		if interval <= 0 {
			interval = 100 * time.Millisecond
		}
		go func() {
			defer db.bgWg.Done()
			ticker := time.NewTicker(interval)
			defer ticker.Stop()
			for {
				select {
				case <-db.stopCh:
					return
				case <-ticker.C:
					db.flushIfDirty()
				}
			}
		}()
	}

	return db, nil
}

func (db *DB) flushIfDirty() {
	db.mu.Lock()
	defer db.mu.Unlock()
	if !db.isDirty || db.options.ReadOnly {
		return
	}
	if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
		if f, err := os.Open(db.path); err == nil {
			_ = f.Sync()
			_ = f.Close()
		}
		db.isDirty = false
		return
	}
	lock, diskMaxTxID, err := db.beginFlush()
	if err != nil {
		return
	}
	_ = db.commitFlush(lock, diskMaxTxID)
}

// Close 释放实例持有的资源：从进程内注册表移除自身（§3.12），
// 并刷盘未提交的脏数据。使同路径的下一次 Open 能创建新实例。幂等。
func (db *DB) Close() error {
	db.mu.Lock()
	if db.stopCh != nil {
		close(db.stopCh)
		db.stopCh = nil
		db.mu.Unlock()
		db.bgWg.Wait()
		db.mu.Lock()
	}
	if db.isDirty && !db.options.ReadOnly {
		if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
			if f, err := os.Open(db.path); err == nil {
				_ = f.Sync()
				_ = f.Close()
			}
			db.isDirty = false
		} else if lock, diskMaxTxID, err := db.beginFlush(); err == nil {
			_ = db.commitFlush(lock, diskMaxTxID)
		}
	}
	if db.registered {
		db.registered = false
		if actual, loaded := openRegistry.Load(db.path); loaded {
			if actual == db {
				openRegistry.Delete(db.path)
			}
		}
	}
	db.watchers = nil
	db.mu.Unlock()
	return nil
}

// watcherSlot 让回调可被精确注销（API v1.0 §2.2）。
type watcherSlot struct {
	id uint64
	cb KeyChangeCallback
}

// Watcher 是 Watch 返回的订阅句柄。仅显式 Unwatch() 才移除回调，
// 单纯丢弃句柄不注销（与 Rust/.NET 一致，API v1.0 §2.2）。
type Watcher struct {
	db *DB
	id uint64
}

// Unwatch 移除关联的回调。幂等。
func (w *Watcher) Unwatch() {
	if w == nil || w.db == nil {
		return
	}
	w.db.mu.Lock()
	out := w.db.watchers[:0]
	for _, slot := range w.db.watchers {
		if slot.id != w.id {
			out = append(out, slot)
		}
	}
	w.db.watchers = out
	w.db.mu.Unlock()
}

// Id 返回该订阅在实例内的 id。
func (w *Watcher) Id() uint64 { return w.id }

// openRegistry 是同路径单例注册表（§3.12）。
var openRegistry sync.Map // path -> *DB


func (db *DB) GetBytes(ns, key string) ([]byte, error) {
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

func (db *DB) GetString(ns, key string) (*string, error) {
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

func (db *DB) ContainsKey(ns, key string) (bool, error) {
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

func (db *DB) Count(ns string) (int, error) {
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

func (db *DB) putHelper(ns string, key []byte, valType uint8, val []byte) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	if err := validateLengths(string(key), val); err != nil {
		return err
	}

	var watchers []KeyChangeCallback
	db.mu.Lock()
	entry := &RawOpEntry{
		OpCode:    OpPut,
		ValueType: valType,
		Namespace: []byte(normNs),
		Key:       key,
		Value:     val,
	}
	if err := db.applyMutation([]*RawOpEntry{entry}, func() {
		db.putInternal(normNs, key, valType, val)
	}); err != nil {
		db.mu.Unlock()
		return err
	}
	if len(db.watchers) > 0 {
		watchers = make([]KeyChangeCallback, len(db.watchers))
		for i, slot := range db.watchers {
			watchers[i] = slot.cb
		}
	}
	db.mu.Unlock()

	if len(watchers) > 0 {
		ev := KeyChangeEvent{
			Op:        OpChangePut,
			Namespace: normNs,
			Key:       string(key),
			ValueType: valType,
		}
		for _, w := range watchers {
			w(ev)
		}
	}
	return nil
}

func (db *DB) PutBytes(ns, key string, val []byte) error {
	return db.putHelper(ns, []byte(key), ValTypeBytes, val)
}

func (db *DB) PutString(ns, key, val string) error {
	return db.putHelper(ns, []byte(key), ValTypeString, []byte(val))
}

func (db *DB) PutInt64(ns, key string, val int64) error {
	return db.putHelper(ns, []byte(key), ValTypeInt64, EncodeInt64(val))
}

func (db *DB) GetInt64(ns, key string) (*int64, error) {
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

func (db *DB) PutFloat64(ns, key string, val float64) error {
	return db.putHelper(ns, []byte(key), ValTypeFloat64, EncodeFloat64(val))
}

func (db *DB) GetFloat64(ns, key string) (*float64, error) {
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

func (db *DB) PutBool(ns, key string, val bool) error {
	return db.putHelper(ns, []byte(key), ValTypeBool, EncodeBool(val))
}

func (db *DB) GetBool(ns, key string) (*bool, error) {
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

func (db *DB) IncrementInt64(ns, key string, delta int64) (int64, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return 0, err
	}
	keyBytes := []byte(key)
	if err := validateLengths(key, []byte{0, 0, 0, 0, 0, 0, 0, 0}); err != nil {
		return 0, err
	}

	var watchers []KeyChangeCallback
	var newVal int64

	db.mu.Lock()
	if db.options.ReadOnly {
		db.mu.Unlock()
		return 0, NewLockedError("Database is opened in read-only mode")
	}
	store, ok := db.namespaces[normNs]
	if !ok {
		store = &namespaceStore{items: make([]keyItem, 0)}
		db.namespaces[normNs] = store
		db.rebuildSortedNs()
	}
	idx, found := store.findIndex(keyBytes)
	if found {
		if store.items[idx].valueType != ValTypeInt64 {
			db.mu.Unlock()
			return 0, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected int64", key, store.items[idx].valueType))
		}
		curr, err := DecodeInt64(store.items[idx].value)
		if err != nil {
			db.mu.Unlock()
			return 0, err
		}
		newVal = curr + delta
	} else {
		newVal = delta
	}

	encoded := EncodeInt64(newVal)
	entry := &RawOpEntry{
		OpCode:    OpPut,
		ValueType: ValTypeInt64,
		Namespace: []byte(normNs),
		Key:       keyBytes,
		Value:     encoded,
	}
	if err := db.applyMutation([]*RawOpEntry{entry}, func() {
		db.putInternal(normNs, keyBytes, ValTypeInt64, encoded)
	}); err != nil {
		db.mu.Unlock()
		return 0, err
	}
	if len(db.watchers) > 0 {
		watchers = make([]KeyChangeCallback, len(db.watchers))
		for i, slot := range db.watchers {
			watchers[i] = slot.cb
		}
	}
	db.mu.Unlock()

	if len(watchers) > 0 {
		ev := KeyChangeEvent{
			Op:        OpChangePut,
			Namespace: normNs,
			Key:       key,
			ValueType: ValTypeInt64,
		}
		for _, w := range watchers {
			w(ev)
		}
	}
	return newVal, nil
}

// Delete 单条删除指定键，返回是否实际删除了键（规范 §5.2.2）。
func (db *DB) Delete(ns, key string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var watchers []KeyChangeCallback
	deleted := false

	db.mu.Lock()
	if db.options.ReadOnly {
		db.mu.Unlock()
		return false, NewLockedError("Database is opened in read-only mode")
	}
	if store, ok := db.namespaces[normNs]; ok {
		if _, found := store.findIndex([]byte(key)); found {
			deleted = true
			entry := &RawOpEntry{
				OpCode:    OpDelete,
				ValueType: ValTypeBytes,
				Namespace: []byte(normNs),
				Key:       []byte(key),
				Value:     nil,
			}
			if err := db.applyMutation([]*RawOpEntry{entry}, func() {
				if db.snapshotCount > 0 {
					store = store.clone()
					db.namespaces[normNs] = store
				}
				store.delete([]byte(key))
			}); err != nil {
				db.mu.Unlock()
				return false, err
			}
			if len(db.watchers) > 0 {
				watchers = make([]KeyChangeCallback, len(db.watchers))
				for i, slot := range db.watchers {
					watchers[i] = slot.cb
				}
			}
		}
	}
	db.mu.Unlock()

	if deleted && len(watchers) > 0 {
		ev := KeyChangeEvent{
			Op:        OpChangeDelete,
			Namespace: normNs,
			Key:       key,
			ValueType: 0,
		}
		for _, w := range watchers {
			w(ev)
		}
	}
	return deleted, nil
}

// DropNamespace 物理彻底删除指定命名空间，返回是否命中存在（规范 §5.2.2）。
// 按规范 §5.2.3 发送单条聚合事件（Op=DropNamespace，Key 为空串）。
func (db *DB) DropNamespace(ns string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var watchers []KeyChangeCallback
	dropped := false

	db.mu.Lock()
	if db.options.ReadOnly {
		db.mu.Unlock()
		return false, NewLockedError("Database is opened in read-only mode")
	}
	if _, ok := db.namespaces[normNs]; ok {
		dropped = true
		entry := &RawOpEntry{
			OpCode:    OpDropNamespace,
			ValueType: ValTypeBytes,
			Namespace: []byte(normNs),
			Key:       nil,
			Value:     nil,
		}
		if err := db.applyMutation([]*RawOpEntry{entry}, func() {
			delete(db.namespaces, normNs)
			db.rebuildSortedNs()
		}); err != nil {
			db.mu.Unlock()
			return false, err
		}
		if len(db.watchers) > 0 {
			watchers = make([]KeyChangeCallback, len(db.watchers))
			for i, slot := range db.watchers {
				watchers[i] = slot.cb
			}
		}
	}
	db.mu.Unlock()

	if dropped && len(watchers) > 0 {
		ev := KeyChangeEvent{
			Op:        OpChangeDropNamespace,
			Namespace: normNs,
			Key:       "",
			ValueType: 0,
		}
		for _, w := range watchers {
			w(ev)
		}
	}
	return dropped, nil
}

// ClearNamespace 清空命名空间内的所有条目但保留命名空间自身，
// 返回是否实际清空（命名空间存在且非空）（规范 §5.2.2）。
// 按规范 §5.2.3 发送单条聚合事件（Op=Clear，Key 为空串）。
func (db *DB) ClearNamespace(ns string) (bool, error) {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return false, err
	}

	var watchers []KeyChangeCallback
	cleared := false

	db.mu.Lock()
	if db.options.ReadOnly {
		db.mu.Unlock()
		return false, NewLockedError("Database is opened in read-only mode")
	}
	if store, ok := db.namespaces[normNs]; ok && len(store.items) > 0 {
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
		if err := db.applyMutation(entries, func() {
			db.namespaces[normNs] = &namespaceStore{items: make([]keyItem, 0)}
		}); err != nil {
			db.mu.Unlock()
			return false, err
		}
		if len(db.watchers) > 0 {
			watchers = make([]KeyChangeCallback, len(db.watchers))
			for i, slot := range db.watchers {
				watchers[i] = slot.cb
			}
		}
	}
	db.mu.Unlock()

	if cleared && len(watchers) > 0 {
		ev := KeyChangeEvent{
			Op:        OpChangeClear,
			Namespace: normNs,
			Key:       "",
			ValueType: 0,
		}
		for _, w := range watchers {
			w(ev)
		}
	}
	return cleared, nil
}

// Watch 注册数据变更回调，返回订阅句柄（API v1.0 §2.2）。
// 仅显式 (*Watcher).Unwatch() 才移除；丢弃句柄不注销。
func (db *DB) Watch(cb KeyChangeCallback) *Watcher {
	db.mu.Lock()
	defer db.mu.Unlock()
	db.nextWatcherID++
	id := db.nextWatcherID
	db.watchers = append(db.watchers, watcherSlot{id: id, cb: cb})
	return &Watcher{db: db, id: id}
}

// Namespaces 按 UTF-8 无符号字节序返回所有命名空间（API v1.0 §2.1）。
// 默认命名空间为 ""；clear_namespace 之后的空命名空间仍然列出。
// 非 UTF-8 的命名空间名（只能由外部文件引入）跳过。
func (db *DB) Namespaces() []string {
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
func (db *DB) FileSize() int64 {
	fi, err := os.Stat(db.path)
	if err != nil {
		return 0
	}
	return fi.Size()
}

// DbStats 是 Stats() 返回的聚合统计（API v1.0 §2.4）。
type DbStats struct {
	FileSize         int64
	NamespaceCount   int
	TotalEntries     int
	CurrentMaxTxID   uint64
}

// Stats 返回纯内存统计，不触发磁盘 I/O、不修改状态。
func (db *DB) Stats() DbStats {
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
func (db *DB) Backup(dest string) error {
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
func (db *DB) Flush() error {
	db.mu.Lock()
	defer db.mu.Unlock()
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
	lock, diskMaxTxID, err := db.beginFlush()
	if err != nil {
		return err
	}
	return db.commitFlush(lock, diskMaxTxID)
}

// IterPrefix 按 UTF-8 无符号字节序返回匹配前缀的条目（API v1.0 §2.6）。
// 缓冲式语义：内部一次性收集后逐个返回（三语言一致的刻意选择）。
func (db *DB) IterPrefix(ns string, prefix ...string) ([]Entry, error) {
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

func (db *DB) Keys(ns string, prefix ...string) ([]string, error) {
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

func (db *DB) ScanRange(ns string, startKey, endKey *string) ([]Entry, error) {
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
	bw.ops = append(bw.ops, batchOp{
		opType:    OpPut,
		namespace: normNs,
		key:       []byte(key),
		valType:   ValTypeBytes,
		val:       val,
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

func (db *DB) Batch(fn func(b *BatchWriter) error) error {
	writer := &BatchWriter{db: db}
	if err := fn(writer); err != nil {
		// Rollback staging
		return err
	}

	if len(writer.ops) == 0 {
		return nil
	}

	var watchers []KeyChangeCallback
	var events []KeyChangeEvent

	db.mu.Lock()
	if db.options.ReadOnly {
		db.mu.Unlock()
		return NewLockedError("Database is opened in read-only mode")
	}
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

	if err := db.applyMutation(rawEntries, func() {
		for _, op := range writer.ops {
			switch op.opType {
			case OpPut:
				events = append(events, KeyChangeEvent{
					Op:        OpChangePut,
					Namespace: op.namespace,
					Key:       string(op.key),
					ValueType: op.valType,
				})
				db.putInternal(op.namespace, op.key, op.valType, op.val)
			case OpDelete:
				if store, ok := db.namespaces[op.namespace]; ok {
					if _, found := store.findIndex(op.key); found {
						events = append(events, KeyChangeEvent{
							Op:        OpChangeDelete,
							Namespace: op.namespace,
							Key:       string(op.key),
							ValueType: 0,
						})
						if db.snapshotCount > 0 {
							store = store.clone()
							db.namespaces[op.namespace] = store
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
				}
			}
		}
	}); err != nil {
		db.mu.Unlock()
		return err
	}
	if len(db.watchers) > 0 {
		watchers = make([]KeyChangeCallback, len(db.watchers))
		for i, slot := range db.watchers {
			watchers[i] = slot.cb
		}
	}
	db.mu.Unlock()

	for _, ev := range events {
		for _, w := range watchers {
			w(ev)
		}
	}
	return nil
}

func (db *DB) Compact() error {
	db.mu.Lock()
	defer db.mu.Unlock()
	lock, diskMaxTxID, err := db.beginFlush()
	if err != nil {
		return err
	}
	return db.commitFlush(lock, diskMaxTxID)
}

func (db *DB) putInternal(ns string, key []byte, vtype uint8, val []byte) {
	store, ok := db.namespaces[ns]
	if !ok {
		store = &namespaceStore{}
		db.namespaces[ns] = store
		db.rebuildSortedNs()
	} else if db.snapshotCount > 0 {
		// 存在存活快照：CoW 克隆后再改，冻结旧切片；
		// 无快照时原地修改，写路径保持 O(log N) 查找 + O(1) 分摊插入。
		store = store.clone()
		db.namespaces[ns] = store
	}
	store.put(key, vtype, val)
}

func (db *DB) rebuildSortedNs() {
	names := make([]string, 0, len(db.namespaces))
	for ns := range db.namespaces {
		names = append(names, ns)
	}
	sort.Slice(names, func(i, j int) bool {
		return CompareUtf8Bytes([]byte(names[i]), []byte(names[j])) < 0
	})
	db.sortedNsNames = names
}

func (db *DB) initAndRecover() error {
	tmpPath := db.path + ".tmp"
	bakPath := db.path + ".bak"

	// 1. Clean unfinished tmp
	if _, err := os.Stat(tmpPath); err == nil {
		if _, dbErr := os.Stat(db.path); os.IsNotExist(dbErr) && verifyFile(tmpPath) == nil {
			_ = renameWithRetry(tmpPath, db.path)
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

	// 3. Primary does not exist, check .bak
	if _, err := os.Stat(bakPath); err == nil {
		// `.bak` is a recovery source: load it as-is, never tail-truncate (G1).
		entries, maxTxID, size, loadErr := db.loadFileWith(bakPath, false)
		if loadErr == nil {
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
	}

	// New database
	db.currentMaxTxID = 0
	db.lastKnownDiskState.maxTxID = 0
	db.lastKnownDiskState.fileSize = 0
	return nil
}

// allowTailTruncation reports whether the *live* log may be healed by discarding
// a torn tail.
func (db *DB) allowTailTruncation() bool {
	return db.options.EngineMode == StorageEngineModeAppendOnlyLog && !db.options.ReadOnly
}

func (db *DB) loadFile(path string) ([]*RawOpEntry, uint64, int64, error) {
	return db.loadFileWith(path, db.allowTailTruncation())
}

// loadFileWith loads a file under an explicit tail-truncation policy.
//
// Recovery sources (`.bak`) are always loaded with allowTailTruncation = false:
// truncating a recovery source would destroy the data it is kept for (G1).
func (db *DB) loadFileWith(path string, allowTailTruncation bool) ([]*RawOpEntry, uint64, int64, error) {
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
func (db *DB) applyEntries(entries []*RawOpEntry) error {
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
func (db *DB) beginFlush() (*FileLock, uint64, error) {
	if db.options.ReadOnly {
		return nil, 0, NewLockedError("Database is opened in read-only mode")
	}

	lock, err := AcquireLock(db.path, db.options)
	if err != nil {
		return nil, 0, err
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
			lock.Release()
			return nil, 0, NewConcurrentModificationError(fmt.Sprintf(
				"Disk state changed externally: expected (%d, %d), found (%d, %d)",
				db.lastKnownDiskState.maxTxID, db.lastKnownDiskState.fileSize,
				diskMaxTxID, diskFileSize,
			))
		}
	}
	return lock, diskMaxTxID, nil
}

// commitFlush 是写路径的第二阶段：把内存状态序列化进 .tmp、fsync 后经
// .tmp -> .db 原子轮转换入（上一代保留为 .bak）。消费 beginFlush 取得的锁，
// 无论成功或失败都会释放它。
func (db *DB) commitFlush(lock *FileLock, diskMaxTxID uint64) error {
	defer lock.Release()

	tmpPath := db.path + ".tmp"
	bakPath := db.path + ".bak"

	tmpFile, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0666)
	if err != nil {
		return err
	}

	// Write 32B file header
	header := CreateFileHeader()
	if _, err := tmpFile.Write(header[:]); err != nil {
		_ = tmpFile.Close()
		return err
	}

	// Collect sorted alive entries
	var sortedEntries []*RawOpEntry
	for _, ns := range db.sortedNsNames {
		store := db.namespaces[ns]
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

	finalMaxTxID := db.currentMaxTxID
	if diskMaxTxID > finalMaxTxID {
		finalMaxTxID = diskMaxTxID
	}

	if len(sortedEntries) > 0 {
		currentTxID := finalMaxTxID + 1
		var currentFrame []*RawOpEntry
		currentPayloadLen := 0

		for _, entry := range sortedEntries {
			entrySize := entry.EncodedLen()
			if len(currentFrame) >= MaxOpsPerFrame || (currentPayloadLen+entrySize > MaxPayloadLen) {
				if err := WriteBatchFrame(tmpFile, currentTxID, currentFrame); err != nil {
					_ = tmpFile.Close()
					return err
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
				return err
			}
			finalMaxTxID = currentTxID
		}
	}

	_ = tmpFile.Sync()
	_ = tmpFile.Close()

	// Atomic rotation
	if _, err := os.Stat(db.path); err == nil {
		if err := renameWithRetry(db.path, bakPath); err != nil {
			return err
		}
	}
	if err := renameWithRetry(tmpPath, db.path); err != nil {
		return err
	}

	db.currentMaxTxID = finalMaxTxID
	if fi, statErr := os.Stat(db.path); statErr == nil {
		db.lastKnownDiskState.fileSize = fi.Size()
	}
	db.lastKnownDiskState.maxTxID = finalMaxTxID
	db.isDirty = false

	return nil
}

// appendEntries appends the mutation's frames to the log and returns the
// resulting file size. Compaction is deliberately *not* triggered here: the
// snapshot a compaction writes is taken from the current in-memory state, so it
// may only run once the mutation has been applied to memory (C1).
func (db *DB) appendEntries(entries []*RawOpEntry) (int64, error) {
	if len(entries) == 0 {
		return 0, nil
	}

	lock, diskMaxTxID, err := db.beginFlush()
	if err != nil {
		return 0, err
	}
	defer lock.Release()

	fileExists := false
	if _, statErr := os.Stat(db.path); statErr == nil {
		fileExists = true
	}

	f, err := os.OpenFile(db.path, os.O_CREATE|os.O_RDWR, 0666)
	if err != nil {
		return 0, err
	}
	defer f.Close()

	fi, statErr := f.Stat()
	if !fileExists || (statErr == nil && fi.Size() == 0) {
		hdr := CreateFileHeader()
		if _, err := f.Write(hdr[:]); err != nil {
			return 0, err
		}
	}
	if _, err := f.Seek(0, io.SeekEnd); err != nil {
		return 0, err
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

	newFi, err := f.Stat()
	if err != nil {
		return 0, err
	}
	newFileSize := newFi.Size()
	db.currentMaxTxID = currentTxID
	db.lastKnownDiskState.fileSize = newFileSize
	db.lastKnownDiskState.maxTxID = currentTxID

	return newFileSize, nil
}

func (db *DB) applyMutation(entries []*RawOpEntry, applyMem func()) error {
	if db.options.ReadOnly {
		return NewLockedError("Database is opened in read-only mode")
	}
	if db.options.EngineMode == StorageEngineModeAppendOnlyLog {
		// Order matters (C1/C2):
		//   1. append the frames (durability first),
		//   2. apply the mutation to the in-memory state,
		//   3. only now consider compaction, whose snapshot is serialised from the
		//      in-memory state and must therefore already contain this mutation.
		// appendEntries releases the `.db` lock before returning, so the compaction
		// below acquires it in its own lock cycle instead of colliding with the
		// lock the caller used to hold (C2).
		newFileSize, err := db.appendEntries(entries)
		if err != nil {
			return err
		}
		applyMem()

		if db.options.CompactionThresholdBytes > 0 && uint64(newFileSize) >= db.options.CompactionThresholdBytes {
			compLock, diskMax, err := db.beginFlush()
			if err != nil {
				return err
			}
			if err := db.commitFlush(compLock, diskMax); err != nil {
				return err
			}
		}
	} else if db.options.SyncMode == SyncModeImmediate {
		lock, diskMaxTxID, err := db.beginFlush()
		if err != nil {
			return err
		}
		applyMem()
		if err := db.commitFlush(lock, diskMaxTxID); err != nil {
			return err
		}
	} else {
		applyMem()
		db.currentMaxTxID++
		db.isDirty = true
	}
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
// ERROR_ACCESS_DENIED=5 / ERROR_SHARING_VIOLATION=32 / ERROR_LOCK_VIOLATION=33，
// 在 Go 的 syscall 映射中分别为 EACCES / EBUSY / ETXTBSY。
func isWindowsSharingViolation(err error) bool {
	if runtime.GOOS != "windows" {
		return false
	}
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return errno == syscall.EACCES || errno == syscall.EBUSY || errno == syscall.ETXTBSY
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
	db         *DB
	txID       uint64
	namespaces map[string]*namespaceStore
	released   int32 // CAS 保护，防止 Close 与 finalizer 双重释放
}

func (db *DB) Snapshot() *Snapshot {
	db.mu.Lock()
	defer db.mu.Unlock()

	snapNs := make(map[string]*namespaceStore, len(db.namespaces))
	for k, v := range db.namespaces {
		snapNs[k] = v
	}
	snap := &Snapshot{
		db:         db,
		txID:       db.currentMaxTxID,
		namespaces: snapNs,
	}
	db.snapshotCount++
	runtime.SetFinalizer(snap, func(s *Snapshot) { s.release() })
	return snap
}

func (s *Snapshot) release() {
	if s.db != nil && atomic.CompareAndSwapInt32(&s.released, 0, 1) {
		s.db.mu.Lock()
		if s.db.snapshotCount > 0 {
			s.db.snapshotCount--
		}
		s.db.mu.Unlock()
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
