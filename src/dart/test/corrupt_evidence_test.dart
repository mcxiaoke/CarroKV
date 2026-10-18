import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:test/test.dart';

/// 规范 §3.4：AOL 尾部自愈截断前必须把整库留证到 `<db>.corrupt.<unix_ms>`。
void main() {
  late Directory tempDir;

  setUp(() => tempDir = Directory.systemTemp.createTempSync('ckv_corrupt_'));
  tearDown(() {
    try {
      tempDir.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('torn tail leaves evidence before truncation', () {
    final dbPath = '${tempDir.path}/a.db';
    final opts = DbOptions()..engineMode = StorageEngineMode.appendOnlyLog;

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'k', 'v');
    db.close();

    final raf = File(dbPath).openSync(mode: FileMode.append);
    raf.writeFromSync(List<int>.filled(10, 0xFF));
    raf.closeSync();
    final damaged = File(dbPath).readAsBytesSync();

    final reopened = CkvDb.open(dbPath, opts);
    expect(reopened.getString('', 'k'), 'v',
        reason: 'intact frame must survive');
    reopened.close();

    final backups = tempDir
        .listSync()
        .whereType<File>()
        .where((f) => f.path.contains('.corrupt.'))
        .toList();
    expect(backups, hasLength(1), reason: 'exactly one evidence file');
    expect(backups.single.readAsBytesSync(), damaged,
        reason: 'evidence must be the pre-truncation content');
    expect(File(dbPath).lengthSync(), lessThan(damaged.length),
        reason: 'live file must be truncated');
  });
}
