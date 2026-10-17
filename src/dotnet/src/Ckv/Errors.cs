using System;

// 本文件是错误体系的兼容层：新名（规范名）刻意继承旧名（[Obsolete] 别名），
// 以便既有 catch (旧名) 仍能命中新名实例。该继承必然触发 CS0618，此处显式禁用。
#pragma warning disable CS0618

namespace Ckv
{
    /// <summary>
    /// 所有 CKV 内部错误的公共基类（规范 §5.1.2 名：DbError）。
    /// </summary>
    /// <remarks>
    /// 命名偏离：规范要求基类名为 <c>DbError</c>，与 Rust <c>DbError</c> /
    /// Go <c>DbError</c> 逐字一致；C# 惯例以 Exception 结尾，本项目显式接受该偏离。
    ///
    /// 继承链设计（API v1.0 冻结 §3.1）：
    /// <code>
    /// Exception
    /// └── DbError                     ← 新基类（规范名）
    ///     └── CkvException            ← 旧基类（[Obsolete] 兼容层）
    ///         ├── CorruptedDatabaseException → CorruptedError
    ///         ├── DatabaseLockedException    → LockedError
    ///         └── ...（每个旧名都是对应新名的基类）
    /// </code>
    /// 旧名放在新名**之上**是有意的：这样既有的 <c>catch (CkvException)</c> 与
    /// <c>catch (CorruptedDatabaseException)</c> 在新名实例抛出时仍然命中，
    /// 源码兼容一个弃用周期。
    /// </remarks>
    public class DbError : Exception
    {
        public DbError(string message) : base(message) { }
        public DbError(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 旧基类，保留用于源码兼容（API v1.0 冻结 §3.1）。
    /// 新代码请 catch <see cref="DbError"/>。
    /// </summary>
    [Obsolete("Use DbError (API v1.0 freeze). CkvException remains as a compatibility base so existing catch blocks keep working.")]
    public class CkvException : DbError
    {
        [Obsolete("Use DbError (API v1.0 freeze).")]
        public CkvException(string message) : base(message) { }
        [Obsolete("Use DbError (API v1.0 freeze).")]
        public CkvException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>格式魔数错误、CRC32 校验失败、主库与备份均损坏（规范名 CorruptedError）。</summary>
    public class CorruptedError : CorruptedDatabaseException
    {
        public CorruptedError(string message) : base(message) { }
        public CorruptedError(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>获取 .lock 失败且退避重试超时（规范名 LockedError）。</summary>
    public class LockedError : DatabaseLockedException
    {
        public LockedError(string message) : base(message) { }
    }

    /// <summary>快照落盘前检查发现磁盘 TxID/FileSize 被外部修改（规范名 ConcurrentModificationError）。</summary>
    public class ConcurrentModificationError : ConcurrentModificationException
    {
        public ConcurrentModificationError(string message) : base(message) { }
    }

    /// <summary>getString() 时底层字节并非合法 UTF-8（规范名 InvalidUtf8Error）。</summary>
    public class InvalidUtf8Error : InvalidUtf8Exception
    {
        public InvalidUtf8Error(string message) : base(message) { }
    }

    /// <summary>Key 超过 65,535 字节或 Value 超过 16 MB（规范名 KeyTooLargeError）。</summary>
    public class KeyTooLargeError : KeyTooLargeException
    {
        public KeyTooLargeError(string message) : base(message) { }
    }

    /// <summary>命名空间 UTF-8 字节长度超过 255（规范名 InvalidNamespaceError）。</summary>
    public class InvalidNamespaceError : InvalidNamespaceException
    {
        public InvalidNamespaceError(string message) : base(message) { }
    }

    /// <summary>文件头 FormatReadVer &gt; 3（规范名 UnsupportedVersionError）。</summary>
    public class UnsupportedVersionError : UnsupportedVersionException
    {
        public UnsupportedVersionError(string message) : base(message) { }
    }

    /// <summary>读取或原子增减时 ValueType 与请求目标类型不一致（规范名 TypeMismatchError）。</summary>
    public class TypeMismatchError : TypeMismatchException
    {
        public TypeMismatchError(string message) : base(message) { }
    }

    /// <summary>
    /// 对同一路径 reentrant open 时选项冲突（规范名 ConflictingOptionsError）。
    ///
    /// 目前仅 <c>ReadOnly</c> 冲突会抛出：调用方绝不能静默拿到权限与所请求不一致的实例
    /// （以 <c>ReadOnly = true</c> 打开却命中一个读写单例 = 静默提权）。
    /// </summary>
    public class ConflictingOptionsError : DbError
    {
        public ConflictingOptionsError(string message) : base(message) { }
    }

    // ---------------------------------------------------------------------
    // 以下为 API v1.0 之前的旧命名。每个旧名都是对应新名的**基类**，
    // 因此 catch (旧名) 在新名实例抛出时仍然命中（源码兼容一个弃用周期）。
    // ---------------------------------------------------------------------

    /// <obsolete>Use <see cref="CorruptedError"/>.</obsolete>
    [Obsolete("Use CorruptedError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class CorruptedDatabaseException : CkvException
    {
        [Obsolete("Use CorruptedError (API v1.0 freeze).")]
        public CorruptedDatabaseException(string message) : base(message) { }
        [Obsolete("Use CorruptedError (API v1.0 freeze).")]
        public CorruptedDatabaseException(string message, Exception inner) : base(message, inner) { }
    }

    /// <obsolete>Use <see cref="LockedError"/>.</obsolete>
    [Obsolete("Use LockedError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class DatabaseLockedException : CkvException
    {
        [Obsolete("Use LockedError (API v1.0 freeze).")]
        public DatabaseLockedException(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="ConcurrentModificationError"/>.</obsolete>
    [Obsolete("Use ConcurrentModificationError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class ConcurrentModificationException : CkvException
    {
        [Obsolete("Use ConcurrentModificationError (API v1.0 freeze).")]
        public ConcurrentModificationException(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="InvalidUtf8Error"/>.</obsolete>
    [Obsolete("Use InvalidUtf8Error (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class InvalidUtf8Exception : CkvException
    {
        [Obsolete("Use InvalidUtf8Error (API v1.0 freeze).")]
        public InvalidUtf8Exception(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="KeyTooLargeError"/>.</obsolete>
    [Obsolete("Use KeyTooLargeError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class KeyTooLargeException : CkvException
    {
        [Obsolete("Use KeyTooLargeError (API v1.0 freeze).")]
        public KeyTooLargeException(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="InvalidNamespaceError"/>.</obsolete>
    [Obsolete("Use InvalidNamespaceError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class InvalidNamespaceException : CkvException
    {
        [Obsolete("Use InvalidNamespaceError (API v1.0 freeze).")]
        public InvalidNamespaceException(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="UnsupportedVersionError"/>.</obsolete>
    [Obsolete("Use UnsupportedVersionError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class UnsupportedVersionException : CkvException
    {
        [Obsolete("Use UnsupportedVersionError (API v1.0 freeze).")]
        public UnsupportedVersionException(string message) : base(message) { }
    }

    /// <obsolete>Use <see cref="TypeMismatchError"/>.</obsolete>
    [Obsolete("Use TypeMismatchError (API v1.0 freeze). Kept as its base class so catch blocks still match.")]
    public class TypeMismatchException : CkvException
    {
        [Obsolete("Use TypeMismatchError (API v1.0 freeze).")]
        public TypeMismatchException(string message) : base(message) { }
    }
}
#pragma warning restore CS0618
