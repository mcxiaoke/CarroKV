package ckv

open class CkvException(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

class CorruptedError(message: String) : CkvException(message)
class TypeMismatchError(message: String) : CkvException(message)
class LockedError(message: String) : CkvException(message)
class KeyNotFoundError(message: String) : CkvException(message)
class InvalidKeyError(message: String) : CkvException(message)
class UnsupportedVersionError(message: String) : CkvException(message)
class InvalidUtf8Error(message: String) : CkvException(message)
class TransactionConflictError(message: String) : CkvException(message)
class KeyTooLongError(message: String) : CkvException(message)
class ValueTooLongError(message: String) : CkvException(message)
class PayloadTooLargeError(message: String) : CkvException(message)
