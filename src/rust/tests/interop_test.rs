use ckv::Db;
use std::fs;
use std::path::PathBuf;

#[test]
fn test_rust_writes_for_go() {
    let test_dir = PathBuf::from("../../temp/interop");
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
    db.put_bytes("bin_ns", "binary_key", &[0x01, 0x02, 0x03, 0xFF]).unwrap();
    db.put_string("user_ns", "name", "Alice").unwrap();
    db.put_string("user_ns", "city", "Beijing").unwrap();
    db.put_string("", "😀", "happy").unwrap();

    // Data types
    db.put_i64("types", "int_min", i64::MIN).unwrap();
    db.put_i64("types", "int_max", i64::MAX).unwrap();
    db.put_f64("types", "float_pi", std::f64::consts::PI).unwrap();
    db.put_f64("types", "float_nan", f64::NAN).unwrap();
    db.put_bool("types", "bool_true", true).unwrap();
    db.put_bool("types", "bool_false", false).unwrap();
}

#[test]
fn test_rust_reads_go_written() {
    let db_path = PathBuf::from("../../temp/interop/go_data.db");
    if !db_path.exists() {
        return; // Run after go generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(db.get_string("", "hello").unwrap(), Some("world from go".to_string()));
    assert_eq!(db.get_string("user_ns", "name").unwrap(), Some("Bob".to_string()));
    assert_eq!(db.get_bytes("bin_ns", "binary_key").unwrap(), Some(vec![0xAA, 0xBB, 0xCC]));

    // Read Go written types
    assert_eq!(db.get_i64("types", "go_int").unwrap(), Some(-1234567890987654321));
    assert_eq!(db.get_f64("types", "go_float").unwrap(), Some(std::f64::consts::E));
    let nan_val = db.get_f64("types", "go_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "go_bool").unwrap(), Some(true));
}

#[test]
fn test_rust_reads_dotnet_written() {
    let db_path = PathBuf::from("../../temp/interop/dotnet_data.db");
    if !db_path.exists() {
        return; // Run after dotnet generates it
    }

    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(db.get_string("", "hello").unwrap(), Some("world from dotnet".to_string()));
    assert_eq!(db.get_string("user_ns", "name").unwrap(), Some("Charlie".to_string()));
    assert_eq!(db.get_bytes("bin_ns", "binary_key").unwrap(), Some(vec![0x11, 0x22, 0x33, 0x44]));

    // Read .NET written types
    assert_eq!(db.get_i64("types", "dotnet_int").unwrap(), Some(987654321012345678));
    assert_eq!(db.get_f64("types", "dotnet_float").unwrap(), Some(std::f64::consts::SQRT_2));
    let nan_val = db.get_f64("types", "dotnet_nan").unwrap().unwrap();
    assert!(nan_val.is_nan());
    assert_eq!(nan_val.to_bits(), 0x7FF8000000000000);
    assert_eq!(db.get_bool("types", "dotnet_bool").unwrap(), Some(true));
}
