package ckv

open class CkvException(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

class CorruptedError(message: String) : CkvException(message)
class TypeMismatchError(message: String) : CkvException(message)
class LockedError(message: String) : CkvException(message)
class KeyNotFoundError(message: String) : CkvException(message)
class InvalidKeyError(message: String) : CkvException(message)
class UnsupportedVersionError(message: String) : CkvException(message)
class InvalidUtf8Error(message: String) : CkvException(message)
open class TransactionConflictError(message: String) : CkvException(message)

/**
 * 规范 §4 的统一超限错误：Key 超过 64KB **或** Value 超过 16MB。
 *
 * `ValueTooLongError` 保留为子类，使既有 `catch (ValueTooLongError)` 仍能工作，
 * 同时任何 `catch (KeyTooLongError)` / `KeyTooLargeError` 都能捕获两类超限——
 * 旧实现的 value 超限会从这套体系里漏出去。
 */
open class KeyTooLongError(message: String) : CkvException(message)
class ValueTooLongError(message: String) : KeyTooLongError(message)
class PayloadTooLargeError(message: String) : CkvException(message)

/**
 * reentrant open 的选项冲突。
 *
 * 目前仅 `readOnly` 冲突会抛出：调用方绝不能静默拿到权限与所请求不一致的实例
 * （以 `readOnly = true` 打开却命中一个读写单例 = 静默提权）。
 */
class ConflictingOptionsError(message: String) : CkvException(message)

/** 命名空间不合法（字节长度超过 255，或含非法字符）。 */
class InvalidNamespaceError(message: String) : CkvException(message)

/** IO 异常封装（如写入 WAL 或快照落盘失败）。 */
class IoError(message: String, cause: Throwable? = null) : CkvException(message, cause)

/** 规范统一根错误名（API-SPEC §4） */
typealias DbError = CkvException

/** 规范统一超限错误名（API-SPEC §4） */
typealias KeyTooLargeError = KeyTooLongError

/** 规范统一并发冲突错误名（API-SPEC §4） */
typealias ConcurrentModificationError = TransactionConflictError
