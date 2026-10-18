import 'dart:convert';
import 'dart:io';
import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final tempBase = p.canonicalize(
      p.join(Directory.current.path, '..', '..', 'temp', 'dart_wal_tests'));

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

  test('WAL append and persistence across reopens', () {
    final dbPath = p.join(tempBase, 'wal_append.db');
    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
    );

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'k1', 'v1');
    db.putString('', 'k2', 'v2');
    db.putString('', 'k1', 'v1_updated');
    db.delete('', 'k2');
    db.close();

    final db2 = CkvDb.open(dbPath, opts);
    expect(db2.getString('', 'k1'), equals('v1_updated'));
    expect(db2.getString('', 'k2'), isNull);
    expect(db2.count(''), equals(1));
    db2.close();
  });

  test('WAL tail truncation recovery on torn write', () {
    final dbPath = p.join(tempBase, 'wal_trunc.db');
    int warningCount = 0;

    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
      onWarning: (code, msg) {
        if (code == 'onTailTruncated') {
          warningCount++;
        }
      },
    );

    // Step 1: Write healthy entries
    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'k1', 'v1');
    db.putString('', 'k2', 'v2');
    db.close();

    final healthyLen = File(dbPath).lengthSync();
    expect(healthyLen > 32, isTrue);

    // Step 2: Inject corrupt tail bytes (torn frame simulation)
    final raf = File(dbPath).openSync(mode: FileMode.append);
    raf.writeFromSync(utf8.encode('CORRUPT_TAIL_BYTES!!'));
    raf.closeSync();

    final corruptedLen = File(dbPath).lengthSync();
    expect(corruptedLen, equals(healthyLen + 20));

    // Step 3: Reopen database - should auto-truncate corrupted tail
    final db2 = CkvDb.open(dbPath, opts);
    expect(warningCount, equals(1));
    expect(db2.getString('', 'k1'), equals('v1'));
    expect(db2.getString('', 'k2'), equals('v2'));

    // Step 4: Write new entry to verify WAL continues healthy append
    db2.putString('', 'k3', 'v3');
    db2.close();

    final db3 = CkvDb.open(dbPath, opts);
    expect(db3.getString('', 'k3'), equals('v3'));
    expect(db3.getString('', 'k1'), equals('v1'));
    db3.close();
  });

  test('WAL compaction creates clean snapshot and truncates file', () {
    final dbPath = p.join(tempBase, 'wal_compact.db');
    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
    );

    final db = CkvDb.open(dbPath, opts);
    for (int i = 0; i < 50; i++) {
      db.putString('', 'k_overwrite', 'val_$i');
    }
    final bloatedSize = db.fileSize();
    expect(bloatedSize > 1000, isTrue);

    // Run compaction
    db.compact();
    final compactedSize = db.fileSize();
    expect(compactedSize < bloatedSize, isTrue);
    expect(db.getString('', 'k_overwrite'), equals('val_49'));
    db.close();

    // Reopen compacted DB
    final db2 = CkvDb.open(dbPath, opts);
    expect(db2.getString('', 'k_overwrite'), equals('val_49'));
    expect(db2.count(''), equals(1));
    db2.close();
  });

  test('WAL drop namespace and multi-namespace operations', () {
    final dbPath = p.join(tempBase, 'wal_namespaces.db');
    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
    );

    final db = CkvDb.open(dbPath, opts);
    db.putString('ns1', 'k1', 'v1');
    db.putString('ns2', 'k2', 'v2');
    db.dropNamespace('ns1');
    db.close();

    final db2 = CkvDb.open(dbPath, opts);
    expect(db2.namespaces(), equals(['ns2']));
    expect(db2.getString('ns1', 'k1'), isNull);
    expect(db2.getString('ns2', 'k2'), equals('v2'));
    db2.close();
  });
}
