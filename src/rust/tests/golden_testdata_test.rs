//! 共享黄金向量测试（`testdata/*.db`）。
//!
//! 这些文件由 `tools/gen_testdata.py` 按 v3.2 规范**独立于三种实现**生成。
//! 本测试做两件事：
//!   1. 读取外部生成的文件 → 断言解析出的内容正确（证明互操作性）
//!   2. 用本实现重新生成相同内容 → 断言字节与外部文件**完全一致**
//!      （证明确定性序列化，字节级兼容）
//!
//! 新增语言必须通过同一套 testdata，这是 API v1.0 冻结契约的验收门槛之一。

use ckv::Db;
use std::fs;
use std::path::{Path, PathBuf};
use tempfile::tempdir;

fn testdata_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("testdata")
}

fn read_vector(name: &str) -> Vec<u8> {
    let p = testdata_dir().join(name);
    fs::read(&p).unwrap_or_else(|e| panic!("cannot read {}: {}", p.display(), e))
}

fn read_vector_hex(name: &str) -> String {
    read_vector(name)
        .iter()
        .map(|b| format!("{:02x}", b))
        .collect()
}

// 已知清单（与 testdata/MANIFEST.txt 一致，独立第三方生成器产出）
const EMPTY_SHA: &str = "06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328";
const SINGLE_PUT_SHA: &str = "f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a";

// 零依赖 SHA-256（测试用）
mod sha256 {
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

    pub fn hex(data: &[u8]) -> String {
        let mut h: [u32; 8] = [
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
            0x5be0cd19,
        ];

        let mut msg = data.to_vec();
        let bit_len = (data.len() as u64) * 8;
        msg.push(0x80);
        while msg.len() % 64 != 56 {
            msg.push(0);
        }
        msg.extend_from_slice(&bit_len.to_be_bytes());

        for chunk in msg.chunks(64) {
            let mut w = [0u32; 64];
            for i in 0..16 {
                w[i] = u32::from_be_bytes([
                    chunk[i * 4],
                    chunk[i * 4 + 1],
                    chunk[i * 4 + 2],
                    chunk[i * 4 + 3],
                ]);
            }
            for i in 16..64 {
                let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
                let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16]
                    .wrapping_add(s0)
                    .wrapping_add(w[i - 7])
                    .wrapping_add(s1);
            }

            let (mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh) =
                (h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7]);

            for i in 0..64 {
                let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
                let ch = (e & f) ^ ((!e) & g);
                let t1 = hh
                    .wrapping_add(s1)
                    .wrapping_add(ch)
                    .wrapping_add(K[i])
                    .wrapping_add(w[i]);
                let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
                let maj = (a & b) ^ (a & c) ^ (b & c);
                let t2 = s0.wrapping_add(maj);
                hh = g;
                g = f;
                f = e;
                e = d.wrapping_add(t1);
                d = c;
                c = b;
                b = a;
                a = t1.wrapping_add(t2);
            }

            h[0] = h[0].wrapping_add(a);
            h[1] = h[1].wrapping_add(b);
            h[2] = h[2].wrapping_add(c);
            h[3] = h[3].wrapping_add(d);
            h[4] = h[4].wrapping_add(e);
            h[5] = h[5].wrapping_add(f);
            h[6] = h[6].wrapping_add(g);
            h[7] = h[7].wrapping_add(hh);
        }

        h.iter().map(|v| format!("{:08x}", v)).collect()
    }
}

