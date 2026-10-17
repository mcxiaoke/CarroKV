import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

/// 启动一个持有 `<db>.lock` 的子进程，等它确认拿到锁后返回。
///
/// Dart 的 `lockSync` 在 POSIX 上走 fcntl（按进程生效），同一进程里的第二个句柄
/// 不会被排除，因此"另一个连接"必须是真的另一个进程。
Future<Process> holdLockInAnotherProcess(String lockPath, {bool shared = false}) async {
  final helper = p.join(Directory.current.path, 'test', 'support', 'hold_lock.dart');
  final proc = await Process.start(
    Platform.resolvedExecutable,
    ['run', helper, lockPath, shared ? 'shared' : 'exclusive'],
  );

  final line = await proc.stdout
      .transform(utf8.decoder)
      .firstWhere((l) => l.trim().isNotEmpty)
      .timeout(const Duration(seconds: 60));

  if (!line.contains('locked')) {
    proc.kill();
    throw StateError('lock holder failed to acquire the lock: $line');
  }
  return proc;
}

/// 结束持有者并等待进程退出（退出即由内核释放锁）。
Future<void> stopHolder(Process proc) async {
  proc.kill();
  await proc.exitCode;
}
