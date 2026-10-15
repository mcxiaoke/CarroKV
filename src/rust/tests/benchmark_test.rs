use ckv::Db;
use std::time::Instant;
use tempfile::tempdir;

#[test]
fn test_performance_quantification() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("bench.db");
    let db = Db::open(&db_path, None).unwrap();

    // 1. Batch Write Benchmark (1,000 keys in a single atomic batch snapshot)
    let start_batch = Instant::now();
    let mut batch = db.new_batch();
    for i in 0..1000 {
        batch
            .put_string("", format!("user:{:05}", i), "value_payload_128_bytes_padding_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")
            .unwrap();
    }
    db.write_batch(batch).unwrap();
    let batch_dur = start_batch.elapsed();
    println!("\n[BENCHMARK] 1,000 batch writes + fsync took: {:?}", batch_dur);

    // 2. Point Read Benchmark (10,000 random reads from memory cache)
    let start_reads = Instant::now();
    for i in 0..10000 {
        let key = format!("user:{:05}", i % 1000);
        let val = db.get_string("", &key).unwrap();
        assert!(val.is_some());
    }
    let read_dur = start_reads.elapsed();
    let read_qps = 10000.0 / read_dur.as_secs_f64();
    println!("[BENCHMARK] 10,000 point reads took: {:?} (QPS: {:.0})", read_dur, read_qps);

    // 3. Range Scan Benchmark (1,000 keys prefix scan)
    let start_scan = Instant::now();
    let count = db.iter_prefix("", Some("user:")).unwrap().count();
    let scan_dur = start_scan.elapsed();
    println!("[BENCHMARK] 1,000 prefix scan took: {:?} (found: {})", scan_dur, count);
    assert_eq!(count, 1000);

    // Assert minimum performance expectations
    assert!(read_qps > 50_000.0, "Point read QPS should exceed 50,000");
}
