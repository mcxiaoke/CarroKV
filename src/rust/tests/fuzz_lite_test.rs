//! Fuzz-lite: deterministic mutation of real database files.
//!
//! Not a coverage-guided fuzzer. It applies a fixed, reproducible mutation set to
//! the golden vectors plus one generated AOL file, and asserts the two properties
//! that matter for a binary parser:
//!
//! 1. **no panic / no hang** - a mutated file must be rejected cleanly or opened
//!    and read successfully; it must never crash the process or index out of
//!    bounds;
//! 2. **no false acceptance of structural damage** - truncating below the file
//!    header or corrupting the magic must be reported, not silently treated as an
//!    empty database.
//!
//! Accepted error kinds are those the spec defines for damaged input. Anything
//! else (Locked, TypeMismatch, ConcurrentModification) means the parser reached a
//! state it should never reach from bytes alone.

use carro_kv::{Db, DbError, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};

struct Seed {
    name: &'static str,
    bytes: Vec<u8>,
}

fn seeds() -> Vec<Seed> {
    let mut out = Vec::new();
    for name in [
        "empty_v3.db",
        "single_put_v3.db",
        "multi_types_v3.db",
        "unicode_order_v3.db",
    ] {
        let path = PathBuf::from("../../testdata").join(name);
        if let Ok(bytes) = fs::read(&path) {
            out.push(Seed { name, bytes });
        }
    }

    // One append-only-log file with several frames, so the mutation set also hits
    // frame boundaries that the compact golden vectors do not contain.
    let dir = std::env::temp_dir().join(format!("ckv_fuzz_seed_{}", std::process::id()));
    let _ = fs::create_dir_all(&dir);
    let path = dir.join("generated.db");
    for suffix in ["", ".bak", ".tmp", ".lock"] {
        let _ = fs::remove_file(dir.join(format!("generated.db{suffix}")));
    }
    if let Ok(db) = Db::open(
        &path,
        Some(
            DbOptions::default()
                .engine_mode(StorageEngineMode::AppendOnlyLog)
                .sync_mode(SyncMode::Manual),
        ),
    ) {
        let _ = db.put_string("ns", "alpha", "1");
        let _ = db.put_string("ns", "beta", "22");
        let _ = db.put_i64("ns2", "gamma", i64::MIN);
        let _ = db.close();
        if let Ok(bytes) = fs::read(&path) {
            out.push(Seed {
                name: "generated_aol",
                bytes,
            });
        }
    }
    let _ = fs::remove_dir_all(&dir);
    out
}

fn accepted(err: &DbError) -> bool {
    matches!(
        err,
        DbError::Corrupted(_)
            | DbError::UnsupportedVersion(_)
            | DbError::InvalidUtf8(_)
            | DbError::InvalidNamespace(_)
            | DbError::KeyTooLarge(_)
            | DbError::Io(_)
    )
}

fn sweep(db: &Db) {
    for ns in db.namespaces() {
        let _ = db.count(&ns);
        if let Ok(keys) = db.keys(&ns, None) {
            for key in keys {
                let _ = db.get_bytes(&ns, &key);
            }
        }
        if let Ok(iter) = db.scan_range(&ns, None, None) {
            for _ in iter {}
        }
    }
    let _ = db.stats();
}

#[derive(PartialEq, Eq)]
enum Outcome {
    Opened,
    Rejected,
}

/// Opens `bytes` as a database, exercises every read path and closes it. Returns
/// `Err(message)` when the process must be considered broken.
fn exercise(dir: &Path, tag: usize, bytes: &[u8]) -> Result<Outcome, String> {
    let path = dir.join(format!("case_{tag}.db"));
    fs::write(&path, bytes).map_err(|e| format!("write: {e}"))?;

    let result = catch_unwind(AssertUnwindSafe(|| match Db::open(&path, None) {
        Ok(db) => {
            sweep(&db);
            let _ = db.close();
            Ok(Outcome::Opened)
        }
        Err(err) => {
            if accepted(&err) {
                Ok(Outcome::Rejected)
            } else {
                Err(format!("unexpected error kind: {err}"))
            }
        }
    }));

    match result {
        Ok(inner) => inner,
        Err(_) => Err("panicked while opening/reading a mutated database".to_string()),
    }
}

#[test]
fn mutated_golden_vectors_never_panic() {
    let dir = std::env::temp_dir().join(format!("ckv_fuzz_cases_{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();

    let mut tag = 0usize;
    let mut cases = 0usize;
    let mut seeds_found = 0usize;
    let mut rejected = 0usize;

    for seed in seeds() {
        seeds_found += 1;
        let original = seed.bytes.clone();
        let len = original.len();

        // 1. Truncation, including inside the header and one byte short of the end.
        let mut cuts = vec![0usize, 1, 15, 31, 32, 33];
        if len > 1 {
            cuts.push(len - 1);
        }
        for cut in cuts.into_iter().filter(|c| *c <= len) {
            let mut bytes = original.clone();
            bytes.truncate(cut);
            tag += 1;
            cases += 1;
            if exercise(&dir, tag, &bytes)
                .unwrap_or_else(|msg| panic!("{} truncated to {} bytes: {msg}", seed.name, cut))
                == Outcome::Rejected
            {
                rejected += 1;
            }
        }

        // 2. Single-byte flips across the whole file, three masks.
        for offset in 0..len {
            for mask in [0xFFu8, 0x01, 0x80] {
                let mut bytes = original.clone();
                bytes[offset] ^= mask;
                tag += 1;
                cases += 1;
                if exercise(&dir, tag, &bytes).unwrap_or_else(|msg| {
                    panic!(
                        "{} flipped at offset {} mask {:#04x}: {msg}",
                        seed.name, offset, mask
                    )
                }) == Outcome::Rejected
                {
                    rejected += 1;
                }
            }
        }

        // 3. Appended garbage (a torn tail must be rejected or healed, never crash).
        for extra in [vec![0x00u8], vec![0xFF], vec![0x00, 0x01, 0x02, 0x03]] {
            let mut bytes = original.clone();
            bytes.extend_from_slice(&extra);
            tag += 1;
            cases += 1;
            if exercise(&dir, tag, &bytes).unwrap_or_else(|msg| {
                panic!("{} with {} appended bytes: {msg}", seed.name, extra.len())
            }) == Outcome::Rejected
            {
                rejected += 1;
            }
        }

        // 4. Structural damage must be *detected*, not accepted as an empty db.
        let mut short = original.clone();
        short.truncate(31);
        tag += 1;
        cases += 1;
        let path = dir.join(format!("case_{tag}.db"));
        fs::write(&path, &short).unwrap();
        assert!(
            Db::open(&path, None).is_err(),
            "{} truncated below the file header was accepted",
            seed.name
        );
        if len > 0 {
            let mut magic = original.clone();
            magic[0] ^= 0xFF;
            tag += 1;
            cases += 1;
            let path = dir.join(format!("case_{tag}.db"));
            fs::write(&path, &magic).unwrap();
            let opened_ok = Db::open(&path, None).is_ok();
            assert!(
                !opened_ok,
                "{} with corrupted magic was accepted",
                seed.name
            );
        }
    }

    let _ = fs::remove_dir_all(&dir);
    assert!(cases > 1_000, "mutation set unexpectedly small: {cases}");
    assert!(seeds_found > 0, "no seed files found");
    // Guard against a vacuous test: if nothing is ever rejected, the corpus (or the
    // parser) is not actually exercising damage detection.
    assert!(
        rejected > cases / 10,
        "only {rejected} of {cases} mutations were rejected - corruption detection looks broken"
    );
}
