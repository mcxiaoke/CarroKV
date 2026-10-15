package ckv

import (
	"bytes"
	"fmt"
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

	ValTypeBytes  uint8 = 0x00
	ValTypeString uint8 = 0x01

	DefaultNamespaceAlias = "default"
)

// Entry representation returned by ScanRange.
type Entry struct {
	Namespace string
	Key       string
	ValueType uint8
	RawBytes  []byte
}

func (e *Entry) StringValue() (string, error) {
	if !utf8.Valid(e.RawBytes) {
		return "", NewInvalidUtf8Error(fmt.Sprintf("Invalid UTF-8 byte sequence in key '%s'", e.Key))
	}
	return string(e.RawBytes), nil
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
