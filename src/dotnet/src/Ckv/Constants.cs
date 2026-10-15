using System;

namespace Ckv
{
    public static class Constants
    {
        public static readonly byte[] FileMagic = new byte[] { 0x55, 0x4B, 0x56, 0x44, 0x42, 0x30, 0x30, 0x33 }; // "UKVDB003"
        public const uint FrameMagic = 0x4D52464B; // 'KFRM' in LE
        public const ushort FormatReadVer = 3;
        public const ushort FormatWriteVer = 3;

        public const int FileHeaderLen = 32;
        public const int FrameHeaderLen = 28;
        public const int EntryFixedHeaderLen = 10;

        public const int MaxNsLen = 255;
        public const int MaxKeyLen = 65535;
        public const int MaxValLen = 16 * 1024 * 1024; // 16 MB
        public const int MaxPayloadLen = 64 * 1024 * 1024; // 64 MB
        public const int MaxOpsPerFrame = 1000000;

        public const byte OpPut = 0x01;
        public const byte OpDelete = 0x02;
        public const byte OpDropNamespace = 0x03;

        public const byte ValTypeBytes = 0x00;
        public const byte ValTypeString = 0x01;
        public const byte ValTypeInt64 = 0x02;
        public const byte ValTypeFloat64 = 0x03;
        public const byte ValTypeBool = 0x04;

        public const string DefaultNamespaceAlias = "default";
        public const ulong CanonicalQuietNaNBits = 0x7FF8000000000000UL;
    }
}
