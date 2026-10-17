use carro_kv::format::FileHeader;
use carro_kv::Db;
use std::fs::{self, File};
use std::io::Read;
use std::path::PathBuf;

fn sha256_hex(data: &[u8]) -> String {
    // Zero-dependency sha256 or simple byte check
    // We can compute or check bytes directly
    let mut hasher = Sha256::new();
    hasher.update(data);
    hasher.finish_hex()
}

// Minimal inline SHA256 for testing without external crate
struct Sha256 {
    state: [u32; 8],
    buffer: [u8; 64],
    buf_len: usize,
    total_len: u64,
}

impl Sha256 {
    fn new() -> Self {
        Self {
            state: [
                0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
                0x5be0cd19,
            ],
            buffer: [0u8; 64],
            buf_len: 0,
            total_len: 0,
        }
    }

    fn update(&mut self, mut data: &[u8]) {
        self.total_len += data.len() as u64;
        while !data.is_empty() {
            let space = 64 - self.buf_len;
            if data.len() >= space {
                self.buffer[self.buf_len..64].copy_from_slice(&data[..space]);
                self.process_block();
                self.buf_len = 0;
                data = &data[space..];
            } else {
                self.buffer[self.buf_len..self.buf_len + data.len()].copy_from_slice(data);
                self.buf_len += data.len();
                break;
            }
        }
    }

    fn process_block(&mut self) {
        const K: [u32; 64] = [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
            0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
            0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
            0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
            0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
            0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
            0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
            0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
            0xc67178f2,
        ];

        let mut w = [0u32; 64];
        for (i, chunk) in self.buffer.chunks_exact(4).take(16).enumerate() {
            w[i] = u32::from_be_bytes(chunk.try_into().unwrap());
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }

        let mut a = self.state[0];
        let mut b = self.state[1];
        let mut c = self.state[2];
        let mut d = self.state[3];
        let mut e = self.state[4];
        let mut f = self.state[5];
        let mut g = self.state[6];
        let mut h = self.state[7];

        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);

            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }

        self.state[0] = self.state[0].wrapping_add(a);
        self.state[1] = self.state[1].wrapping_add(b);
        self.state[2] = self.state[2].wrapping_add(c);
        self.state[3] = self.state[3].wrapping_add(d);
        self.state[4] = self.state[4].wrapping_add(e);
        self.state[5] = self.state[5].wrapping_add(f);
        self.state[6] = self.state[6].wrapping_add(g);
        self.state[7] = self.state[7].wrapping_add(h);
    }

    fn finish_hex(mut self) -> String {
        let bit_len = self.total_len * 8;
        self.buffer[self.buf_len] = 0x80;
        self.buf_len += 1;

        if self.buf_len > 56 {
            for i in self.buf_len..64 {
                self.buffer[i] = 0;
            }
            self.process_block();
            self.buf_len = 0;
        }

        for i in self.buf_len..56 {
            self.buffer[i] = 0;
        }
        self.buffer[56..64].copy_from_slice(&bit_len.to_be_bytes());
        self.process_block();

        let mut out = String::with_capacity(64);
        for s in self.state {
            out.push_str(&format!("{:08x}", s));
        }
        out
    }
}

#[test]
fn test_golden_empty_v3_vector() {
    let empty_header = FileHeader::create_bytes();
    assert_eq!(empty_header.len(), 32);

    let crc = u32::from_le_bytes(empty_header[28..32].try_into().unwrap());
    assert_eq!(crc, 0x0C44C937);

    let sha = sha256_hex(&empty_header);
    assert_eq!(
        sha,
        "06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328"
    );
}

#[test]
fn test_golden_single_put_v3_vector() {
    let test_dir = PathBuf::from("temp/test_rust_golden");
    let _ = fs::remove_dir_all(&test_dir);
    fs::create_dir_all(&test_dir).unwrap();

    let db_path = test_dir.join("single_put_v3.db");
    {
        let db = Db::open(&db_path, None).unwrap();
        // Insert hello: world in default namespace
        db.put_string("", "hello", "world").unwrap();
    }

    let mut file = File::open(&db_path).unwrap();
    let mut data = Vec::new();
    file.read_to_end(&mut data).unwrap();

    assert_eq!(data.len(), 80);

    let sha = sha256_hex(&data);
    assert_eq!(
        sha,
        "f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a"
    );

    let _ = fs::remove_dir_all(&test_dir);
}

#[test]
fn test_crud_and_sorting() {
    let test_dir = PathBuf::from("temp/test_rust_crud");
    let _ = fs::remove_dir_all(&test_dir);
    fs::create_dir_all(&test_dir).unwrap();

    let db_path = test_dir.join("crud.db");
    let db = Db::open(&db_path, None).unwrap();

    // Test Unicode & Emoji order
    // UTF-8 byte ordering: "a" (0x61) < "z" (0x7A) < "\uE000" (EE 80 80) < "😀" (F0 9F 98 80)
    db.put_string("", "😀", "emoji").unwrap();
    db.put_string("", "a", "first").unwrap();
    db.put_string("", "\u{e000}", "private_use").unwrap();
    db.put_string("", "z", "last_ascii").unwrap();

    let keys = db.keys("", None).unwrap();
    assert_eq!(keys, vec!["a", "z", "\u{e000}", "😀"]);

    // Test scan_range
    let entries = db.scan_range("", Some("z"), Some("😀")).unwrap();
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].key, "z");
    assert_eq!(entries[1].key, "\u{e000}");

    // Test batch staging & rollback
    let res = db.batch(|b| {
        b.put_string("", "temp_key", "temp_val")?;
        Err(carro_kv::DbError::Corrupted(
            "Simulated error in batch".to_string(),
        ))
    });
    assert!(res.is_err());
    assert_eq!(db.get_string("", "temp_key").unwrap(), None);

    // Test batch success
    db.batch(|b| {
        b.put_string("", "k1", "v1")?;
        b.put_string("", "k2", "v2")?;
        Ok(())
    })
    .unwrap();

    assert_eq!(db.get_string("", "k1").unwrap(), Some("v1".to_string()));
    assert_eq!(db.get_string("", "k2").unwrap(), Some("v2".to_string()));

    let _ = fs::remove_dir_all(&test_dir);
}
