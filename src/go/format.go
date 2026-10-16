package ckv

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
)

type RawOpEntry struct {
	OpCode    uint8
	ValueType uint8
	Namespace []byte
	Key       []byte
	Value     []byte
}

func (e *RawOpEntry) EncodedLen() int {
	return EntryFixedHeader + len(e.Namespace) + len(e.Key) + len(e.Value)
}

func (e *RawOpEntry) Encode(out []byte) int {
	out[0] = e.OpCode
	out[1] = e.ValueType
	binary.LittleEndian.PutUint16(out[2:4], uint16(len(e.Namespace)))
	binary.LittleEndian.PutUint16(out[4:6], uint16(len(e.Key)))
	binary.LittleEndian.PutUint32(out[6:10], uint32(len(e.Value)))

	off := EntryFixedHeader
	copy(out[off:], e.Namespace)
	off += len(e.Namespace)
	copy(out[off:], e.Key)
	off += len(e.Key)
	copy(out[off:], e.Value)
	off += len(e.Value)
	return off
}

func DecodeRawOpEntry(buf []byte, offset *int) (*RawOpEntry, error) {
	if len(buf) < *offset+EntryFixedHeader {
		return nil, NewCorruptedError("Truncated entry header")
	}
	opCode := buf[*offset]
	valType := buf[*offset+1]
	nsLen := int(binary.LittleEndian.Uint16(buf[*offset+2 : *offset+4]))
	keyLen := int(binary.LittleEndian.Uint16(buf[*offset+4 : *offset+6]))
	valLen := int(binary.LittleEndian.Uint32(buf[*offset+6 : *offset+10]))
	*offset += EntryFixedHeader

	if len(buf) < *offset+nsLen+keyLen+valLen {
		return nil, NewCorruptedError("Truncated entry payload")
	}

	ns := make([]byte, nsLen)
	copy(ns, buf[*offset:*offset+nsLen])
	*offset += nsLen

	key := make([]byte, keyLen)
	copy(key, buf[*offset:*offset+keyLen])
	*offset += keyLen

	val := make([]byte, valLen)
	copy(val, buf[*offset:*offset+valLen])
	*offset += valLen

	return &RawOpEntry{
		OpCode:    opCode,
		ValueType: valType,
		Namespace: ns,
		Key:       key,
		Value:     val,
	}, nil
}

func CreateFileHeader() [FileHeaderLen]byte {
	var buf [FileHeaderLen]byte
	copy(buf[0:8], FileMagic)
	binary.LittleEndian.PutUint16(buf[8:10], FormatReadVer)
	binary.LittleEndian.PutUint16(buf[10:12], FormatWriteVer)
	binary.LittleEndian.PutUint32(buf[12:16], 0) // HeaderFlags
	// 16..28 reserved 0x00
	crc := Crc32(buf[0:28])
	binary.LittleEndian.PutUint32(buf[28:32], crc)
	return buf
}

func VerifyFileHeader(buf []byte) error {
	if len(buf) < FileHeaderLen {
		return NewCorruptedError("File header too short")
	}
	if string(buf[0:8]) != string(FileMagic) {
		return NewCorruptedError("Invalid file magic")
	}
	readVer := binary.LittleEndian.Uint16(buf[8:10])
	if readVer > FormatReadVer {
		return NewUnsupportedVersionError(fmt.Sprintf(
			"File requires read version %d, but engine only supports version %d",
			readVer, FormatReadVer,
		))
	}
	expectedCRC := binary.LittleEndian.Uint32(buf[28:32])
	actualCRC := Crc32(buf[0:28])
	if actualCRC != expectedCRC {
		return NewCorruptedError(fmt.Sprintf(
			"File header CRC mismatch: expected 0x%08X, got 0x%08X",
			expectedCRC, actualCRC,
		))
	}
	return nil
}

