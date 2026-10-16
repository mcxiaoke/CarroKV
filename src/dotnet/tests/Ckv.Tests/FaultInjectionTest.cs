using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 决策 D1：主库损坏时默认必须报错拒开，而不是被 `.bak` 静默替换
        /// （`.bak` 是上一次轮转之前的一代，替换即回滚此后写入的全部数据）。
        /// </summary>
        [Fact]
        public void TestBitRotIsRejectedByDefault()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "bitrot.db");
                string bakPath = Path.Combine(tempDir, "bitrot.db.bak");

                var warnings = new List<(string, string)>();
                var options = new DbOptions { OnWarning = (type, msg) => warnings.Add((type, msg)) };

                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "k1", "v1");
                }
                // 此时 db 已经存在，再写一次生成 .bak
                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "k2", "v2");
                }

                Assert.True(File.Exists(bakPath));

                // 模拟主库比特翻转单字节损坏
                byte[] corrupted = File.ReadAllBytes(dbPath);
                corrupted[14] ^= 0xFF; // 翻转 header
                File.WriteAllBytes(dbPath, corrupted);

                long primaryLen = new FileInfo(dbPath).Length;
                long bakLen = new FileInfo(bakPath).Length;

                var ex = Assert.Throws<CorruptedError>(() => CkvDb.Open(dbPath, options));
                // 错误信息必须把运维引向那份离线副本
                Assert.Contains(bakPath, ex.Message);
                Assert.DoesNotContain(warnings, w => w.Item1 == "onRecoveredFromBackup");

                // 两代文件都必须原样保留，供离线恢复使用
                Assert.Equal(primaryLen, new FileInfo(dbPath).Length);
                Assert.Equal(bakLen, new FileInfo(bakPath).Length);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 决策 D1 的显式逃生口：开启 AutoRecoverFromBackup 后恢复旧行为（含告警）。
        /// </summary>
        [Fact]
        public void TestBitRotHealsWhenExplicitlyEnabled()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault2b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "bitrot_optin.db");
                string bakPath = Path.Combine(tempDir, "bitrot_optin.db.bak");

                var warnings = new List<(string, string)>();
                var options = new DbOptions
                {
                    AutoRecoverFromBackup = true,
                    OnWarning = (type, msg) => warnings.Add((type, msg))
                };

                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "k1", "v1");
                }
                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "k2", "v2");
                }
                Assert.True(File.Exists(bakPath));

                byte[] corrupted = File.ReadAllBytes(dbPath);
                corrupted[14] ^= 0xFF;
                File.WriteAllBytes(dbPath, corrupted);

                using (var db = CkvDb.Open(dbPath, options))
                {
                    // 上一代只含 k1；损坏的那一代含 k1+k2
                    Assert.Equal("v1", db.GetString("", "k1"));
                    Assert.Null(db.GetString("", "k2"));
                }

                Assert.Contains(warnings, w => w.Item1 == "onRecoveredFromBackup");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// G1：`.bak` 作为恢复源加载时绝不能被截断——截断它会毁掉备份本身的数据。
        /// 这里特意使用 AppendOnlyLog 模式：只有该模式允许对**主日志**做残尾截断，
        /// 若恢复源继承该行为，撕裂的 `.bak` 会被悄悄削平后覆盖到主库。
        /// </summary>
        [Fact]
        public void TestRecoverySourceIsNotTruncated()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault_g1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "g1.db");
                string bakPath = dbPath + ".bak";

                var options = new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    SyncMode = SyncMode.Immediate,
                    AutoRecoverFromBackup = true
                };

                using (var db = CkvDb.Open(dbPath, options))
                {
                    db.PutString("", "version", "v1");
                    db.Compact();               // 第一代轮转进 `.bak`
                    db.PutString("", "version", "v2"); // 第二代留在日志里
                }

                Assert.True(File.Exists(bakPath), "压实必须产生 `.bak`");

                // 撕掉恢复源的尾部
                long tornLen = new FileInfo(bakPath).Length - 4;
                using (var fs = new FileStream(bakPath, FileMode.Open, FileAccess.Write))
                {
                    fs.SetLength(tornLen);
                    fs.Flush(true);
                }

                // 破坏主库，使恢复路径被走到
                byte[] corrupted = File.ReadAllBytes(dbPath);
                long primaryLen = corrupted.Length;
                corrupted[corrupted.Length - 2] ^= 0xFF;
                File.WriteAllBytes(dbPath, corrupted);

                Assert.Throws<CorruptedError>(() => CkvDb.Open(dbPath, options));

                Assert.Equal(tornLen, new FileInfo(bakPath).Length);
                Assert.Equal(primaryLen, new FileInfo(dbPath).Length);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// D1-9：只读打开损坏库必须失败且不写盘，并忽略 AutoRecoverFromBackup。
        /// </summary>
        [Fact]
        public void TestReadOnlyNeverWritesOnCorruption()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ckv_fault_ro_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dbPath = Path.Combine(tempDir, "ro.db");
                string bakPath = dbPath + ".bak";

                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "version", "v1");
                }
                using (var db = CkvDb.Open(dbPath))
                {
                    db.PutString("", "version", "v2");
                }
                Assert.True(File.Exists(bakPath));

                byte[] corrupted = File.ReadAllBytes(dbPath);
                long primaryLen = corrupted.Length;
                corrupted[14] ^= 0xFF;
                File.WriteAllBytes(dbPath, corrupted);
                long bakLen = new FileInfo(bakPath).Length;

                Assert.Throws<CorruptedError>(() =>
                    CkvDb.Open(dbPath, new DbOptions { ReadOnly = true }));
                Assert.Throws<CorruptedError>(() =>
                    CkvDb.Open(dbPath, new DbOptions { ReadOnly = true, AutoRecoverFromBackup = true }));

                Assert.Equal(primaryLen, new FileInfo(dbPath).Length);
                Assert.Equal(bakLen, new FileInfo(bakPath).Length);
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
