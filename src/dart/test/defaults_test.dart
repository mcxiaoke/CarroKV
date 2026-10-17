import 'package:ckv/ckv.dart';
import 'package:test/test.dart';

/// Guards the cross-language default matrix documented in
/// `KV-DB-DESIGN-SPEC.md` §5.1.1.1: every implementation must ship the same
/// conservative, durability-first defaults. A change here is an ADR-level
/// decision and must be applied to all five languages in the same batch.
void main() {
  test('DbOptions defaults match the cross-language matrix', () {
    final o = DbOptions();
    expect(o.readOnly, isFalse);
    expect(o.autoRecoverFromBackup, isFalse); // D1
    expect(o.syncMode, SyncMode.immediate);
    expect(o.syncIntervalMs, 100);
    expect(o.engineMode, StorageEngineMode.snapshotAtomic);
    expect(o.compactionThresholdBytes, 4 * 1024 * 1024); // C8
    expect(o.lockTimeoutMs, 10000); // C19
    expect(o.syncOnWrite, isTrue);
    expect(o.strictCompaction, isFalse); // D2
  });
}
