use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use std::time::Instant;
use tempfile::tempdir;

#[allow(dead_code)]
struct BenchResult {
    name: &'static str,
    total_ops: usize,
    total_duration_ms: f64,
    ops_per_sec: f64,
    avg_latency_us: f64,
    p50_latency_us: f64,
    p99_latency_us: f64,
    final_file_size: u64,
    logical_bytes: u64,
    waf: f64,
}

fn run_single_put_bench(
    name: &'static str,
    engine: StorageEngineMode,
    sync: SyncMode,
    ops_count: usize,
) -> BenchResult {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("bench_matrix.db");

    let opts = DbOptions::default()
        .engine_mode(engine)
        .sync_mode(sync)
        .sync_interval_ms(50);

    let db = Db::open(&db_path, Some(opts)).unwrap();

    let mut latencies_us = Vec::with_capacity(ops_count);
    let total_start = Instant::now();
    let mut logical_bytes = 0u64;

    for i in 0..ops_count {
        let key = format!("bench_key_{:06}", i);
        let val = format!("bench_val_{:06}_padding_for_128_bytes_payload_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", i);
        logical_bytes += (key.len() + val.len()) as u64;

        let op_start = Instant::now();
        db.put_string("", &key, &val).unwrap();
        latencies_us.push(op_start.elapsed().as_nanos() as f64 / 1000.0);
    }

    // Ensure flushed at the end
    db.flush().unwrap();
    let total_dur = total_start.elapsed();
    let total_dur_ms = total_dur.as_secs_f64() * 1000.0;

    latencies_us.sort_by(|a, b| a.partial_cmp(b).unwrap());

    let avg_latency = latencies_us.iter().sum::<f64>() / (ops_count as f64);
    let p50_idx = (ops_count as f64 * 0.50) as usize;
    let p99_idx = (ops_count as f64 * 0.99).min((ops_count - 1) as f64) as usize;
    let p50 = latencies_us[p50_idx];
    let p99 = latencies_us[p99_idx];

    let final_file_size = fs::metadata(&db_path).map(|m| m.len()).unwrap_or(0);

    // Calculate approximate physical bytes written
    // In SnapshotAtomic: each step rewrites the accumulated snapshot
    // In WAL: each step appends 28B header + entry overhead (~10B + ns + key + val)
    let physical_bytes_written = match engine {
        StorageEngineMode::SnapshotAtomic => {
            // Sum of file sizes after each write if Immediate, or final file size if Manual
            match sync {
                SyncMode::Immediate => {
                    // Cumulative rewrite: sum_{i=1..N} (32 + i * (28 + 10 + entry))
                    // Approximation based on final file size:
                    (ops_count as f64 * (final_file_size as f64 + 32.0) / 2.0) as u64
                }
                _ => final_file_size,
            }
        }
        StorageEngineMode::AppendOnlyLog => final_file_size,
    };

    let waf = (physical_bytes_written as f64) / (logical_bytes.max(1) as f64);
    let ops_per_sec = (ops_count as f64) / total_dur.as_secs_f64();

    BenchResult {
        name,
        total_ops: ops_count,
        total_duration_ms: total_dur_ms,
        ops_per_sec,
        avg_latency_us: avg_latency,
        p50_latency_us: p50,
        p99_latency_us: p99,
        final_file_size,
        logical_bytes,
        waf,
    }
}

#[test]
fn bench_sync_mode_and_wal_matrix() {
    println!("\n=========================================================================================");
    println!("                    CKV STORAGE & SYNC ENGINE MATRIX BENCHMARK");
    println!("=========================================================================================");

    let ops_immediate = 100;
    let ops_deferred = 2000;
    let ops_manual = 5000;

    let res_snap_imm = run_single_put_bench("Snapshot + Immediate", StorageEngineMode::SnapshotAtomic, SyncMode::Immediate, ops_immediate);
    let res_wal_imm = run_single_put_bench("AppendOnlyLog + Immediate", StorageEngineMode::AppendOnlyLog, SyncMode::Immediate, ops_immediate);

    let res_snap_def = run_single_put_bench("Snapshot + Deferred", StorageEngineMode::SnapshotAtomic, SyncMode::Deferred, ops_deferred);
    let res_wal_def = run_single_put_bench("AppendOnlyLog + Deferred", StorageEngineMode::AppendOnlyLog, SyncMode::Deferred, ops_deferred);

    let res_snap_man = run_single_put_bench("Snapshot + Manual", StorageEngineMode::SnapshotAtomic, SyncMode::Manual, ops_manual);
    let res_wal_man = run_single_put_bench("AppendOnlyLog + Manual", StorageEngineMode::AppendOnlyLog, SyncMode::Manual, ops_manual);

    let results = vec![
        res_snap_imm,
        res_wal_imm,
        res_snap_def,
        res_wal_def,
        res_snap_man,
        res_wal_man,
    ];

    println!("{:<28} | {:>7} | {:>10} | {:>10} | {:>9} | {:>9} | {:>8}", 
             "Configuration", "Ops", "Throughput", "Avg Latency", "P50 Lat", "P99 Lat", "WAF");
    println!("{:-<28}-+-{:-<7}-+-{:-<10}-+-{:-<10}-+-{:-<9}-+-{:-<9}-+-{:-<8}", 
             "", "", "", "", "", "", "");

    for r in &results {
        println!("{:<28} | {:>7} | {:>8.0} o/s | {:>8.2} us | {:>7.2} us | {:>7.2} us | {:>7.2}x",
                 r.name, r.total_ops, r.ops_per_sec, r.avg_latency_us, r.p50_latency_us, r.p99_latency_us, r.waf);
    }
    println!("=========================================================================================\n");

    // Compaction & Overwrite Benchmark
    println!("------------------------ Compaction & Overwrite Benchmark ------------------------");
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("wal_overwrite.db");
    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Manual);
    let db = Db::open(&db_path, Some(opts)).unwrap();

    let overwrite_count = 1000;
    for i in 0..overwrite_count {
        db.put_string("", "hot_key", &format!("hot_value_generation_{:05}_with_padding_data_xxxxxxxxxxxxxxxxxxxx", i)).unwrap();
    }
    db.flush().unwrap();

    let uncompacted_size = fs::metadata(&db_path).unwrap().len();
    let compact_start = Instant::now();
    db.compact().unwrap();
    let compact_dur = compact_start.elapsed();
    let compacted_size = fs::metadata(&db_path).unwrap().len();

    let shrink_pct = (1.0 - (compacted_size as f64 / uncompacted_size as f64)) * 100.0;
    println!("1,000 Overwrites -> WAL File Size: {} bytes", uncompacted_size);
    println!("Compaction took: {:?}, Compacted File Size: {} bytes (Shrunk {:.1}%)", compact_dur, compacted_size, shrink_pct);
    println!("-----------------------------------------------------------------------------------\n");
}
