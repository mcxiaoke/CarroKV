use carro_kv::Db;
use std::fs;
use std::path::PathBuf;

fn interop_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CKV_INTEROP_DIR") {
        if !dir.is_empty() {
            return PathBuf::from(dir);
        }
    }
    std::env::temp_dir().join("ckv_interop")
}

#[test]
fn test_rust_writes_for_go() {
    let test_dir = interop_dir();
    let _ = fs::create_dir_all(&test_dir);

    let db_path = test_dir.join("rust_data.db");
    // Start from a clean fixture: the primary *and* its sidecars. Removing only
    // the primary would leave a previous generation behind, which is refused by
    // default since D1-8 (a missing primary next to a `.bak` is never a fresh
    // database).
    for suffix in ["", ".bak", ".tmp", ".lock"] {
        let sidecar = test_dir.join(format!("rust_data.db{suffix}"));
        if sidecar.exists() {
            let _ = fs::remove_file(&sidecar);
        }
    }

    let db = Db::open(&db_path, None).unwrap();
    db.put_string("", "hello", "world from rust").unwrap();
    db.put_bytes("bin_ns", "binary_key", [0x01, 0x02, 0x03, 0xFF])
        .unwrap();
    db.put_string("user_ns", "name", "Alice").unwrap();
    db.put_string("user_ns", "city", "Beijing").unwrap();
    db.put_string("", "😀", "happy").unwrap();

    // Data types
    db.put_i64("types", "int_min", i64::MIN).unwrap();
    db.put_i64("types", "int_max", i64::MAX).unwrap();
    db.put_f64("types", "float_pi", std::f64::consts::PI)
        .unwrap();
    db.put_f64("types", "float_nan", f64::NAN).unwrap();
    db.put_bool("types", "bool_true", true).unwrap();
    db.put_bool("types", "bool_false", false).unwrap();
}

#[test]
fn test_rust_reads_go_written() {
    let db_path = interop_dir().join("go_data.db");
    if !db_path.exists() {
        return; // Run after go generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(
        db.get_string("", "hello").unwrap(),
        Some("world from go".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "name").unwrap(),
        Some("Bob".to_string())
    );
    assert_eq!(
        db.get_bytes("bin_ns", "binary_key").unwrap(),
        Some(vec![0xAA, 0xBB, 0xCC])
    );

    // Read Go written types
    assert_eq!(
        db.get_i64("types", "go_int").unwrap(),
        Some(-1234567890987654321)
    );
    assert_eq!(
        db.get_f64("types", "go_float").unwrap(),
        Some(std::f64::consts::E)
    );
    let nan_val = db.get_f64("types", "go_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "go_bool").unwrap(), Some(true));
}

#[test]
fn test_rust_reads_dotnet_written() {
    let db_path = interop_dir().join("dotnet_data.db");
    if !db_path.exists() {
        return; // Run after dotnet generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(
        db.get_string("", "hello").unwrap(),
        Some("world from dotnet".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "name").unwrap(),
        Some("Charlie".to_string())
    );
    assert_eq!(
        db.get_bytes("bin_ns", "binary_key").unwrap(),
        Some(vec![0x11, 0x22, 0x33, 0x44])
    );

    // Read .NET written types
    assert_eq!(
        db.get_i64("types", "dotnet_int").unwrap(),
        Some(987654321012345678)
    );
    assert_eq!(
        db.get_f64("types", "dotnet_float").unwrap(),
        Some(std::f64::consts::SQRT_2)
    );
    let nan_val = db.get_f64("types", "dotnet_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "dotnet_bool").unwrap(), Some(true));
}

/// Kotlin fixture: `kotlin_data.db`, written by `ckv.InteropTest`.
#[test]
fn test_rust_reads_kotlin_written() {
    let db_path = interop_dir().join("kotlin_data.db");
    if !db_path.exists() {
        return; // Run after the Kotlin suite generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(
        db.get_string("", "hello").unwrap(),
        Some("world from kotlin".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "name").unwrap(),
        Some("Carol".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "city").unwrap(),
        Some("Shanghai".to_string())
    );
    assert_eq!(
        db.get_bytes("bin_ns", "binary_key").unwrap(),
        Some(vec![0x01, 0x02, 0x03, 0xFF])
    );
    assert_eq!(db.get_string("", "😀").unwrap(), Some("happy".to_string()));
    assert_eq!(db.get_i64("types", "int_min").unwrap(), Some(i64::MIN));
    assert_eq!(db.get_i64("types", "int_max").unwrap(), Some(i64::MAX));
    assert_eq!(
        db.get_f64("types", "float_pi").unwrap(),
        Some(std::f64::consts::PI)
    );
    let nan_val = db.get_f64("types", "float_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "bool_true").unwrap(), Some(true));
    assert_eq!(db.get_bool("types", "bool_false").unwrap(), Some(false));
}

/// Dart fixture: `dart_data.db`, written by `test/interop_test.dart`.
#[test]
fn test_rust_reads_dart_written() {
    let db_path = interop_dir().join("dart_data.db");
    if !db_path.exists() {
        return; // Run after the Dart suite generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(
        db.get_string("", "hello").unwrap(),
        Some("world from dart".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "name").unwrap(),
        Some("Dave".to_string())
    );
    assert_eq!(
        db.get_string("user_ns", "city").unwrap(),
        Some("Shenzhen".to_string())
    );
    assert_eq!(
        db.get_bytes("bin_ns", "binary_key").unwrap(),
        Some(vec![0xDE, 0xAD, 0xBE, 0xEF])
    );
    assert_eq!(db.get_i64("types", "int_min").unwrap(), Some(i64::MIN));
    assert_eq!(db.get_i64("types", "int_max").unwrap(), Some(i64::MAX));
    assert_eq!(
        db.get_f64("types", "float_pi").unwrap(),
        Some(std::f64::consts::PI)
    );
    let nan_val = db.get_f64("types", "float_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "bool_true").unwrap(), Some(true));
    assert_eq!(db.get_bool("types", "bool_false").unwrap(), Some(false));
}
