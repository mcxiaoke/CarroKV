//! Scale profile: how the three linear costs behave as the live dataset grows.
//!
//! Measured per size (2 / 10 / 50 / 100 MB live, 1 KB values):
//!   * open time (5 reopens, median + max);
//!   * resident memory after open;
//!   * AOL steady-state overwrite latency p50 / p99 (compaction temporarily far
//!     away, because the trigger needs 4 MB of garbage);
//!   * AOL compaction pause p50 / p99 (10 explicit `compact()` calls);
//!   * SnapshotAtomic + Immediate single-write latency p50 / p99 - this is the
//!     default configuration, and by design every write rewrites the whole file.
//!
//! The two write measurements are reported separately on purpose: with a 4 MB
//! garbage threshold, compactions happen once per ~4000 writes, so a p99 taken
//! over a few thousand samples never contains one. Mixing them into a single
//! percentile would understate the pause.

use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

const KB: usize = 1024;
const VAL_LEN: usize = 1024;

fn aol(threshold: u64) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(3_600_000)
        .compaction_threshold_bytes(threshold)
}

fn snap_default() -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::SnapshotAtomic)
        .sync_mode(SyncMode::Immediate)
}

fn pct(mut v: Vec<f64>) -> (f64, f64, f64, usize) {
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let n = v.len();
    let at = |p: f64| v[((n as f64 * p) as usize).min(n - 1)];
    (at(0.50), at(0.99), *v.last().unwrap(), n)
}

fn rss_bytes() -> Option<u64> {
    #[cfg(windows)]
    {
        let pid = std::process::id();
        let script = format!("(Get-Process -Id {pid}).WorkingSet64");
        let out = std::process::Command::new("powershell")
            .args(["-NoProfile", "-Command", &script])
            .output()
            .ok()?;
        String::from_utf8_lossy(&out.stdout).trim().parse().ok()
    }
    #[cfg(target_os = "linux")]
    {
        let statm = fs::read_to_string("/proc/self/statm").ok()?;
        let pages: u64 = statm.split_whitespace().nth(1)?.parse().ok()?;
        Some(pages * 4096)
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        None
    }
}

fn build_aol_fixture(path: &Path, keys: usize) -> f64 {
    let value = "v".repeat(VAL_LEN);
    let t = Instant::now();
    let db = Db::open(path, Some(aol(0))).unwrap();
    for i in 0..keys {
        db.put_string("ns", format!("k{i:08}"), &value).unwrap();
    }
    db.flush().unwrap();
    db.close().unwrap();
    t.elapsed().as_secs_f64()
}

fn main() {
    let out_dir = PathBuf::from("out");
    fs::create_dir_all(&out_dir).unwrap();

    println!(
        "| live | file | load | open p50 | open max | RSS | steady p50 | steady p99 | compact p50 | compact p99 | snap p50 | snap p99 |"
    );
    println!("|---|---|---|---|---|---|---|---|---|---|---|---|");

    for mb in [2usize, 10, 50, 100] {
        let keys = mb * KB; // 1 KB per key
        let path = out_dir.join(format!("aol_{mb}mb.db"));
        if path.exists() {
            let _ = fs::remove_file(&path);
        }
        let _ = fs::remove_file(format!("{}.bak", path.display()));

        let load_s = build_aol_fixture(&path, keys);
        let file_bytes = fs::metadata(&path).unwrap().len();

        // --- open time (reopen, no writes) ---
        let mut opens = Vec::new();
        for _ in 0..5 {
            let t = Instant::now();
            let db = Db::open(&path, Some(aol(4 * 1024 * 1024))).unwrap();
            opens.push(t.elapsed().as_secs_f64() * 1000.0);
            db.close().unwrap();
        }
        let (open_p50, _, open_max, _) = pct(opens);

        // --- RSS after open ---
        let db = Db::open(&path, Some(aol(4 * 1024 * 1024))).unwrap();
        let rss = rss_bytes().unwrap_or(0);

        // --- AOL steady-state overwrites (4 MB garbage needed, so no compaction
        // is expected inside 2000 x 1 KB) ---
        let value = "v".repeat(VAL_LEN);
        let mut steady = Vec::new();
        for i in 0..2000 {
            let t = Instant::now();
            db.put_string("ns", format!("k{i:08}"), &value).unwrap();
            steady.push(t.elapsed().as_secs_f64() * 1000.0);
        }
        let (steady_p50, steady_p99, _, _) = pct(steady);

        // --- explicit compaction pause ---
        let mut compactions = Vec::new();
        for _ in 0..10 {
            let t = Instant::now();
            db.compact().unwrap();
            compactions.push(t.elapsed().as_secs_f64() * 1000.0);
        }
        let (compact_p50, compact_p99, _, _) = pct(compactions);
        db.close().unwrap();

        // --- default mode: SnapshotAtomic + Immediate, one write = full rewrite ---
        let snap_path = out_dir.join(format!("snap_{mb}mb.db"));
        let _ = fs::remove_file(&snap_path);
        let _ = fs::remove_file(format!("{}.bak", snap_path.display()));
        let puts = if mb >= 50 { 12 } else { 25 };
        // Bulk-load through the deferred path (memory-only writes, one flush) so
        // building the fixture does not take hours; the measured writes below then
        // run with the default Immediate mode against the same file.
        {
            let bulk = DbOptions::default()
                .sync_mode(SyncMode::Deferred)
                .sync_interval_ms(3_600_000);
            let db = Db::open(&snap_path, Some(bulk)).unwrap();
            for i in 0..keys {
                db.put_string("ns", format!("k{i:08}"), &value).unwrap();
            }
            db.flush().unwrap();
            db.close().unwrap();
        }
        let snap_db = Db::open(&snap_path, Some(snap_default())).unwrap();
        let mut snap = Vec::new();
        for i in 0..puts {
            let t = Instant::now();
            snap_db
                .put_string("ns", format!("hot{i:04}"), &value)
                .unwrap();
            snap.push(t.elapsed().as_secs_f64() * 1000.0);
        }
        let (snap_p50, snap_p99, _, _) = pct(snap);
        snap_db.close().unwrap();

        println!(
            "| {mb} MB | {:.1} MB | {load_s:.2}s | {open_p50:.1} ms | {open_max:.1} ms | {:.0} MB | {steady_p50:.2} ms | {steady_p99:.2} ms | {compact_p50:.1} ms | {compact_p99:.1} ms | {snap_p50:.1} ms | {snap_p99:.1} ms |",
            file_bytes as f64 / 1e6,
            rss as f64 / 1e6
        );
    }
}
