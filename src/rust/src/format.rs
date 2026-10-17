use crate::crc32::crc32;
use crate::errors::{DbError, Result};
use crate::types::*;
use std::io::{Read, Seek, SeekFrom, Write};

pub const FILE_HEADER_LEN: usize = 32;
pub const FRAME_HEADER_LEN: usize = 28;

pub struct FileHeader;

impl FileHeader {
    pub fn create_bytes() -> [u8; FILE_HEADER_LEN] {
        let mut buf = [0u8; FILE_HEADER_LEN];
        buf[0..8].copy_from_slice(&FILE_MAGIC);
        buf[8..10].copy_from_slice(&FORMAT_READ_VER.to_le_bytes());
        buf[10..12].copy_from_slice(&FORMAT_WRITE_VER.to_le_bytes());
        buf[12..16].copy_from_slice(&0u32.to_le_bytes()); // HeaderFlags
                                                          // 16..28 are reserved 0x00
        let crc = crc32(&buf[0..28]);
        buf[28..32].copy_from_slice(&crc.to_le_bytes());
        buf
    }

    pub fn verify_bytes(buf: &[u8; FILE_HEADER_LEN]) -> Result<()> {
        if buf[0..8] != FILE_MAGIC {
            return Err(DbError::Corrupted("Invalid file magic".to_string()));
        }
        let read_ver = u16::from_le_bytes(buf[8..10].try_into().unwrap());
        if read_ver > FORMAT_READ_VER {
            return Err(DbError::UnsupportedVersion(format!(
                "File requires read version {}, but engine only supports version {}",
                read_ver, FORMAT_READ_VER
            )));
        }
        let expected_crc = u32::from_le_bytes(buf[28..32].try_into().unwrap());
        let actual_crc = crc32(&buf[0..28]);
        if actual_crc != expected_crc {
            return Err(DbError::Corrupted(format!(
                "File header CRC mismatch: expected 0x{:08X}, got 0x{:08X}",
                expected_crc, actual_crc
            )));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawOpEntry {
    pub op_code: u8,
    pub value_type: u8,
    pub namespace: Vec<u8>,
    pub key: Vec<u8>,
    pub value: Vec<u8>,
}

impl RawOpEntry {
    pub fn encoded_len(&self) -> usize {
        ENTRY_FIXED_HEADER_LEN + self.namespace.len() + self.key.len() + self.value.len()
    }

    pub fn encode(&self, out: &mut Vec<u8>) {
        out.push(self.op_code);
        out.push(self.value_type);
        out.extend_from_slice(&(self.namespace.len() as u16).to_le_bytes());
        out.extend_from_slice(&(self.key.len() as u16).to_le_bytes());
        out.extend_from_slice(&(self.value.len() as u32).to_le_bytes());
        out.extend_from_slice(&self.namespace);
        out.extend_from_slice(&self.key);
        out.extend_from_slice(&self.value);
    }

    pub fn decode(buf: &[u8], offset: &mut usize) -> Result<Self> {
        if buf.len() < *offset + ENTRY_FIXED_HEADER_LEN {
            return Err(DbError::Corrupted(
                "Truncated operation entry header".to_string(),
            ));
        }
        let op_code = buf[*offset];
        let value_type = buf[*offset + 1];
        let ns_len = u16::from_le_bytes(buf[*offset + 2..*offset + 4].try_into().unwrap()) as usize;
        let key_len =
            u16::from_le_bytes(buf[*offset + 4..*offset + 6].try_into().unwrap()) as usize;
        let val_len =
            u32::from_le_bytes(buf[*offset + 6..*offset + 10].try_into().unwrap()) as usize;
        // Spec §3.2.2 hard limit: 0 <= NsLen <= 255. `ns_len` is a u16 read straight
        // off disk and can reach 65535. Without this check an over-long namespace
        // (written by a foreign producer, or by a past version of this library that
        // failed to validate) is loaded, kept resident, listed by `namespaces()`, and
        // then rejected by *every* API entry point including `drop_namespace` and
        // `clear_namespace` -- an orphan that can neither be read nor reclaimed, and
        // that is re-serialized into every `.bak` and `backup()` artifact, poisoning
        // the other four languages. Kotlin already rejects this at load time
        // (Format.kt); align the rest so a bad file fails loudly at open instead of
        // loading as an unreachable partition.
        if ns_len > MAX_NS_LEN {
            return Err(DbError::Corrupted(format!(
                "Namespace length {} exceeds max {}",
                ns_len, MAX_NS_LEN
            )));
        }
        if op_code == OP_PUT {
            if value_type > VAL_TYPE_BOOL {
                return Err(DbError::Corrupted(format!(
                    "Reserved or invalid value_type 0x{:02X}",
                    value_type
                )));
            }
            if value_type == VAL_TYPE_INT64 && val_len != 8 {
                return Err(DbError::Corrupted(format!(
                    "Expected 8 bytes for int64, got {}",
                    val_len
                )));
            }
            if value_type == VAL_TYPE_FLOAT64 && val_len != 8 {
                return Err(DbError::Corrupted(format!(
                    "Expected 8 bytes for float64, got {}",
                    val_len
                )));
            }
            if value_type == VAL_TYPE_BOOL && val_len != 1 {
                return Err(DbError::Corrupted(format!(
                    "Expected 1 byte for bool, got {}",
                    val_len
                )));
            }
        }
        *offset += ENTRY_FIXED_HEADER_LEN;

        if buf.len() < *offset + ns_len + key_len + val_len {
            return Err(DbError::Corrupted(
                "Truncated operation entry body".to_string(),
            ));
        }

        let namespace = buf[*offset..*offset + ns_len].to_vec();
        *offset += ns_len;
        let key = buf[*offset..*offset + key_len].to_vec();
        *offset += key_len;
        let value = buf[*offset..*offset + val_len].to_vec();
        *offset += val_len;

        Ok(RawOpEntry {
            op_code,
            value_type,
            namespace,
            key,
            value,
        })
    }
}

pub struct BatchFrameWriter;

impl BatchFrameWriter {
    pub fn write_frame<W: Write>(w: &mut W, tx_id: u64, entries: &[RawOpEntry]) -> Result<()> {
        if entries.is_empty() {
            return Err(DbError::Corrupted(
                "OpCount must be >= 1 for a frame".to_string(),
            ));
        }
        if entries.len() > MAX_OPS_PER_FRAME {
            return Err(DbError::Corrupted(format!(
                "OpCount {} exceeds max {}",
                entries.len(),
                MAX_OPS_PER_FRAME
            )));
        }

        let mut payload = Vec::new();
        for e in entries {
            e.encode(&mut payload);
        }
        let payload_len = payload.len() as u32;
        if payload_len as usize > MAX_PAYLOAD_LEN {
            return Err(DbError::Corrupted(format!(
                "Payload length {} exceeds max {}",
                payload_len, MAX_PAYLOAD_LEN
            )));
        }

        let payload_crc = crc32(&payload);

        let mut tail_20 = [0u8; 20];
        tail_20[0..8].copy_from_slice(&tx_id.to_le_bytes());
        tail_20[8..12].copy_from_slice(&(entries.len() as u32).to_le_bytes());
        tail_20[12..16].copy_from_slice(&payload_len.to_le_bytes());
        tail_20[16..20].copy_from_slice(&payload_crc.to_le_bytes());

        let header_crc = crc32(&tail_20);

        let mut header_28 = [0u8; FRAME_HEADER_LEN];
        header_28[0..4].copy_from_slice(&FRAME_MAGIC.to_le_bytes());
        header_28[4..8].copy_from_slice(&header_crc.to_le_bytes());
        header_28[8..28].copy_from_slice(&tail_20);

        w.write_all(&header_28)?;
        w.write_all(&payload)?;
        Ok(())
    }
}

pub struct FrameLoader;

impl FrameLoader {
    /// Loads all frames sequentially from offset 32 to end of file.
    /// Stage 1 rule: returns all RawOpEntry and the max TxID.
    pub fn load_all<R: Read + Seek>(r: &mut R, file_size: u64) -> Result<(Vec<RawOpEntry>, u64)> {
        let mut entries = Vec::new();
        let mut last_tx_id = 0u64;
        let mut offset = FILE_HEADER_LEN as u64;

        r.seek(SeekFrom::Start(offset))?;

        while offset < file_size {
            if file_size - offset < FRAME_HEADER_LEN as u64 {
                return Err(DbError::Corrupted(format!(
                    "Remaining bytes {} insufficient for frame header at offset {}",
                    file_size - offset,
                    offset
                )));
            }

            let mut header_28 = [0u8; FRAME_HEADER_LEN];
            r.read_exact(&mut header_28)?;

            let magic = u32::from_le_bytes(header_28[0..4].try_into().unwrap());
            if magic != FRAME_MAGIC {
                return Err(DbError::Corrupted(format!(
                    "Invalid frame magic 0x{:08X} at offset {}",
                    magic, offset
                )));
            }

            let expected_hdr_crc = u32::from_le_bytes(header_28[4..8].try_into().unwrap());
            let actual_hdr_crc = crc32(&header_28[8..28]);
            if actual_hdr_crc != expected_hdr_crc {
                return Err(DbError::Corrupted(format!(
                    "Frame header CRC mismatch at offset {}: expected 0x{:08X}, got 0x{:08X}",
                    offset, expected_hdr_crc, actual_hdr_crc
                )));
            }

            let tx_id = u64::from_le_bytes(header_28[8..16].try_into().unwrap());
            if tx_id <= last_tx_id {
                return Err(DbError::Corrupted(format!(
                    "Non-monotonic TxID {} (last was {}) at offset {}",
                    tx_id, last_tx_id, offset
                )));
            }

            let op_count = u32::from_le_bytes(header_28[16..20].try_into().unwrap()) as usize;
            if op_count == 0 || op_count > MAX_OPS_PER_FRAME {
                return Err(DbError::Corrupted(format!(
                    "Invalid OpCount {} at offset {}",
                    op_count, offset
                )));
            }

            let payload_len = u32::from_le_bytes(header_28[20..24].try_into().unwrap()) as usize;
            if payload_len > MAX_PAYLOAD_LEN {
                return Err(DbError::Corrupted(format!(
                    "Payload length {} exceeds limit at offset {}",
                    payload_len, offset
                )));
            }

            let expected_payload_crc = u32::from_le_bytes(header_28[24..28].try_into().unwrap());

            if file_size - (offset + FRAME_HEADER_LEN as u64) < payload_len as u64 {
                return Err(DbError::Corrupted(format!(
                    "File truncated inside payload at offset {}",
                    offset
                )));
            }

            let mut payload = vec![0u8; payload_len];
            r.read_exact(&mut payload)?;

            let actual_payload_crc = crc32(&payload);
            if actual_payload_crc != expected_payload_crc {
                return Err(DbError::Corrupted(format!(
                    "Payload CRC mismatch at offset {}: expected 0x{:08X}, got 0x{:08X}",
                    offset, expected_payload_crc, actual_payload_crc
                )));
            }

            // Decode entries
            let mut payload_off = 0usize;
            for _ in 0..op_count {
                let entry = RawOpEntry::decode(&payload, &mut payload_off)?;
                entries.push(entry);
            }

            last_tx_id = tx_id;
            offset += (FRAME_HEADER_LEN + payload_len) as u64;
        }

        Ok((entries, last_tx_id))
    }

    /// True when `[from, to)` consists only of zero bytes.
    ///
    /// Filesystems may materialise a crash-truncated (never written) tail as zeros.
    /// Treating such a region as media damage would turn a routine crash into a hard
    /// failure, so it is classified as an unwritten tail instead. RocksDB tolerates
    /// zeroed trailing data in its default WAL recovery mode for the same reason.
    fn tail_region_is_all_zero(file: &mut std::fs::File, from: u64, to: u64) -> bool {
        const CHUNK: usize = 64 * 1024;

        if file.seek(SeekFrom::Start(from)).is_err() {
            return false;
        }

        let mut remaining = to.saturating_sub(from);
        let mut buf = vec![0u8; CHUNK];

        while remaining > 0 {
            let want = remaining.min(CHUNK as u64) as usize;
            if file.read_exact(&mut buf[..want]).is_err() {
                return false;
            }
            if buf[..want].iter().any(|byte| *byte != 0) {
                return false;
            }
            remaining -= want as u64;
        }

        true
    }

    /// Loads frames with tail-truncation healing for AppendOnlyLog mode.
    ///
    /// Damage classification (C4). The log is append-only, so a torn write can only
    /// ever leave the file ending *inside* the last frame:
    ///
    /// * the file ends before a frame is complete (short header, or a payload that
    ///   runs past EOF) -> **torn tail** -> truncate to the last frame boundary,
    ///   emit `onTailTruncated`, keep every intact frame before it;
    /// * the damaged region is entirely zero-filled -> **unwritten tail** (how some
    ///   filesystems materialise a crash) -> treated like a torn tail;
    /// * a complete frame whose magic / header CRC / payload CRC / entry encoding is
    ///   invalid -> those bytes were written and are wrong, i.e. **media damage**
    ///   -> `Corrupted`, and never a silent truncation, because intact frames may
    ///   follow the damaged one and truncating would discard them.
    ///
    /// `allow_tail_truncation = false` disables healing entirely: every anomaly is
    /// reported as `Corrupted` (snapshot mode, and recovery sources such as `.bak`).
    pub fn load_with_recovery(
        file: &mut std::fs::File,
        file_size: u64,
        allow_tail_truncation: bool,
        on_warning: &Option<crate::options::WarningCallback>,
    ) -> Result<(Vec<RawOpEntry>, u64, u64)> {
        let mut entries = Vec::new();
        let mut last_tx_id = 0u64;
        let mut offset = FILE_HEADER_LEN as u64;
        let mut effective_size = file_size;

        file.seek(SeekFrom::Start(offset))?;

        while offset < effective_size {
            let remaining = effective_size - offset;
            if remaining < FRAME_HEADER_LEN as u64 {
                if allow_tail_truncation {
                    file.set_len(offset)?;
                    let _ = file.sync_all();
                    if let Some(cb) = on_warning {
                        cb(
                            "onTailTruncated",
                            "Truncated incomplete frame header at tail",
                        );
                    }
                    effective_size = offset;
                    break;
                } else {
                    return Err(DbError::Corrupted(format!(
                        "Remaining bytes {} insufficient for frame header at offset {}",
                        remaining, offset
                    )));
                }
            }

            let mut header_28 = [0u8; FRAME_HEADER_LEN];
            if let Err(e) = file.read_exact(&mut header_28) {
                if allow_tail_truncation {
                    file.set_len(offset)?;
                    let _ = file.sync_all();
                    if let Some(cb) = on_warning {
                        cb(
                            "onTailTruncated",
                            "Truncated unreadable frame header at tail",
                        );
                    }
                    effective_size = offset;
                    break;
                } else {
                    return Err(DbError::Io(e));
                }
            }

            let magic = u32::from_le_bytes(header_28[0..4].try_into().unwrap());
            let expected_hdr_crc = u32::from_le_bytes(header_28[4..8].try_into().unwrap());
            let actual_hdr_crc = crc32(&header_28[8..28]);
            let tx_id = u64::from_le_bytes(header_28[8..16].try_into().unwrap());
            let op_count = u32::from_le_bytes(header_28[16..20].try_into().unwrap()) as usize;
            let payload_len = u32::from_le_bytes(header_28[20..24].try_into().unwrap()) as usize;
            let expected_payload_crc = u32::from_le_bytes(header_28[24..28].try_into().unwrap());

            if magic != FRAME_MAGIC
                || actual_hdr_crc != expected_hdr_crc
                || tx_id <= last_tx_id
                || op_count == 0
                || op_count > MAX_OPS_PER_FRAME
                || payload_len > MAX_PAYLOAD_LEN
            {
                // A complete frame header is present, so these bytes were written:
                // this cannot be a torn write, only media damage or an unwritten
                // (zero-filled) tail.
                if allow_tail_truncation
                    && Self::tail_region_is_all_zero(file, offset, effective_size)
                {
                    file.set_len(offset)?;
                    let _ = file.sync_all();
                    if let Some(cb) = on_warning {
                        cb("onTailTruncated", "Truncated zero-filled tail at tail");
                    }
                    effective_size = offset;
                    break;
                }
                return Err(DbError::Corrupted(format!(
                    "Corrupted frame header at offset {}: magic 0x{:08X}, tx_id {}, \
                     op_count {}, payload_len {}",
                    offset, magic, tx_id, op_count, payload_len
                )));
            }

            if effective_size - (offset + FRAME_HEADER_LEN as u64) < payload_len as u64 {
                if allow_tail_truncation {
                    file.set_len(offset)?;
                    let _ = file.sync_all();
                    if let Some(cb) = on_warning {
                        cb("onTailTruncated", "Truncated incomplete payload at tail");
                    }
                    effective_size = offset;
                    break;
                } else {
                    return Err(DbError::Corrupted(format!(
                        "File truncated inside payload at offset {}",
                        offset
                    )));
                }
            }

            let mut payload = vec![0u8; payload_len];
            if let Err(e) = file.read_exact(&mut payload) {
                if allow_tail_truncation {
                    file.set_len(offset)?;
                    let _ = file.sync_all();
                    if let Some(cb) = on_warning {
                        cb("onTailTruncated", "Truncated unreadable payload at tail");
                    }
                    effective_size = offset;
                    break;
                } else {
                    return Err(DbError::Io(e));
                }
            }

            let actual_payload_crc = crc32(&payload);
            if actual_payload_crc != expected_payload_crc {
                // The whole frame is present -- the header validated and the payload
                // length lies inside the file -- yet its content does not match the
                // recorded CRC: the bytes were written and are wrong (media damage).
                return Err(DbError::Corrupted(format!(
                    "Payload CRC mismatch at offset {}: expected 0x{:08X}, got 0x{:08X}",
                    offset, expected_payload_crc, actual_payload_crc
                )));
            }

            let mut payload_off = 0usize;
            let mut frame_entries = Vec::with_capacity(op_count);
            let mut decode_err = false;
            for _ in 0..op_count {
                match RawOpEntry::decode(&payload, &mut payload_off) {
                    Ok(entry) => frame_entries.push(entry),
                    Err(_) => {
                        decode_err = true;
                        break;
                    }
                }
            }

            if decode_err {
                // The payload CRC matched, so the payload is exactly what was
                // written: an entry that fails to decode is an invariant violation
                // (or a deliberately crafted file), never a torn write.
                return Err(DbError::Corrupted(format!(
                    "Failed to decode entry in payload at offset {}",
                    offset
                )));
            }

            entries.extend(frame_entries);
            last_tx_id = tx_id;
            offset += (FRAME_HEADER_LEN + payload_len) as u64;
        }

        Ok((entries, last_tx_id, effective_size))
    }

    /// Fast scan to find the latest (max_tx_id, file_size) without loading payloads.
    pub fn scan_max_tx_id<R: Read + Seek>(r: &mut R, file_size: u64) -> Result<u64> {
        let mut last_tx_id = 0u64;
        let mut offset = FILE_HEADER_LEN as u64;

        while offset < file_size {
            if file_size - offset < FRAME_HEADER_LEN as u64 {
                return Err(DbError::Corrupted(
                    "Truncated frame in fast scan".to_string(),
                ));
            }
            r.seek(SeekFrom::Start(offset))?;
            let mut header_28 = [0u8; FRAME_HEADER_LEN];
            r.read_exact(&mut header_28)?;

            let magic = u32::from_le_bytes(header_28[0..4].try_into().unwrap());
            if magic != FRAME_MAGIC {
                return Err(DbError::Corrupted(
                    "Invalid frame magic in fast scan".to_string(),
                ));
            }

            let tx_id = u64::from_le_bytes(header_28[8..16].try_into().unwrap());
            let payload_len = u32::from_le_bytes(header_28[20..24].try_into().unwrap()) as u64;

            last_tx_id = tx_id;
            offset += FRAME_HEADER_LEN as u64 + payload_len;
        }

        Ok(last_tx_id)
    }
}
