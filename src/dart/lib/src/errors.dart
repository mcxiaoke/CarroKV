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
