package ckv

import (
	"hash/crc32"
)

// IEEE 802.3 CRC32 implementation using Go standard library hash/crc32.
// Uses hardware-accelerated SSE4.2 / ARMv8 instructions with zero external dependencies.

// Crc32 computes IEEE 802.3 CRC32 checksum for the given data slice.
func Crc32(data []byte) uint32 {
	return crc32.ChecksumIEEE(data)
}
