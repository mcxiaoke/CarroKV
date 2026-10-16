package ckv

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"math"
	"unicode/utf8"
)

var (
	FileMagic = []byte("UKVDB003")
)

const (
	FrameMagic       uint32 = 0x4D52464B // 'KFRM' in little endian
	FormatReadVer    uint16 = 3
	FormatWriteVer   uint16 = 3
	FileHeaderLen    int    = 32
	FrameHeaderLen   int    = 28
	EntryFixedHeader int    = 10

	MaxNsLen         = 255
	MaxKeyLen        = 65535
	MaxValLen        = 16 * 1024 * 1024 // 16 MB
	MaxPayloadLen    = 64 * 1024 * 1024 // 64 MB
	MaxOpsPerFrame   = 1000000

	OpPut           uint8 = 0x01
	OpDelete        uint8 = 0x02
	OpDropNamespace uint8 = 0x03

	ValTypeBytes   uint8 = 0x00
	ValTypeString  uint8 = 0x01
	ValTypeInt64   uint8 = 0x02
	ValTypeFloat64 uint8 = 0x03
	ValTypeBool    uint8 = 0x04

	DefaultNamespaceAlias = "default"
	CanonicalQuietNaNBits = uint64(0x7FF8000000000000)
)

func EncodeFloat64(val float64) []byte {
	buf := make([]byte, 8)
	if math.IsNaN(val) {
		binary.LittleEndian.PutUint64(buf, CanonicalQuietNaNBits)
	} else {
		binary.LittleEndian.PutUint64(buf, math.Float64bits(val))
	}
	return buf
}

func DecodeFloat64(buf []byte) (float64, error) {
	if len(buf) != 8 {
		return 0, NewCorruptedError(fmt.Sprintf("Expected 8 bytes for float64, got %d", len(buf)))
	}
	bits := binary.LittleEndian.Uint64(buf)
	return math.Float64frombits(bits), nil
}

func EncodeInt64(val int64) []byte {
	buf := make([]byte, 8)
	binary.LittleEndian.PutUint64(buf, uint64(val))
	return buf
}

func DecodeInt64(buf []byte) (int64, error) {
	if len(buf) != 8 {
		return 0, NewCorruptedError(fmt.Sprintf("Expected 8 bytes for int64, got %d", len(buf)))
	}
	return int64(binary.LittleEndian.Uint64(buf)), nil
}

func EncodeBool(val bool) []byte {
	if val {
		return []byte{0x01}
	}
	return []byte{0x00}
}

func DecodeBool(buf []byte) (bool, error) {
	if len(buf) == 0 {
		return false, NewCorruptedError("Expected at least 1 byte for bool")
	}
	return buf[0] != 0, nil
}

// Entry representation returned by ScanRange.
type Entry struct {
	Namespace string
	Key       string
	ValueType uint8
	RawBytes  []byte
}

func (e *Entry) IsBytes() bool   { return e.ValueType == ValTypeBytes }
func (e *Entry) IsString() bool  { return e.ValueType == ValTypeString }
func (e *Entry) IsInt64() bool   { return e.ValueType == ValTypeInt64 }
func (e *Entry) IsFloat64() bool { return e.ValueType == ValTypeFloat64 }
func (e *Entry) IsBool() bool    { return e.ValueType == ValTypeBool }

func (e *Entry) StringValue() (string, error) {
	if e.ValueType != ValTypeString {
		return "", NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected string", e.Key, e.ValueType))
	}
	if !utf8.Valid(e.RawBytes) {
		return "", NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 byte sequence in key '%s'", e.Key))
	}
	return string(e.RawBytes), nil
}

func (e *Entry) AsString() (string, error) {
	return e.StringValue()
}

func (e *Entry) AsInt64() (int64, error) {
	if e.ValueType != ValTypeInt64 {
		return 0, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected int64", e.Key, e.ValueType))
	}
	return DecodeInt64(e.RawBytes)
}

func (e *Entry) Int64Value() (int64, error) {
	return e.AsInt64()
}

func (e *Entry) AsFloat64() (float64, error) {
	if e.ValueType != ValTypeFloat64 {
		return 0, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected float64", e.Key, e.ValueType))
	}
	return DecodeFloat64(e.RawBytes)
}

func (e *Entry) Float64Value() (float64, error) {
	return e.AsFloat64()
}

func (e *Entry) AsBool() (bool, error) {
	if e.ValueType != ValTypeBool {
		return false, NewTypeMismatchError(fmt.Sprintf("Key '%s' has value_type %d, expected bool", e.Key, e.ValueType))
	}
	return DecodeBool(e.RawBytes)
}

func (e *Entry) BoolValue() (bool, error) {
	return e.AsBool()
}

// CompareUtf8Bytes compares two byte slices using 0..255 unsigned lexicographical order.
func CompareUtf8Bytes(a, b []byte) int {
	return bytes.Compare(a, b) // Go bytes.Compare is already unsigned 0..255 lexicographical!
}

// NormalizeNamespace normalizes namespace string.
// Empty string and "default" are normalized to "".
func NormalizeNamespace(ns string) (string, error) {
	if len(ns) > MaxNsLen {
		return "", NewInvalidNamespaceError(fmt.Sprintf(
			"Namespace byte length %d exceeds maximum allowed %d",
			len(ns), MaxNsLen,
		))
	}
	if ns == "" || ns == DefaultNamespaceAlias {
		return "", nil
	}
	return ns, nil
}

// KeyChangeOp 数据变更事件类型。
// 数值与设计规范 §5.2.3 逐字对齐，属于跨语言稳定 ABI，严禁重排。
type KeyChangeOp int

const (
	OpChangePut           KeyChangeOp = 1 // 键新增或值更新（含 incrementInt64）
	OpChangeDelete        KeyChangeOp = 2 // 键被删除
	OpChangeDropNamespace KeyChangeOp = 3 // 整个命名空间被删除（Key 为空串）
	OpChangeClear         KeyChangeOp = 4 // 命名空间被清空（Key 为空串）
)

type KeyChangeEvent struct {
	Op        KeyChangeOp
	Namespace string
	Key       string
	ValueType uint8
}

type KeyChangeCallback func(event KeyChangeEvent)

// SyncMode 数据同步落盘模式。
type SyncMode int

const (
	SyncModeImmediate SyncMode = 0 // 每次写立即落盘并 fsync
	SyncModeDeferred  SyncMode = 1 // 异步延迟落盘（后台定时器或显式 Flush）
	SyncModeManual    SyncMode = 2 // 手动落盘（仅显式 Flush 或 Close 时落盘）
)

// StorageEngineMode 底层存储引擎模式。
type StorageEngineMode int

const (
	StorageEngineSnapshotAtomic StorageEngineMode = 1 // 阶段一：整库快照原子轮转（默认）
	StorageEngineAppendOnlyLog  StorageEngineMode = 2 // 阶段二：增量追加日志（WAL）与压实

	StorageEngineModeSnapshotAtomic = StorageEngineSnapshotAtomic
	StorageEngineModeAppendOnlyLog  = StorageEngineAppendOnlyLog
)

