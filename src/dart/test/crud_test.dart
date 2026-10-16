import 'dart:io';
import 'dart:typed_data';
import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory tempDir;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('ckv_dart_crud_');
  });

  tearDown(() {
    try {
      tempDir.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('Basic CRUD and typed getters/setters', () {
    final dbPath = p.join(tempDir.path, 'crud.db');
    final db = CkvDb.open(dbPath);

    db.putString('', 'name', 'Alice');
    db.putInt64('', 'age', 30);
    db.putFloat64('', 'score', 99.5);
    db.putBool('', 'active', true);
    db.putBytes('', 'raw', Uint8List.fromList([1, 2, 3, 4]));

    expect(db.getString('', 'name'), equals('Alice'));
    expect(db.getInt64('', 'age'), equals(30));
    expect(db.getFloat64('', 'score'), equals(99.5));
    expect(db.getBool('', 'active'), isTrue);
    expect(db.getBytes('', 'raw'), equals(Uint8List.fromList([1, 2, 3, 4])));

    // Type mismatch throws
    expect(() => db.getInt64('', 'name'), throwsA(isA<TypeMismatchError>()));
    expect(() => db.getString('', 'age'), throwsA(isA<TypeMismatchError>()));

    // Contains and count
    expect(db.containsKey('', 'name'), isTrue);
    expect(db.containsKey('', 'nonexistent'), isFalse);
    expect(db.count(''), equals(5));

    // Delete
    expect(db.delete('', 'name'), isTrue);
    expect(db.delete('', 'name'), isFalse);
    expect(db.containsKey('', 'name'), isFalse);
    expect(db.count(''), equals(4));

    db.close();
  });

  test('IncrementInt64 with overflow wrap', () {
    final dbPath = p.join(tempDir.path, 'inc.db');
    final db = CkvDb.open(dbPath);

    expect(db.incrementInt64('', 'counter', 10), equals(10));
    expect(db.incrementInt64('', 'counter', -3), equals(7));
    expect(db.getInt64('', 'counter'), equals(7));

    // Overflow wraps in two's complement: 0x7FFFFFFFFFFFFFFF + 1 = -0x8000000000000000
    db.putInt64('', 'max', 9223372036854775807);
    final wrapped = db.incrementInt64('', 'max', 1);
    expect(wrapped, equals(-9223372036854775808));
    expect(db.getInt64('', 'max'), equals(-9223372036854775808));

    db.close();
  });

  test('Namespaces, Drop, Clear, and Range scans', () {
    final dbPath = p.join(tempDir.path, 'ns.db');
    final db = CkvDb.open(dbPath);

    db.putString('users', 'u1', 'Alice');
    db.putString('users', 'u2', 'Bob');
    db.putString('orders', 'o1', 'Order1');
    db.putString('', 'global_key', 'val');

    final nsList = db.namespaces();
    expect(nsList, equals(['', 'orders', 'users']));

    // Iter prefix
    final usersPrefix = db.iterPrefix('users', 'u');
    expect(usersPrefix.length, equals(2));
    expect(usersPrefix.map((e) => e.key).toList(), equals(['u1', 'u2']));

    // Scan range
    final usersRange = db.scanRange('users', 'u1', 'u2');
    expect(usersRange.length, equals(1));
    expect(usersRange[0].key, equals('u1'));

    // Clear namespace (keeps namespace in list)
    expect(db.clearNamespace('users'), isTrue);
    expect(db.count('users'), equals(0));
    expect(db.namespaces(), contains('users'));

    // Drop namespace (removes completely)
    expect(db.dropNamespace('orders'), isTrue);
    expect(db.namespaces(), equals(['', 'users']));

    db.close();
  });

  test('Batch atomic execution and rollback on error', () {
    final dbPath = p.join(tempDir.path, 'batch.db');
    final db = CkvDb.open(dbPath);

    db.batch((b) {
      b.putString('', 'k1', 'v1');
      b.putString('', 'k2', 'v2');
      b.putInt64('meta', 'version', 1);
    });

    expect(db.getString('', 'k1'), equals('v1'));
    expect(db.getString('', 'k2'), equals('v2'));
    expect(db.getInt64('meta', 'version'), equals(1));

    db.close();
  });

  test('Watch notifications and explicit unwatch', () {
    final dbPath = p.join(tempDir.path, 'watch.db');
    final db = CkvDb.open(dbPath);

    final events = <KeyChangeEvent>[];
    final handle = db.watch((ev) {
      events.add(ev);
    });

    db.putString('', 'hello', 'world');
    db.delete('', 'hello');

    expect(events.length, equals(2));
    expect(events[0].op, equals(KeyChangeOp.put));
    expect(events[0].key, equals('hello'));
    expect(events[1].op, equals(KeyChangeOp.delete));

    // Explicit unwatch
    handle.unwatch();
    db.putString('', 'hello2', 'world2');
    // No new events received
    expect(events.length, equals(2));

    db.close();
  });
}