func WriteBatchFrame(w io.Writer, txID uint64, entries []*RawOpEntry) error {
	if len(entries) == 0 {
		return NewCorruptedError("OpCount must be >= 1 for a frame")
	}
	if len(entries) > MaxOpsPerFrame {
		return NewCorruptedError(fmt.Sprintf("OpCount %d exceeds limit", len(entries)))
	}

	payloadLen := 0
	for _, e := range entries {
		payloadLen += e.EncodedLen()
	}
	if payloadLen > MaxPayloadLen {
		return NewCorruptedError(fmt.Sprintf("Payload length %d exceeds max %d", payloadLen, MaxPayloadLen))
	}

	payload := make([]byte, payloadLen)
	off := 0
	for _, e := range entries {
		off += e.Encode(payload[off:])
	}

	payloadCRC := Crc32(payload)

	var tail20 [20]byte
	binary.LittleEndian.PutUint64(tail20[0:8], txID)
	binary.LittleEndian.PutUint32(tail20[8:12], uint32(len(entries)))
	binary.LittleEndian.PutUint32(tail20[12:16], uint32(payloadLen))
	binary.LittleEndian.PutUint32(tail20[16:20], payloadCRC)

	headerCRC := Crc32(tail20[:])

	var header28 [FrameHeaderLen]byte
	binary.LittleEndian.PutUint32(header28[0:4], FrameMagic)
	binary.LittleEndian.PutUint32(header28[4:8], headerCRC)
	copy(header28[8:28], tail20[:])

	if _, err := w.Write(header28[:]); err != nil {
		return err
	}
	if _, err := w.Write(payload); err != nil {
		return err
	}
	return nil
}

func LoadAllFrames(r io.ReadSeeker, fileSize int64) ([]*RawOpEntry, uint64, error) {
	var entries []*RawOpEntry
	var lastTxID uint64
	offset := int64(FileHeaderLen)

	if _, err := r.Seek(offset, io.SeekStart); err != nil {
		return nil, 0, err
	}

	for offset < fileSize {
		if fileSize-offset < int64(FrameHeaderLen) {
			return nil, 0, NewCorruptedError(fmt.Sprintf("Remaining bytes insufficient for frame header at offset %d", offset))
		}

		var header28 [FrameHeaderLen]byte
		if _, err := io.ReadFull(r, header28[:]); err != nil {
			return nil, 0, err
		}

		magic := binary.LittleEndian.Uint32(header28[0:4])
		if magic != FrameMagic {
			return nil, 0, NewCorruptedError(fmt.Sprintf("Invalid frame magic 0x%08X at offset %d", magic, offset))
		}

		expectedHdrCRC := binary.LittleEndian.Uint32(header28[4:8])
		actualHdrCRC := Crc32(header28[8:28])
		if actualHdrCRC != expectedHdrCRC {
			return nil, 0, NewCorruptedError(fmt.Sprintf(
				"Frame header CRC mismatch at offset %d: expected 0x%08X, got 0x%08X",
				offset, expectedHdrCRC, actualHdrCRC,
			))
		}

		txID := binary.LittleEndian.Uint64(header28[8:16])
		if txID <= lastTxID {
			return nil, 0, NewCorruptedError(fmt.Sprintf(
				"Non-monotonic TxID %d (last was %d) at offset %d",
				txID, lastTxID, offset,
			))
		}

		opCount := int(binary.LittleEndian.Uint32(header28[16:20]))
		if opCount == 0 || opCount > MaxOpsPerFrame {
			return nil, 0, NewCorruptedError(fmt.Sprintf("Invalid OpCount %d at offset %d", opCount, offset))
		}

		payloadLen := int(binary.LittleEndian.Uint32(header28[20:24]))
		if payloadLen > MaxPayloadLen {
			return nil, 0, NewCorruptedError(fmt.Sprintf("Payload length %d exceeds limit at offset %d", payloadLen, offset))
		}

		expectedPayloadCRC := binary.LittleEndian.Uint32(header28[24:28])

		if fileSize-(offset+int64(FrameHeaderLen)) < int64(payloadLen) {
			return nil, 0, NewCorruptedError(fmt.Sprintf("File truncated inside payload at offset %d", offset))
		}

		payload := make([]byte, payloadLen)
		if _, err := io.ReadFull(r, payload); err != nil {
			return nil, 0, err
		}

		actualPayloadCRC := Crc32(payload)
		if actualPayloadCRC != expectedPayloadCRC {
			return nil, 0, NewCorruptedError(fmt.Sprintf(
				"Payload CRC mismatch at offset %d: expected 0x%08X, got 0x%08X",
				offset, expectedPayloadCRC, actualPayloadCRC,
			))
		}

		payloadOff := 0
		for i := 0; i < opCount; i++ {
			entry, err := DecodeRawOpEntry(payload, &payloadOff)
			if err != nil {
				return nil, 0, err
			}
			entries = append(entries, entry)
		}

		lastTxID = txID
		offset += int64(FrameHeaderLen + payloadLen)
	}

	return entries, lastTxID, nil
}

