import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:test/test.dart';

void main() {
  late Directory tempDir;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('ckv_dart_convenience_');
  });

  tearDown(() {
    if (tempDir.existsSync()) {
      tempDir.deleteSync(recursive: true);
    }
  });

  test('CkvDb default namespace indexer and DefaultNs facade', () {
    final dbPath = '${tempDir.path}/test_convenience.db';
    final db = CkvDb.open(dbPath);
    try {
      // 1. 运算符索引器语法糖
      db['strKey'] = 'strVal';
      expect(db['strKey'], equals('strVal'));
      expect(db.getString(null, 'strKey'), equals('strVal'));

      db['strKey'] = null;
      expect(db['strKey'], isNull);
      expect(db.containsKey(null, 'strKey'), isFalse);

      // 2. defaultNs 门面
      final def = db.defaultNs;
      def.putString('strKey', 'strVal');
      expect(def.getString('strKey'), equals('strVal'));

      def.putBytes('byteKey', Uint8List.fromList(utf8.encode('byteVal')));
      expect(utf8.decode(def.getBytes('byteKey')!), equals('byteVal'));

      def.putInt64('intKey', 42);
      expect(def.getInt64('intKey'), equals(42));
      expect(def.incrementInt64('intKey', 8), equals(50));
      expect(def.getInt64('intKey'), equals(50));

      def.putFloat64('floatKey', 3.14159);
      expect(def.getFloat64('floatKey'), closeTo(3.14159, 0.0001));

      def.putBool('boolKey', true);
      expect(def.getBool('boolKey'), isTrue);

      expect(def.containsKey('strKey'), isTrue);
      expect(def.count(), equals(5));

      final keys = def.keys();
      expect(keys, contains('strKey'));
      expect(keys, contains('intKey'));

      final prefixItems = def.iterPrefix('str');
      expect(prefixItems.length, equals(1));
      expect(prefixItems[0].key, equals('strKey'));

      final rangeItems = def.scanRange('a', 'z');
      expect(rangeItems.isNotEmpty, isTrue);

      expect(def.delete('strKey'), isTrue);
      expect(def.containsKey('strKey'), isFalse);

      expect(def.clear(), isTrue);
      expect(def.count(), equals(0));

      // 3. 自定义命名空间门面 ns('users')
      final userScope = db.ns('users');
      userScope['user:1'] = 'Alice';
      userScope.putInt64('age:1', 30);

      expect(userScope['user:1'], equals('Alice'));
      expect(db.getString('users', 'user:1'), equals('Alice'));
      expect(userScope.getInt64('age:1'), equals(30));
      expect(userScope.count(), equals(2));

      expect(userScope.delete('user:1'), isTrue);
      expect(userScope.containsKey('user:1'), isFalse);

      // 4. Batch 门面与索引器
      db.batch((b) {
        b['batchKey'] = 'batchVal';
        b.putInt64(null, 'batchInt', 999);
        b.ns('audit')['action'] = 'login';

        // 读己所写
        expect(b['batchKey'], equals('batchVal'));
        expect(b.ns('audit')['action'], equals('login'));
      });

      expect(db['batchKey'], equals('batchVal'));
      expect(db.getInt64(null, 'batchInt'), equals(999));
      expect(db.getString('audit', 'action'), equals('login'));

      // 5. Snapshot 门面与索引器
      final snap = db.snapshot();
      expect(snap['batchKey'], equals('batchVal'));
      expect(snap.defaultNs.getString('batchKey'), equals('batchVal'));
      expect(snap.ns('audit')['action'], equals('login'));
      expect(snap.defaultNs.count(), equals(2));
    } finally {
      db.close();
    }
  });
}
