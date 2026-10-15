use crate::errors::{DbError, Result};
use std::cmp::Ordering;

pub const FILE_MAGIC: [u8; 8] = *b"UKVDB003";
pub const FRAME_MAGIC: u32 = 0x4D52464B; // Little-endian 'KFRM'
pub const FORMAT_READ_VER: u16 = 3;
pub const FORMAT_WRITE_VER: u16 = 3;

pub const MAX_NS_LEN: usize = 255;
pub const MAX_KEY_LEN: usize = 65535;
pub const MAX_VAL_LEN: usize = 16_777_216; // 16 MB
pub const MAX_PAYLOAD_LEN: usize = 67_108_864; // 64 MB
pub const MAX_OPS_PER_FRAME: usize = 1_000_000;
pub const ENTRY_FIXED_HEADER_LEN: usize = 10;

pub const OP_PUT: u8 = 0x01;
pub const OP_DELETE: u8 = 0x02;
pub const OP_DROP_NAMESPACE: u8 = 0x03;

pub const VAL_TYPE_BYTES: u8 = 0x00;
pub const VAL_TYPE_STRING: u8 = 0x01;
pub const VAL_TYPE_INT64: u8 = 0x02;
pub const VAL_TYPE_FLOAT64: u8 = 0x03;
pub const VAL_TYPE_BOOL: u8 = 0x04;

pub const DEFAULT_NAMESPACE_ALIAS: &str = "default";

/// Encode f64 according to specification: canonical quiet NaN (0x7FF8000000000000)
pub fn encode_f64(val: f64) -> [u8; 8] {
    if val.is_nan() {
        0x7FF8000000000000u64.to_le_bytes()
    } else {
        val.to_bits().to_le_bytes()
    }
}

pub fn decode_f64(bytes: &[u8]) -> Result<f64> {
    if bytes.len() != 8 {
        return Err(DbError::Corrupted(format!("Expected 8 bytes for f64, got {}", bytes.len())));
    }
    let bits = u64::from_le_bytes(bytes.try_into().unwrap());
    Ok(f64::from_bits(bits))
}

pub fn encode_i64(val: i64) -> [u8; 8] {
    val.to_le_bytes()
}

pub fn decode_i64(bytes: &[u8]) -> Result<i64> {
    if bytes.len() != 8 {
        return Err(DbError::Corrupted(format!("Expected 8 bytes for i64, got {}", bytes.len())));
    }
    Ok(i64::from_le_bytes(bytes.try_into().unwrap()))
}

pub fn encode_bool(val: bool) -> [u8; 1] {
    [if val { 0x01 } else { 0x00 }]
}

pub fn decode_bool(bytes: &[u8]) -> Result<bool> {
    if bytes.is_empty() {
        return Err(DbError::Corrupted("Expected at least 1 byte for bool".to_string()));
    }
    Ok(bytes[0] != 0)
}

/// Normalize namespace string according to specification.
/// Empty string and "default" are normalized to "".
/// Throws InvalidNamespaceError if UTF-8 length exceeds 255 bytes.
pub fn normalize_namespace(ns: &str) -> Result<&str> {
    if ns.len() > MAX_NS_LEN {
        return Err(DbError::InvalidNamespace(format!(
            "Namespace byte length {} exceeds maximum allowed {}",
            ns.len(),
            MAX_NS_LEN
        )));
    }
    if ns.is_empty() || ns == DEFAULT_NAMESPACE_ALIAS {
        Ok("")
    } else {
        Ok(ns)
    }
}

/// Unsigned UTF-8 byte-by-byte dictionary comparator.
pub fn compare_utf8_bytes(a: &[u8], b: &[u8]) -> Ordering {
    a.cmp(b) // Rust [u8] comparison is naturally unsigned 0..255 lexicographical!
}

/// An entry (key-value pair with metadata) returned by range queries and iterators.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    /// Normalized namespace name ("" for default namespace)
    pub namespace: String,

    /// Key string
    pub key: String,

    /// Value type tag (0x00 for Bytes, 0x01 for Utf8String)
    pub value_type: u8,

    /// Raw value bytes
    pub raw_bytes: Vec<u8>,
}

impl Entry {
    /// Create a new Entry instance.
    pub fn new(namespace: String, key: String, value_type: u8, raw_bytes: Vec<u8>) -> Self {
        Self {
            namespace,
            key,
            value_type,
            raw_bytes,
        }
    }

    /// Decode raw_bytes into a UTF-8 String.
    ///
    /// # Errors
    /// Returns `DbError::InvalidUtf8` if raw_bytes contains invalid UTF-8 sequences.
    pub fn string_value(&self) -> Result<String> {
        String::from_utf8(self.raw_bytes.clone()).map_err(|e| {
            DbError::InvalidUtf8(format!("Invalid UTF-8 byte sequence in key '{}': {}", self.key, e))
        })
    }

    /// Returns a reference to the raw bytes.
    pub fn as_bytes(&self) -> &[u8] {
        &self.raw_bytes
    }

    /// Check if this entry was stored as a UTF-8 String.
    pub fn is_string(&self) -> bool {
        self.value_type == VAL_TYPE_STRING
    }

    /// Decode raw_bytes into an i64 integer.
    pub fn as_i64(&self) -> Result<i64> {
        decode_i64(&self.raw_bytes)
    }

    /// Decode raw_bytes into an f64 double-precision float.
    pub fn as_f64(&self) -> Result<f64> {
        decode_f64(&self.raw_bytes)
    }

    /// Decode raw_bytes into a boolean.
    pub fn as_bool(&self) -> Result<bool> {
        decode_bool(&self.raw_bytes)
    }

    pub fn is_i64(&self) -> bool {
        self.value_type == VAL_TYPE_INT64
    }

    pub fn is_f64(&self) -> bool {
        self.value_type == VAL_TYPE_FLOAT64
    }

    pub fn is_bool(&self) -> bool {
        self.value_type == VAL_TYPE_BOOL
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyChangeOp {
    Put,
    Delete,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeyChangeEvent {
    pub op: KeyChangeOp,
    pub namespace: String,
    pub key: String,
    pub value_type: u8,
}