func ScanMaxTxID(r io.ReadSeeker, fileSize int64) (uint64, error) {
	var lastTxID uint64
	offset := int64(FileHeaderLen)

	for offset < fileSize {
		if fileSize-offset < int64(FrameHeaderLen) {
			return 0, NewCorruptedError("Truncated frame in fast scan")
		}
		if _, err := r.Seek(offset, io.SeekStart); err != nil {
			return 0, err
		}
		var header28 [FrameHeaderLen]byte
		if _, err := io.ReadFull(r, header28[:]); err != nil {
			return 0, err
		}

		magic := binary.LittleEndian.Uint32(header28[0:4])
		if magic != FrameMagic {
			return 0, NewCorruptedError("Invalid frame magic in fast scan")
		}

		txID := binary.LittleEndian.Uint64(header28[8:16])
		payloadLen := binary.LittleEndian.Uint32(header28[20:24])

		lastTxID = txID
		offset += int64(FrameHeaderLen) + int64(payloadLen)
	}

	return lastTxID, nil
}

func LoadAllFramesWithRecovery(file *os.File, fileSize int64, allowTailTruncation bool, onWarning WarningCallback) ([]*RawOpEntry, uint64, int64, error) {
	var entries []*RawOpEntry
	var lastTxID uint64
	offset := int64(FileHeaderLen)
	effectiveSize := fileSize

	if _, err := file.Seek(offset, io.SeekStart); err != nil {
		return nil, 0, 0, err
	}

	for offset < effectiveSize {
		remaining := effectiveSize - offset
		if remaining < int64(FrameHeaderLen) {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated partial frame header at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, NewCorruptedError(fmt.Sprintf("Remaining bytes insufficient for frame header at offset %d", offset))
		}

		var header28 [FrameHeaderLen]byte
		if _, err := io.ReadFull(file, header28[:]); err != nil {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated unreadable frame header at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, err
		}

		magic := binary.LittleEndian.Uint32(header28[0:4])
		expectedHdrCRC := binary.LittleEndian.Uint32(header28[4:8])
		actualHdrCRC := Crc32(header28[8:28])
		txID := binary.LittleEndian.Uint64(header28[8:16])
		opCount := int(binary.LittleEndian.Uint32(header28[16:20]))
		payloadLen := int(binary.LittleEndian.Uint32(header28[20:24]))
		expectedPayloadCRC := binary.LittleEndian.Uint32(header28[24:28])

		if magic != FrameMagic || actualHdrCRC != expectedHdrCRC || txID <= lastTxID ||
			opCount == 0 || opCount > MaxOpsPerFrame || payloadLen > MaxPayloadLen {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated invalid frame header at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, NewCorruptedError(fmt.Sprintf("Invalid frame header at offset %d", offset))
		}

		if effectiveSize-(offset+int64(FrameHeaderLen)) < int64(payloadLen) {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated incomplete payload at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, NewCorruptedError(fmt.Sprintf("File truncated inside payload at offset %d", offset))
		}

		payload := make([]byte, payloadLen)
		if _, err := io.ReadFull(file, payload); err != nil {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated unreadable payload at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, err
		}

		actualPayloadCRC := Crc32(payload)
		if actualPayloadCRC != expectedPayloadCRC {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated payload CRC mismatch at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, NewCorruptedError(fmt.Sprintf("Payload CRC mismatch at offset %d", offset))
		}

		payloadOff := 0
		decodeErr := false
		var frameEntries []*RawOpEntry
		for i := 0; i < opCount; i++ {
			entry, err := DecodeRawOpEntry(payload, &payloadOff)
			if err != nil {
				decodeErr = true
				break
			}
			frameEntries = append(frameEntries, entry)
		}

		if decodeErr {
			if allowTailTruncation {
				_ = file.Truncate(offset)
				_ = file.Sync()
				if onWarning != nil {
					onWarning("onTailTruncated", "Truncated corrupt entry in payload at tail")
				}
				effectiveSize = offset
				break
			}
			return nil, 0, 0, NewCorruptedError(fmt.Sprintf("Failed to decode entry in payload at offset %d", offset))
		}

		entries = append(entries, frameEntries...)
		lastTxID = txID
		offset += int64(FrameHeaderLen + payloadLen)
	}

	return entries, lastTxID, effectiveSize, nil
}
