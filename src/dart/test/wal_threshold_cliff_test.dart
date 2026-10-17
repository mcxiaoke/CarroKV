import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// FIX-21 regression: the automatic-compaction trigger must measure garbage
/// (file size minus live payload), not the absolute file size.
///
/// With the old `newFileSize >= compactionThresholdBytes` condition, a
/// compaction rewrote the file down to exactly the live size, so once live data
/// reached the threshold *every* subsequent write paid for a full-database
/// rewrite (measured at 6 MB live: 26 ms/write instead of ~0.2 ms).
///
/// Assertions are behavioural (file-size deltas) rather than timing based.
/// Mirrors the Rust / Go / Kotlin / .NET suites.
const int _threshold = 4 * 1024 * 1024;

void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_threshold_cliff'),
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

  DbOptions wal(int threshold) => DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        syncMode: SyncMode.deferred,
        syncIntervalMs: 3600000,
        compactionThresholdBytes: threshold,
      );

  /// Loads 6000 x 1 KB with compaction disabled, so the reopened database starts
  /// with a garbage-free log that is already above the 4 MB threshold.
  void loadFixture(String path) {
    final db = CkvDb.open(path, wal(0));
    try {
      final value = 'v' * 1000;
      for (var i = 0; i < 6000; i++) {
        db.putString('ns', 'k${i.toString().padLeft(6, '0')}', value);
      }
      db.flush();
    } finally {
      db.close();
    }
  }

  void overwrite(CkvDb db, int count) {
    final value = 'v' * 1000;
    for (var i = 0; i < count; i++) {
      db.putString('ns', 'k${i.toString().padLeft(6, '0')}', value);
    }
  }

  test('overwrites below the threshold do not rewrite the whole database', () {
    final path = tempPath('cliff.db');
    loadFixture(path);

    final db = CkvDb.open(path, wal(_threshold));
    try {
      final before = File(path).lengthSync();
      expect(before, greaterThan(5500000), reason: 'fixture too small');

      // 50 overwrites = ~50 KB of pure garbage, 80x below the threshold.
      overwrite(db, 50);

      final after = File(path).lengthSync();
      expect(
        after,
        greaterThanOrEqualTo(before + 40000),
        reason: 'file did not grow ($before -> $after): every write is still '
            'triggering a full-database rewrite',
      );
    } finally {
      db.close();
    }
  });

  test('garbage past the threshold still compacts', () {
    final path = tempPath('reclaim.db');
    loadFixture(path);

    final db = CkvDb.open(path, wal(_threshold));
    try {
      final before = File(path).lengthSync();

      // ~4.5 MB of pure garbage: must be reclaimed once, back to live size.
      overwrite(db, 4500);

      final after = File(path).lengthSync();
      expect(
        after,
        lessThan(before + 2000000),
        reason: 'compaction did not reclaim $before -> $after bytes of garbage',
      );
    } finally {
      db.close();
    }
  });
}