#[test]
fn sha256_self_test() {
    assert_eq!(
        sha256::hex(b"abc"),
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
    assert_eq!(
        sha256::hex(b""),
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    );
}

// 空库：32 字节、SHA-256 与规范一致、可被打开且为空
#[test]
fn golden_empty_v3() {
    let data = read_vector("empty_v3.db");
    assert_eq!(data.len(), 32, "empty_v3.db must be exactly 32 bytes");
    assert_eq!(sha256::hex(&data), EMPTY_SHA);

    let dir = tempdir().unwrap();
    let p = dir.path().join("copy.db");
    fs::write(&p, &data).unwrap();

    let db = Db::open(&p, None).unwrap();
    assert_eq!(db.count("").unwrap(), 0);
    assert!(db.namespaces().is_empty());
    assert_eq!(db.stats().current_max_tx_id, 0);

    // 再落盘一次必须还原同样的 32 字节
    db.flush().unwrap();
    assert_eq!(
        fs::read(&p).unwrap(),
        data,
        "empty round-trip must be byte-identical"
    );
}

// 单条 put：80 字节、SHA-256 与规范一致、内容与本地生成字节完全相同
#[test]
fn golden_single_put_v3() {
    let data = read_vector("single_put_v3.db");
    assert_eq!(data.len(), 80, "single_put_v3.db must be exactly 80 bytes");
    assert_eq!(sha256::hex(&data), SINGLE_PUT_SHA);

    // 1) 解析外部文件
    let dir = tempdir().unwrap();
    let p = dir.path().join("single.db");
    fs::write(&p, &data).unwrap();
    {
        let db = Db::open(&p, None).unwrap();
        assert_eq!(
            db.get_string("", "hello").unwrap(),
            Some("world".to_string())
        );
        assert_eq!(db.count("").unwrap(), 1);
    }

    // 2) 本地重新生成，字节必须相同
    let p2 = dir.path().join("regen.db");
    {
        let db = Db::open(&p2, None).unwrap();
        db.put_string("", "hello", "world").unwrap();
    }
    assert_eq!(
        fs::read(&p2).unwrap(),
        data,
        "locally generated bytes must match the shared golden vector"
    );
}

// 5 种 ValueType + 多命名空间：解析正确且本地重新生成字节相同
#[test]
fn golden_multi_types_v3() {
    let data = read_vector("multi_types_v3.db");

    let dir = tempdir().unwrap();
    let p = dir.path().join("multi.db");
    fs::write(&p, &data).unwrap();
    {
        let db = Db::open(&p, None).unwrap();
        assert_eq!(
            db.get_bytes("", "bytes").unwrap().unwrap(),
            vec![0x00, 0x01, 0xFE, 0xFF]
        );
        assert_eq!(db.get_string("", "str").unwrap(), Some("你好".to_string()));
        assert_eq!(db.get_i64("", "i64").unwrap(), Some(-1234567890123456789));
        assert_eq!(
            db.get_f64("", "f64").unwrap(),
            Some(std::f64::consts::PI),
            "vector stores f64 = PI (same bit pattern as the generator's math.pi)"
        );
        let nan = db.get_f64("", "f64nan").unwrap().unwrap();
        assert!(nan.is_nan());
        assert_eq!(
            nan.to_bits(),
            0x7FF8000000000000,
            "NaN must be canonicalised"
        );
        assert_eq!(db.get_bool("", "bool_t").unwrap(), Some(true));
        assert_eq!(db.get_bool("", "bool_f").unwrap(), Some(false));
        assert_eq!(db.get_bool("cfg", "enabled").unwrap(), Some(true));
        assert_eq!(db.get_i64("cfg", "limit").unwrap(), Some(1000));
        assert_eq!(
            db.get_string("users", "u1").unwrap(),
            Some("alice".to_string())
        );
        assert_eq!(db.namespaces(), vec!["", "cfg", "users"]);
        assert_eq!(db.stats().total_entries, 10);
    }

    // 本地重新生成
    let p2 = dir.path().join("multi_regen.db");
    {
        let db = Db::open(&p2, None).unwrap();
        db.batch(|b| {
            b.put_bytes("", "bytes", vec![0x00, 0x01, 0xFE, 0xFF])?;
            b.put_string("", "str", "你好")?;
            b.put_i64("", "i64", -1234567890123456789)?;
            b.put_f64("", "f64", std::f64::consts::PI)?;
            b.put_f64("", "f64nan", f64::NAN)?;
            b.put_bool("", "bool_t", true)?;
            b.put_bool("", "bool_f", false)?;
            b.put_bool("cfg", "enabled", true)?;
            b.put_i64("cfg", "limit", 1000)?;
            b.put_string("users", "u1", "alice")?;
            Ok(())
        })
        .unwrap();
    }
    assert_eq!(
        fs::read(&p2).unwrap(),
        data,
        "locally generated bytes must match the shared golden vector"
    );
}

// 非 ASCII 命名空间/键：锁定 UTF-8 无符号字节序，且磁盘排序不可二次排序
#[test]
fn golden_unicode_order_v3() {
    let data = read_vector("unicode_order_v3.db");

    let dir = tempdir().unwrap();
    let p = dir.path().join("uni.db");
    fs::write(&p, &data).unwrap();
    {
        let db = Db::open(&p, None).unwrap();
        assert_eq!(
            db.namespaces(),
            vec!["", "cf", "é", "中文"],
            "namespaces must follow unsigned UTF-8 byte order"
        );
        assert_eq!(
            db.keys("", None).unwrap(),
            vec!["a", "z", "\u{E000}", "\u{1F600}"],
            "keys must follow unsigned UTF-8 byte order (not UTF-16 code unit order)"
        );
    }

    let p2 = dir.path().join("uni_regen.db");
    {
        let db = Db::open(&p2, None).unwrap();
        db.batch(|b| {
            for (ns, k, v) in [
                ("", "a", "1"),
                ("", "z", "2"),
                ("", "\u{E000}", "3"),
                ("", "\u{1F600}", "4"),
                ("é", "k", "5"),
                ("中文", "k", "6"),
                ("cf", "k", "7"),
            ] {
                b.put_string(ns, k, v)?;
            }
            Ok(())
        })
        .unwrap();
    }
    assert_eq!(
        fs::read(&p2).unwrap(),
        data,
        "locally generated bytes must match the shared golden vector"
    );
}

// 把向量文件里的关键帧字段直接解码核对（不经过 Db 实现）
// 帧头布局：FrameMagic(4) HeaderCRC32(4) TxID(8) OpCount(4) PayloadLen(4) PayloadCRC32(4)
#[test]
fn golden_frame_header_layout_is_as_specified() {
    let data = read_vector("single_put_v3.db");
    assert_eq!(&data[0..8], b"UKVDB003");
    assert_eq!(u16::from_le_bytes([data[8], data[9]]), 3, "FormatReadVer");
    assert_eq!(
        u16::from_le_bytes([data[10], data[11]]),
        3,
        "FormatWriteVer"
    );
    assert_eq!(
        u32::from_le_bytes([data[32], data[33], data[34], data[35]]),
        0x4D52464B
    );
    assert_eq!(
        u64::from_le_bytes(data[40..48].try_into().unwrap()),
        1,
        "TxID"
    );
    assert_eq!(
        u32::from_le_bytes(data[48..52].try_into().unwrap()),
        1,
        "OpCount"
    );
    assert_eq!(
        u32::from_le_bytes(data[52..56].try_into().unwrap()),
        20,
        "PayloadLen"
    );
    // 载荷首字节
    assert_eq!(data[60], 0x01, "OpCode=OpPut");
    assert_eq!(data[61], 0x01, "ValueType=Utf8String");
    assert_eq!(u16::from_le_bytes([data[62], data[63]]), 0, "NsLen=0");
    assert_eq!(u16::from_le_bytes([data[64], data[65]]), 5, "KeyLen=5");
    assert_eq!(
        u32::from_le_bytes(data[66..70].try_into().unwrap()),
        5,
        "ValLen=5"
    );
    assert_eq!(&data[70..75], b"hello");
    assert_eq!(&data[75..80], b"world");
}

#[test]
fn testdata_manifest_matches_actual_files() {
    let manifest = fs::read_to_string(testdata_dir().join("MANIFEST.txt")).unwrap();
    for name in [
        "empty_v3.db",
        "single_put_v3.db",
        "multi_types_v3.db",
        "unicode_order_v3.db",
    ] {
        let digest = sha256::hex(&read_vector(name));
        assert!(
            manifest.contains(&digest),
            "{} hash {} missing from MANIFEST.txt",
            name,
            digest
        );
    }
    // 顺手确保 hex 辅助函数可用（调试用）
    assert!(read_vector_hex("empty_v3.db").starts_with("554b564442303033"));
}
