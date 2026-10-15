using System;

namespace Ckv
{
    public static class Crc32
    {
        private static readonly uint[] Table = GenerateTable();

        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ 0xEDB88320u;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            }
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint Compute(byte[] data)
        {
            if (data == null) return 0;
            return Compute(data.AsSpan());
        }

        public static uint Compute(byte[] data, int offset, int count)
        {
            if (data == null) return 0;
            return Compute(new ReadOnlySpan<byte>(data, offset, count));
        }
    }
}
