import 'dart:io';

/// 拿住 `<db>.lock` 直到被杀掉的辅助进程。
///
/// 为什么需要子进程：Dart 的 `RandomAccessFile.lockSync` 在 Linux/macOS 上走 POSIX
/// 记录锁（fcntl），**按进程生效**——同一进程里第二个句柄再加锁会成功，测不出互斥。
/// 跨进程持锁才是这把锁的真实语义，所以测试用子进程模拟"另一个连接"。
///
/// 用法：`dart run support/hold_lock.dart <lockPath> [exclusive|shared]`
/// 拿到锁后向 stdout 打印 `locked`，然后一直挂起。
Future<void> main(List<String> args) async {
  final lockPath = args[0];
  final mode = args.length > 1 && args[1] == 'shared'
      ? FileLock.shared
      : FileLock.exclusive;

  final raf = File(lockPath).openSync(mode: FileMode.append);
  raf.lockSync(mode);
  stdout.writeln('locked');
  await stdout.flush();

  // Hold until the parent kills us; the OS releases the lock on exit either way.
  await Future<void>.delayed(const Duration(days: 1));
}
