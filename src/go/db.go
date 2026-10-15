package ckv

import (
	"bytes"
	"fmt"
	"io"
	"os"
	"sort"
	"sync"
	"time"
	"unicode/utf8"
)

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

type DB struct {
	mu                 sync.RWMutex
	path               string
	options            DbOptions
	namespaces         map[string]*namespaceStore
	sortedNsNames      []string
	currentMaxTxID     uint64
	lastKnownDiskState struct {
		maxTxID  uint64
		fileSize int64
	}
}

func Open(path string, options ...DbOptions) (*DB, error) {
	opts := DefaultDbOptions()
	if len(options) > 0 {
		opts = options[0]
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

	return db, nil
}

func (db *DB) Close() error {
	// In-memory state released
	return nil
}

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
	if !utf8.Valid(item.value) {
		return nil, NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 in key '%s'", key))
	}
	s := string(item.value)
	return &s, nil
}

func (db *DB) PutBytes(ns, key string, val []byte) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	if err := validateLengths(key, val); err != nil {
		return err
	}

	db.mu.Lock()
	defer db.mu.Unlock()

	db.putInternal(normNs, []byte(key), ValTypeBytes, val)
	return db.flushSnapshot()
}

func (db *DB) PutString(ns, key, val string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}
	valBytes := []byte(val)
	if err := validateLengths(key, valBytes); err != nil {
		return err
	}

	db.mu.Lock()
	defer db.mu.Unlock()

	db.putInternal(normNs, []byte(key), ValTypeString, valBytes)
	return db.flushSnapshot()
}

func (db *DB) Delete(ns, key string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}

	db.mu.Lock()
	defer db.mu.Unlock()

	if store, ok := db.namespaces[normNs]; ok {
		store.delete([]byte(key))
	}
	return db.flushSnapshot()
}

func (db *DB) DropNamespace(ns string) error {
	normNs, err := NormalizeNamespace(ns)
	if err != nil {
		return err
	}

	db.mu.Lock()
	defer db.mu.Unlock()

	delete(db.namespaces, normNs)
	db.rebuildSortedNs()
	return db.flushSnapshot()
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

	var keys []string
	for _, item := range store.items {
		if len(pfx) == 0 || bytes.HasPrefix(item.key, pfx) {
			keys = append(keys, string(item.key))
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

func (db *DB) Batch(fn func(b *BatchWriter) error) error {
	writer := &BatchWriter{db: db}
	if err := fn(writer); err != nil {
		// Rollback staging
		return err
	}

	if len(writer.ops) == 0 {
		return nil
	}

	db.mu.Lock()
	defer db.mu.Unlock()

	for _, op := range writer.ops {
		switch op.opType {
		case OpPut:
			db.putInternal(op.namespace, op.key, op.valType, op.val)
		case OpDelete:
			if store, ok := db.namespaces[op.namespace]; ok {
				store.delete(op.key)
			}
		case OpDropNamespace:
			delete(db.namespaces, op.namespace)
			db.rebuildSortedNs()
		}
	}

	return db.flushSnapshot()
}

func (db *DB) Compact() error {
	db.mu.Lock()
	defer db.mu.Unlock()
	return db.flushSnapshot()
}

func (db *DB) putInternal(ns string, key []byte, vtype uint8, val []byte) {
	store, ok := db.namespaces[ns]
	if !ok {
		store = &namespaceStore{}
		db.namespaces[ns] = store
		db.rebuildSortedNs()
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
			db.applyEntries(entries)
			db.currentMaxTxID = maxTxID
			db.lastKnownDiskState.maxTxID = maxTxID
			db.lastKnownDiskState.fileSize = size
			return nil
		}
		// Primary corrupted, check .bak
		if _, bakErr := os.Stat(bakPath); bakErr == nil {
			bakEntries, bakTxID, bakSize, bakLoadErr := db.loadFile(bakPath)
			if bakLoadErr == nil {
				_ = copyFile(bakPath, db.path)
				if db.options.OnWarning != nil {
					db.options.OnWarning("onRecoveredFromBackup", "Recovered database from .bak")
				}
				db.applyEntries(bakEntries)
				db.currentMaxTxID = bakTxID
				db.lastKnownDiskState.maxTxID = bakTxID
				db.lastKnownDiskState.fileSize = bakSize
				return nil
			}
		}
		return loadErr
	}

	// 3. Primary does not exist, check .bak
	if _, err := os.Stat(bakPath); err == nil {
		entries, maxTxID, size, loadErr := db.loadFile(bakPath)
		if loadErr == nil {
			_ = copyFile(bakPath, db.path)
			if db.options.OnWarning != nil {
				db.options.OnWarning("onRecoveredFromBackup", "Recovered database from .bak")
			}
			db.applyEntries(entries)
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

func (db *DB) loadFile(path string) ([]*RawOpEntry, uint64, int64, error) {
	f, err := os.Open(path)
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

	entries, maxTxID, err := LoadAllFrames(f, size)
	if err != nil {
		return nil, 0, 0, err
	}

	return entries, maxTxID, size, nil
}

func (db *DB) applyEntries(entries []*RawOpEntry) {
	for _, e := range entries {
		ns := string(e.Namespace)
		switch e.OpCode {
		case OpPut:
			db.putInternal(ns, e.Key, e.ValueType, e.Value)
		case OpDelete:
			if store, ok := db.namespaces[ns]; ok {
				store.delete(e.Key)
			}
		case OpDropNamespace:
			delete(db.namespaces, ns)
			db.rebuildSortedNs()
		}
	}
}

func (db *DB) flushSnapshot() error {
	if db.options.ReadOnly {
		return NewLockedError("Database is opened in read-only mode")
	}

	lock, err := AcquireLock(db.path, db.options)
	if err != nil {
		return err
	}
	defer lock.Release()

	// Pre-check disk state
	var diskMaxTxID uint64
	var diskFileSize int64
	if fi, statErr := os.Stat(db.path); statErr == nil {
		diskFileSize = fi.Size()
		if diskFileSize >= int64(FileHeaderLen) {
			f, oErr := os.Open(db.path)
			if oErr == nil {
				diskMaxTxID, _ = ScanMaxTxID(f, diskFileSize)
				_ = f.Close()
			}
		}
		if db.lastKnownDiskState.fileSize > 0 &&
			(diskMaxTxID != db.lastKnownDiskState.maxTxID || diskFileSize != db.lastKnownDiskState.fileSize) {
			return NewConcurrentModificationError(fmt.Sprintf(
				"Disk state changed externally: expected (%d, %d), found (%d, %d)",
				db.lastKnownDiskState.maxTxID, db.lastKnownDiskState.fileSize,
				diskMaxTxID, diskFileSize,
			))
		}
	}

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

func renameWithRetry(src, dst string) error {
	backoffs := []time.Duration{5 * time.Millisecond, 15 * time.Millisecond, 30 * time.Millisecond, 60 * time.Millisecond, 120 * time.Millisecond}
	for _, wait := range backoffs {
		err := os.Rename(src, dst)
		if err == nil {
			return nil
		}
		time.Sleep(wait)
	}
	return os.Rename(src, dst)
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
