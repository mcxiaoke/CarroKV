// IEEE 802.3 CRC32 implementation using 256-entry lookup table.
// Zero external dependencies.

const CRC32_POLYNOMIAL: u32 = 0xEDB88320;

const fn make_crc_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    let mut i = 0;
    while i < 256 {
        let mut crc = i as u32;
        let mut j = 0;
        while j < 8 {
            if (crc & 1) != 0 {
                crc = (crc >> 1) ^ CRC32_POLYNOMIAL;
            } else {
                crc >>= 1;
            }
            j += 1;
        }
        table[i] = crc;
        i += 1;
    }
    table
}

static CRC_TABLE: [u32; 256] = make_crc_table();

/// Calculates IEEE 802.3 CRC32 checksum for the given data slice.
pub fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFFFFFFu32;
    for &b in data {
        let index = ((crc ^ (b as u32)) & 0xFF) as usize;
        crc = (crc >> 8) ^ CRC_TABLE[index];
    }
    crc ^ 0xFFFFFFFFu32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_standard_vector() {
        assert_eq!(crc32(b"123456789"), 0xCBF43926);
    }
}
