use ckv::Db;
use std::fs;
use std::path::PathBuf;

#[test]
fn test_rust_writes_for_go() {
    let test_dir = PathBuf::from("../../temp/interop");
    let _ = fs::create_dir_all(&test_dir);

    let db_path = test_dir.join("rust_data.db");
    if db_path.exists() {
        let _ = fs::remove_file(&db_path);
    }

    let db = Db::open(&db_path, None).unwrap();
    db.put_string("", "hello", "world from rust").unwrap();
    db.put_bytes("bin_ns", "binary_key", &[0x01, 0x02, 0x03, 0xFF]).unwrap();
    db.put_string("user_ns", "name", "Alice").unwrap();
    db.put_string("user_ns", "city", "Beijing").unwrap();
    db.put_string("", "😀", "happy").unwrap();
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
}
