import 'dart:io';

import 'errors.dart';
import 'options.dart';

class LockFile {
  final String lockPath;
  final RandomAccessFile _raf;
  bool _released = false;

  LockFile._(this.lockPath, this._raf);

  static LockFile acquire(String dbPath, DbOptions options) {
    final lockPath = '$dbPath.lock';
    final timeout = Duration(milliseconds: options.lockTimeoutMs);
    final stopwatch = Stopwatch()..start();

    while (true) {
      try {
        final file = File(lockPath);
        final raf = file.openSync(mode: FileMode.write);
        try {
          raf.lockSync(FileLock.exclusive);
          final pid = pidToString();
          raf.writeStringSync('$pid\n${DateTime.now().toIso8601String()}\n');
          raf.flushSync();
          return LockFile._(lockPath, raf);
        } catch (_) {
          raf.closeSync();
          rethrow;
        }
      } catch (e) {
        if (stopwatch.elapsed >= timeout) {
          throw LockedError(
            'Failed to acquire lock on "$lockPath" within ${options.lockTimeoutMs}ms: $e',
          );
        }
        sleep(const Duration(milliseconds: 50));
      }
    }
  }

  static String pidToString() {
    try {
      return pid.toString();
    } catch (_) {
      return '0';
    }
  }

  void release() {
    if (_released) return;
    _released = true;
    try {
      _raf.unlockSync();
    } catch (_) {}
    try {
      _raf.closeSync();
    } catch (_) {}
    try {
      File(lockPath).deleteSync();
    } catch (_) {}
  }

  void dispose() => release();
}
