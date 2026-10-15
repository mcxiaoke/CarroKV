using System;
using System.Text;

namespace Ckv
{
    public sealed class Entry
    {
        public string Namespace { get; }
        public string Key { get; }
        public byte ValueType { get; }
        public byte[] RawBytes { get; }

        public Entry(string @namespace, string key, byte valueType, byte[] rawBytes)
        {
            Namespace = @namespace;
            Key = key;
            ValueType = valueType;
            RawBytes = rawBytes;
        }

        public bool IsBytes => ValueType == Constants.ValTypeBytes;
        public bool IsString => ValueType == Constants.ValTypeString;
        public bool IsInt64 => ValueType == Constants.ValTypeInt64;
        public bool IsFloat64 => ValueType == Constants.ValTypeFloat64;
        public bool IsBool => ValueType == Constants.ValTypeBool;

        public string AsString()
        {
            if (ValueType != Constants.ValTypeString)
                throw new TypeMismatchException($"Key '{Key}' has value_type {ValueType}, expected string (0x01)");
            if (!Utf8Helper.IsValid(RawBytes))
                throw new InvalidUtf8Exception($"Invalid UTF-8 byte sequence in key '{Key}'");
            return Encoding.UTF8.GetString(RawBytes);
        }

        public long AsInt64()
        {
            if (ValueType != Constants.ValTypeInt64)
                throw new TypeMismatchException($"Key '{Key}' has value_type {ValueType}, expected int64 (0x02)");
            return BinaryHelper.DecodeInt64(RawBytes);
        }

        public double AsFloat64()
        {
            if (ValueType != Constants.ValTypeFloat64)
                throw new TypeMismatchException($"Key '{Key}' has value_type {ValueType}, expected float64 (0x03)");
            return BinaryHelper.DecodeFloat64(RawBytes);
        }

        public bool AsBool()
        {
            if (ValueType != Constants.ValTypeBool)
                throw new TypeMismatchException($"Key '{Key}' has value_type {ValueType}, expected bool (0x04)");
            return BinaryHelper.DecodeBool(RawBytes);
        }
    }

    public static class Utf8Helper
    {
        public static bool IsValid(ReadOnlySpan<byte> bytes)
        {
            int i = 0;
            int len = bytes.Length;
            while (i < len)
            {
                byte b0 = bytes[i++];
                if (b0 <= 0x7F)
                {
                    // 1-byte ASCII
                    continue;
                }
                else if (b0 >= 0xC2 && b0 <= 0xDF)
                {
                    // 2-byte sequence
                    if (i >= len) return false;
                    byte b1 = bytes[i++];
                    if ((b1 & 0xC0) != 0x80) return false;
                }
                else if (b0 >= 0xE0 && b0 <= 0xEF)
                {
                    // 3-byte sequence
                    if (i + 1 >= len) return false;
                    byte b1 = bytes[i++];
                    byte b2 = bytes[i++];
                    if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80) return false;
                    // Overlong and surrogate checks
                    if (b0 == 0xE0 && b1 < 0xA0) return false;
                    if (b0 == 0xED && b1 >= 0xA0) return false; // Surrogate range 0xD800-0xDFFF
                }
                else if (b0 >= 0xF0 && b0 <= 0xF4)
                {
                    // 4-byte sequence
                    if (i + 2 >= len) return false;
                    byte b1 = bytes[i++];
                    byte b2 = bytes[i++];
                    byte b3 = bytes[i++];
                    if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) return false;
                    if (b0 == 0xF0 && b1 < 0x90) return false;
                    if (b0 == 0xF4 && b1 > 0x8F) return false; // > U+10FFFF
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        public static string NormalizeNamespace(string? ns)
        {
            if (string.IsNullOrEmpty(ns) || ns == Constants.DefaultNamespaceAlias)
            {
                return "";
            }
            int byteLen = Encoding.UTF8.GetByteCount(ns!);
            if (byteLen > Constants.MaxNsLen)
            {
                throw new InvalidNamespaceException($"Namespace byte length {byteLen} exceeds maximum allowed {Constants.MaxNsLen}");
            }
            return ns!;
        }
    }

    public enum KeyChangeOp
    {
        Put = 1,
        Delete = 2,
        DropNamespace = 3,
        Clear = 4
    }

    public sealed class KeyChangeEvent
    {
        public KeyChangeOp Op { get; }
        public string Namespace { get; }
        public string Key { get; }

        public KeyChangeEvent(KeyChangeOp op, string @namespace, string key)
        {
            Op = op;
            Namespace = @namespace;
            Key = key;
        }
    }
}
