using System;
using System.IO;
using Xunit;

namespace Ckv.Tests
{
    public class FaultInjectionTest
    {
        [Fact]
        public void TestCrashRecoveryPromoteTmp()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "crash.db");
                string tmpPath = Path.Combine(tempDir, "crash.db.tmp");

                // 创建一个有效的数据库并重命名为 .tmp，模拟写入完毕准备 rename 前进程崩溃
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "saved", "precious_data");
                }
                File.Move(dbPath, tmpPath);
                Assert.False(File.Exists(dbPath));
                Assert.True(File.Exists(tmpPath));

                // 重新打开：引擎应自动检测到有效 .tmp 并自愈晋升为 .db
                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.Equal("precious_data", db.GetString("", "saved"));
                }

                Assert.True(File.Exists(dbPath));
                Assert.False(File.Exists(tmpPath));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestBitRotHealsFromBackup()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "bitrot.db");
                string bakPath = Path.Combine(tempDir, "bitrot.db.bak");

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k1", "v1");
                }
                // 此时 db 已经存在，再写一次生成 .bak
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k2", "v2");
                }

                Assert.True(File.Exists(bakPath));

                // 模拟主库比特翻转单字节损坏
                byte[] corrupted = File.ReadAllBytes(dbPath);
                corrupted[14] ^= 0xFF; // 翻转 header
                File.WriteAllBytes(dbPath, corrupted);

                // 打开时应自动从 .bak 恢复
                using (var db = CkvDb.Open(dbPath))
                {
                    Assert.Equal("v1", db.GetString("", "k1"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestDualCorruptionAbortsWithoutWiping()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault3_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "dual_corrupt.db");
                string bakPath = Path.Combine(tempDir, "dual_corrupt.db.bak");

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "k", "v");
                }
                File.Copy(dbPath, bakPath);

                // 两者均人为破坏
                File.WriteAllBytes(dbPath, new byte[] { 0x01, 0x02, 0x03 });
                File.WriteAllBytes(bakPath, new byte[] { 0x04, 0x05, 0x06 });

                // 打开必须抛出 CorruptedError，严禁直接清空文件覆盖
                Assert.Throws<CorruptedError>(() =>
                {
                    using var db = CkvDb.Open(dbPath);
                });

                // 文件依然保留现场
                Assert.Equal(3, File.ReadAllBytes(dbPath).Length);
                Assert.Equal(3, File.ReadAllBytes(bakPath).Length);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TestReentrantOpenSharesInstance()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault4_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "reentrant.db");

                var db1 = CkvDb.Open(dbPath);
                // 使用不同形式的相对路径 / 绝对路径打开同一文件
                string relativePath = Path.Combine(tempDir, ".", "reentrant.db");
                var db2 = CkvDb.Open(relativePath);

                Assert.Same(db1, db2);

                db1.PutString("", "shared", "alive");
                Assert.Equal("alive", db2.GetString("", "shared"));

                db1.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
