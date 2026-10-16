using System;
using System.Collections.Generic;
using System.Text;

namespace Ckv
{
    public static class BinaryHelper
    {
        public static void WriteUInt16LE(Span<byte> span, ushort val)
        {
            span[0] = (byte)val;
            span[1] = (byte)(val >> 8);
        }

        public static ushort ReadUInt16LE(ReadOnlySpan<byte> span)
        {
            return (ushort)(span[0] | (span[1] << 8));
        }

        public static void WriteUInt32LE(Span<byte> span, uint val)
        {
            span[0] = (byte)val;
            span[1] = (byte)(val >> 8);
            span[2] = (byte)(val >> 16);
            span[3] = (byte)(val >> 24);
        }

        public static uint ReadUInt32LE(ReadOnlySpan<byte> span)
        {
            return (uint)(span[0] | (span[1] << 8) | (span[2] << 16) | (span[3] << 24));
        }

        public static void WriteUInt64LE(Span<byte> span, ulong val)
        {
            for (int i = 0; i < 8; i++)
            {
                span[i] = (byte)(val >> (8 * i));
            }
        }

        public static ulong ReadUInt64LE(ReadOnlySpan<byte> span)
        {
            ulong val = 0;
            for (int i = 0; i < 8; i++)
            {
                val |= ((ulong)span[i]) << (8 * i);
            }
            return val;
        }

        public static byte[] EncodeInt64(long val)
        {
            byte[] buf = new byte[8];
            WriteUInt64LE(buf.AsSpan(), (ulong)val);
            return buf;
        }

        public static long DecodeInt64(ReadOnlySpan<byte> buf)
        {
            if (buf.Length != 8)
                throw new CorruptedError($"Expected 8 bytes for int64, got {buf.Length}");
            return (long)ReadUInt64LE(buf);
        }

        public static byte[] EncodeFloat64(double val)
        {
            ulong bits;
            if (double.IsNaN(val))
            {
                bits = Constants.CanonicalQuietNaNBits;
            }
            else
            {
                bits = (ulong)BitConverter.DoubleToInt64Bits(val);
            }
            byte[] buf = new byte[8];
            WriteUInt64LE(buf.AsSpan(), bits);
            return buf;
        }

        public static double DecodeFloat64(ReadOnlySpan<byte> buf)
        {
            if (buf.Length != 8)
                throw new CorruptedError($"Expected 8 bytes for float64, got {buf.Length}");
            ulong bits = ReadUInt64LE(buf);
            return BitConverter.Int64BitsToDouble((long)bits);
        }

        public static byte[] EncodeBool(bool val)
        {
            return new byte[] { val ? (byte)0x01 : (byte)0x00 };
        }

        public static bool DecodeBool(ReadOnlySpan<byte> buf)
        {
            if (buf.Length == 0)
                throw new CorruptedError("Expected at least 1 byte for bool");
            return buf[0] != 0;
        }
    }

    public sealed class Utf8ByteComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly Utf8ByteComparer Instance = new Utf8ByteComparer();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.AsSpan().SequenceEqual(y.AsSpan());
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj == null) return 0;
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < obj.Length; i++)
                {
                    hash = (hash ^ obj[i]) * 16777619;
                }
                return hash;
            }
        }
    }

    /// <summary>
    /// 字符串按 UTF-8 字节无符号字典序比较（规范要求的排序序）。
    /// 替代 StringComparer.Ordinal（后者按 UTF-16 码元序，对非 BMP 字符与代理对
    /// 会产出与规范不一致的次序）。
    /// </summary>
    public sealed class Utf8StringComparer : IComparer<string>, IEqualityComparer<string>
    {
        public static readonly Utf8StringComparer Instance = new Utf8StringComparer();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            var xb = Encoding.UTF8.GetBytes(x);
            var yb = Encoding.UTF8.GetBytes(y);
            return xb.AsSpan().SequenceCompareTo(yb.AsSpan());
        }

        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj)
        {
            unchecked
            {
                int hash = (int)2166136261;
                var bytes = Encoding.UTF8.GetBytes(obj);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash = (hash ^ bytes[i]) * 16777619;
                }
                return hash;
            }
        }
    }
}
