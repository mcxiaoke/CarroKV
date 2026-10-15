package ckv

// IEEE 802.3 CRC32 implementation using 256-entry lookup table.
// Zero external dependencies.

const crc32Polynomial uint32 = 0xEDB88320

var crcTable [256]uint32

func init() {
	for i := 0; i < 256; i++ {
		crc := uint32(i)
		for j := 0; j < 8; j++ {
			if (crc & 1) != 0 {
				crc = (crc >> 1) ^ crc32Polynomial
			} else {
				crc >>= 1
			}
		}
		crcTable[i] = crc
	}
}

// Crc32 computes IEEE 802.3 CRC32 checksum for the given data slice.
func Crc32(data []byte) uint32 {
	crc := uint32(0xFFFFFFFF)
	for _, b := range data {
		index := byte(crc ^ uint32(b))
		crc = (crc >> 8) ^ crcTable[index]
	}
	return crc ^ 0xFFFFFFFF
}
