import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Adaptive compaction: reclaim a WAL whose live payload has collapsed, even
/// when the file never crosses `compactionThresholdBytes`.
///
/// Mirrors the Rust, Go, Kotlin and .NET suites. Three properties are pinned:
///   1. dead-ratio trigger compacts an under-threshold log without losing keys;
///   2. the 2 MB baseline keeps small logs from being rewritten for being dead;
///   3. `compactionThresholdBytes == 0` still disables every automatic path.
const int _baseline = 2 * 1024 * 1024;
const int _noSizeCompaction = 0x7FFFFFFFFFFFFFFF;

void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_adaptive_compaction'),
  );
  var seq = 0;

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

  String tempPath(String name) => p.join(tempBase, '${seq++}_$name');

  DbOptions adaptiveOptions(int threshold) => DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        syncMode: SyncMode.deferred,
        syncIntervalMs: 3600000, // keep the background flush out of the way
        compactionThresholdBytes: threshold,
      );

  void fill(CkvDb db, String ns, int count, int valueLen) {
    final value = 'v' * valueLen;
    for (var i = 0; i < count; i++) {
      db.putString(ns, 'k${i.toString().padLeft(5, '0')}', value);
    }
  }

  String key(int i) => 'k${i.toString().padLeft(5, '0')}';

  test('dead ratio compacts below the size threshold', () {
    final path = tempPath('adaptive.db');
    var db = CkvDb.open(path, adaptiveOptions(_noSizeCompaction));
    try {
      fill(db, 'ns', 300, 10 * 1024);
      final grown = File(path).lengthSync();
      expect(grown, greaterThan(_baseline),
          reason: 'log should exceed the adaptive baseline');

      // The trigger fires as soon as the dead ratio crosses 50%, i.e. part-way
      // through this loop, so the file ends up near live size.
      for (var i = 0; i < 250; i++) {
        expect(db.delete('ns', key(i)), isTrue);
      }

      final after = File(path).lengthSync();
      expect(after, lessThan(_baseline),
          reason: 'dead-ratio compaction should have reclaimed the log: '
              '$grown -> $after bytes');
    } finally {
      db.close();
    }

    db = CkvDb.open(path, adaptiveOptions(_noSizeCompaction));
    try {
      expect(db.count('ns'), 50);
      for (var i = 250; i < 300; i++) {
        expect(db.getString('ns', key(i))?.length, 10 * 1024,
            reason: 'compaction lost key ${key(i)}');
      }
      for (var i = 0; i < 250; i++) {
        expect(db.getString('ns', key(i)), isNull);
      }
    } finally {
      db.close();
    }
  });

  test('small logs are not rewritten for being mostly dead', () {
    final path = tempPath('small.db');
    final db = CkvDb.open(path, adaptiveOptions(_noSizeCompaction));
    try {
      fill(db, 'ns', 50, 1024);
      final grown = File(path).lengthSync();
      expect(grown, lessThan(_baseline));

      for (var i = 0; i < 45; i++) {
        db.delete('ns', key(i));
      }

      final after = File(path).lengthSync();
      expect(after, greaterThanOrEqualTo(grown),
          reason: 'sub-baseline log must not be rewritten: $grown -> $after');
      expect(after, lessThan(_baseline));
      expect(db.count('ns'), 5);
    } finally {
      db.close();
    }
  });

  test('zero threshold disables the dead ratio trigger', () {
    final path = tempPath('disabled.db');
    final db = CkvDb.open(path, adaptiveOptions(0));
    try {
      fill(db, 'ns', 300, 10 * 1024);
      final grown = File(path).lengthSync();

      for (var i = 0; i < 270; i++) {
        db.delete('ns', key(i));
      }

      final after = File(path).lengthSync();
      expect(after, greaterThan(grown),
          reason: 'threshold 0 must disable automatic compaction entirely');
      expect(after, greaterThan(_baseline));
      expect(db.count('ns'), 30);
    } finally {
      db.close();
    }
  });
}
