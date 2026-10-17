class DbError implements Exception {
  final String message;
  DbError(this.message);

  @override
  String toString() => '$runtimeType: $message';
}

typedef CkvException = DbError;

class CorruptedError extends DbError {
  CorruptedError(super.message);
}

typedef CorruptedDatabaseException = CorruptedError;

class UnsupportedVersionError extends DbError {
  UnsupportedVersionError(super.message);
}

typedef UnsupportedVersionException = UnsupportedVersionError;

class KeyTooLargeError extends DbError {
  KeyTooLargeError(super.message);
}

typedef KeyTooLargeException = KeyTooLargeError;

class LockedError extends DbError {
  LockedError(super.message);
}

typedef LockedException = LockedError;

class TypeMismatchError extends DbError {
  TypeMismatchError(super.message);
}

typedef TypeMismatchException = TypeMismatchError;

class InvalidUtf8Error extends DbError {
  InvalidUtf8Error(super.message);
}

typedef InvalidUtf8Exception = InvalidUtf8Error;

class ConcurrentModificationError extends DbError {
  ConcurrentModificationError(super.message);
}

typedef ConcurrentModificationException = ConcurrentModificationError;

/// 命名空间不合法（UTF-8 字节长度超过 255）。
class InvalidNamespaceError extends DbError {
  InvalidNamespaceError(super.message);
}

typedef InvalidNamespaceException = InvalidNamespaceError;

/// reentrant open 的选项冲突。
///
/// 目前仅 `readOnly` 冲突会抛出：调用方绝不能静默拿到权限与所请求不一致的实例
///（以 `readOnly = true` 打开却命中一个读写单例 = 静默提权）。
class ConflictingOptionsError extends DbError {
  ConflictingOptionsError(super.message);
}

typedef ConflictingOptionsException = ConflictingOptionsError;

/// 操作系统底层文件读写、权限或磁盘 IO 失败。
class IoError extends DbError {
  IoError(super.message);
}

typedef IoException = IoError;
