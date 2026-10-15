using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace Ckv.Tests
{
    public class GoldenVectorsTest
    {
        [Fact]
        public void TestGoldenEmptyV3Vector()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "empty.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    // Empty database with no keys written -> flushes 32-byte header
                }

                // If no write occurred yet, we can trigger empty flush by opening and closing or put/delete
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "temp", "val");
                    db.Delete("", "temp"); // total entries back to 0 -> writes empty 32-byte header
                }

                byte[] bytes = File.ReadAllBytes(dbPath);
                Assert.Equal(32, bytes.Length);

                uint headerCrc = BinaryHelper.ReadUInt32LE(bytes.AsSpan(28, 4));
                Assert.Equal(0x0C44C937u, headerCrc);

                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(bytes);
                    string hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    Assert.Equal("06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328", hashHex);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestGoldenSinglePutV3Vector()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "single_put.db");
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "hello", "world");
                }

                byte[] bytes = File.ReadAllBytes(dbPath);
                Assert.Equal(80, bytes.Length);

                // Frame header CRC at offset 32 + 4 = 36
                uint frameHdrCrc = BinaryHelper.ReadUInt32LE(bytes.AsSpan(36, 4));
                Assert.Equal(0x501DB93Eu, frameHdrCrc);

                // Payload CRC at offset 32 + 24 = 56
                uint payloadCrc = BinaryHelper.ReadUInt32LE(bytes.AsSpan(56, 4));
                Assert.Equal(0x59CBDB33u, payloadCrc);

                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(bytes);
                    string hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    Assert.Equal("f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a", hashHex);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
