import 'dart:io';
import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final tempBase = p.canonicalize(p.join(Directory.current.path, '..', '..', 'temp', 'dart_sync_tests'));

  setUpAll(() {
    final d = Directory(tempBase);
    if (d.existsSync()) d.deleteSync(recursive: true);
    d.createSync(recursive: true);
  });

  tearDownAll(() {
    final d = Directory(tempBase);
    if (d.existsSync()) {
      try {
        d.deleteSync(recursive: true);
      } catch (_) {}
    }
  });

  test('SyncMode.manual flush and close auto-persists', () {
    final dbPath = p.join(tempBase, 'manual.db');
    final opts = DbOptions(syncMode: SyncMode.manual);

    // 1. Manual mode writes
    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'k1', 'v1');
    db.putString('', 'k2', 'v2');

    // Immediately readable in-memory
    expect(db.getString('', 'k1'), equals('v1'));
    expect(db.getString('', 'k2'), equals('v2'));

    // Explicit flush
    db.flush();
    expect(File(dbPath).existsSync(), isTrue);
    db.close();

    // Reopen to verify persistence
    final db2 = CkvDb.open(dbPath);
    expect(db2.getString('', 'k1'), equals('v1'));
    expect(db2.getString('', 'k2'), equals('v2'));
    db2.close();

    // 2. Test auto-flush on close
    final dbPathAuto = p.join(tempBase, 'manual_close.db');
    final db3 = CkvDb.open(dbPathAuto, opts);
    db3.putString('', 'k_auto', 'v_auto');
    db3.close();

    final db4 = CkvDb.open(dbPathAuto);
    expect(db4.getString('', 'k_auto'), equals('v_auto'));
    db4.close();
  });

  test('SyncMode.deferred background timer flush', () async {
    final dbPath = p.join(tempBase, 'deferred.db');
    final opts = DbOptions(
      syncMode: SyncMode.deferred,
      syncIntervalMs: 50,
    );

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'key_async', 'val_async');
    expect(db.getString('', 'key_async'), equals('val_async'));

    // Wait for background timer to flush (> 100ms)
    final deadline = DateTime.now().add(const Duration(seconds: 3));
    while (!File(dbPath).existsSync() && DateTime.now().isBefore(deadline)) {
      await Future.delayed(const Duration(milliseconds: 20));
    }
    expect(File(dbPath).existsSync(), isTrue);
    db.close();

    final db2 = CkvDb.open(dbPath);
    expect(db2.getString('', 'key_async'), equals('val_async'));
    db2.close();
  });

  test('SyncMode high frequency write coalescing', () {
    final dbPath = p.join(tempBase, 'coalesce.db');
    final opts = DbOptions(syncMode: SyncMode.manual);

    final db = CkvDb.open(dbPath, opts);
    for (int i = 0; i < 100; i++) {
      db.putInt64('', 'counter', i);
    }
    db.flush();
    db.close();

    final db2 = CkvDb.open(dbPath);
    expect(db2.getInt64('', 'counter'), equals(99));
    db2.close();
  });
}
