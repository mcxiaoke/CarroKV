using System;

namespace Ckv
{
    public class CkvException : Exception
    {
        public CkvException(string message) : base(message) { }
        public CkvException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CorruptedDatabaseException : CkvException
    {
        public CorruptedDatabaseException(string message) : base(message) { }
        public CorruptedDatabaseException(string message, Exception inner) : base(message, inner) { }
    }

    public class DatabaseLockedException : CkvException
    {
        public DatabaseLockedException(string message) : base(message) { }
    }

    public class ConcurrentModificationException : CkvException
    {
        public ConcurrentModificationException(string message) : base(message) { }
    }

    public class InvalidUtf8Exception : CkvException
    {
        public InvalidUtf8Exception(string message) : base(message) { }
    }

    public class KeyTooLargeException : CkvException
    {
        public KeyTooLargeException(string message) : base(message) { }
    }

    public class InvalidNamespaceException : CkvException
    {
        public InvalidNamespaceException(string message) : base(message) { }
    }

    public class UnsupportedVersionException : CkvException
    {
        public UnsupportedVersionException(string message) : base(message) { }
    }

    public class TypeMismatchException : CkvException
    {
        public TypeMismatchException(string message) : base(message) { }
    }
}
