use carro_kv::Db;
use tempfile::tempdir;

#[test]
fn test_common_data_types_roundtrip() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("types.db");
    let db = Db::open(&db_path, None).unwrap();

    // 1. i64 Boundary tests
    db.put_i64("", "i64:zero", 0).unwrap();
    db.put_i64("", "i64:pos", 1234567890123456789).unwrap();
    db.put_i64("", "i64:neg", -987654321098765432).unwrap();
    db.put_i64("", "i64:min", i64::MIN).unwrap();
    db.put_i64("", "i64:max", i64::MAX).unwrap();

    assert_eq!(db.get_i64("", "i64:zero").unwrap(), Some(0));
    assert_eq!(
        db.get_i64("", "i64:pos").unwrap(),
        Some(1234567890123456789)
    );
    assert_eq!(
        db.get_i64("", "i64:neg").unwrap(),
        Some(-987654321098765432)
    );
    assert_eq!(db.get_i64("", "i64:min").unwrap(), Some(i64::MIN));
    assert_eq!(db.get_i64("", "i64:max").unwrap(), Some(i64::MAX));

    // 2. f64 Floating point and canonical NaN
    db.put_f64("", "f64:zero", 0.0).unwrap();
    db.put_f64("", "f64:neg_zero", -0.0).unwrap();
    db.put_f64("", "f64:pi", std::f64::consts::PI).unwrap();
    db.put_f64("", "f64:inf", f64::INFINITY).unwrap();
    db.put_f64("", "f64:neg_inf", f64::NEG_INFINITY).unwrap();
    db.put_f64("", "f64:nan", f64::NAN).unwrap();

    assert_eq!(db.get_f64("", "f64:zero").unwrap(), Some(0.0));
    let neg_zero = db.get_f64("", "f64:neg_zero").unwrap().unwrap();
    assert_eq!(neg_zero.to_bits(), (-0.0f64).to_bits());
    assert_eq!(
        db.get_f64("", "f64:pi").unwrap(),
        Some(std::f64::consts::PI)
    );
    assert_eq!(db.get_f64("", "f64:inf").unwrap(), Some(f64::INFINITY));
    assert_eq!(
        db.get_f64("", "f64:neg_inf").unwrap(),
        Some(f64::NEG_INFINITY)
    );

    let nan_val = db.get_f64("", "f64:nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    // Verify IEEE 754 canonical quiet NaN bit pattern (0x7FF8000000000000)
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);

    // 3. bool
    db.put_bool("", "bool:t", true).unwrap();
    db.put_bool("", "bool:f", false).unwrap();

    assert_eq!(db.get_bool("", "bool:t").unwrap(), Some(true));
    assert_eq!(db.get_bool("", "bool:f").unwrap(), Some(false));

    // 4. Batch mixed types
    let mut batch = db.new_batch();
    batch
        .put_i64("metrics", "count", 42)
        .unwrap()
        .put_f64("metrics", "ratio", 0.99)
        .unwrap()
        .put_bool("metrics", "active", true)
        .unwrap();
    db.write_batch(batch).unwrap();

    assert_eq!(db.get_i64("metrics", "count").unwrap(), Some(42));
    assert_eq!(db.get_f64("metrics", "ratio").unwrap(), Some(0.99));
    assert_eq!(db.get_bool("metrics", "active").unwrap(), Some(true));

    // 5. Entry typed accessor verification
    let entries = db.scan_range("metrics", None, None).unwrap();
    assert_eq!(entries.len(), 3);
    for e in entries {
        match e.key.as_str() {
            "count" => {
                assert!(e.is_i64());
                assert_eq!(e.as_i64().unwrap(), 42);
            }
            "ratio" => {
                assert!(e.is_f64());
                assert_eq!(e.as_f64().unwrap(), 0.99);
            }
            "active" => {
                assert!(e.is_bool());
                assert!(e.as_bool().unwrap());
            }
            _ => panic!("Unexpected key"),
        }
    }
}
