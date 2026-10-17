using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Sdk;

namespace Ckv.Tests
{
    /// <summary>
    /// Fuzz-lite：对真实数据库文件做确定性变异。
    ///
    /// 不是覆盖率引导的 fuzzer。固定、可复现的变异集覆盖黄金向量 + 一个生成的 AOL 文件，
    /// 断言两件事：
    ///   1. 不 panic、不挂死——被变异过的文件要么被干净拒绝，要么能完整读出；
    ///   2. 结构性损坏必须被检测，不能被当成空库静默接受。
    ///
    /// 与 Rust / Go / Kotlin / Dart 的同源套件一致。
    /// </summary>
    public class FuzzLiteTest
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? Path.GetFullPath("../../../../../..");
        }

        private static List<(string Name, byte[] Bytes)> Seeds()
        {
            var seeds = new List<(string, byte[])>();
            string testdata = Path.Combine(RepoRoot(), "testdata");
            foreach (var name in new[]
                     {
                         "empty_v3.db", "single_put_v3.db", "multi_types_v3.db", "unicode_order_v3.db"
                     })
            {
                string p = Path.Combine(testdata, name);
                if (File.Exists(p)) seeds.Add((name, File.ReadAllBytes(p)));
            }

            string dir = Path.Combine(Path.GetTempPath(), "ckv_fuzz_seed_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "generated.db");
            try
            {
                var opts = new DbOptions
                {
                    EngineMode = StorageEngineMode.AppendOnlyLog,
                    SyncMode = SyncMode.Manual
                };
                using (var db = CkvDb.Open(path, opts))
                {
                    db.PutString("ns", "alpha", "1");
                    db.PutString("ns", "beta", "22");
                    db.PutInt64("ns2", "gamma", long.MinValue);
                }
                seeds.Add(("generated_aol", File.ReadAllBytes(path)));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return seeds;
        }

        private static bool Accepted(Exception ex) =>
            ex is CorruptedDatabaseException
            || ex is UnsupportedVersionException
            || ex is InvalidUtf8Exception
            || ex is InvalidNamespaceException
            || ex is KeyTooLargeError
            || ex is IOException;

        /// <summary>返回 "opened" / "rejected"；非预期异常直接抛出。</summary>
        private static string Exercise(string dir, int tag, byte[] data)
        {
            string path = Path.Combine(dir, $"case_{tag}.db");
            File.WriteAllBytes(path, data);

            CkvDb? db = null;
            try
            {
                db = CkvDb.Open(path);
                foreach (var ns in db.Namespaces())
                {
                    db.Count(ns);
                    foreach (var key in db.Keys(ns))
                    {
                        db.GetBytes(ns, key);
                    }
                    db.ScanRange(ns);
                }
                db.Stats();
                return "opened";
            }
            catch (Exception ex) when (Accepted(ex))
            {
                return "rejected";
            }
            finally
            {
                db?.Dispose();
            }
        }

        private static bool OpensSuccessfully(string path)
        {
            try
            {
                using var db = CkvDb.Open(path);
                return true;
            }
            catch (Exception ex) when (Accepted(ex))
            {
                return false;
            }
        }

        [Fact]
        public void MutatedFilesNeverPanic()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_fuzz_cases_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            int cases = 0, rejected = 0, tag = 0;

            void Check(string seed, string what, byte[] data)
            {
                tag++;
                cases++;
                try
                {
                    if (Exercise(dir, tag, data) == "rejected") rejected++;
                }
                catch (Exception ex)
                {
                    throw new XunitException($"{seed} {what}: unexpected {ex.GetType().Name}: {ex.Message}");
                }
            }

            try
            {
                foreach (var (name, original) in Seeds())
                {
                    int n = original.Length;

                    var cuts = new List<int> { 0, 1, 15, 31, 32, 33 };
                    if (n > 1) cuts.Add(n - 1);
                    foreach (int cut in cuts)
                    {
                        if (cut > n) continue;
                        Check(name, $"truncated to {cut} bytes", original[..cut]);
                    }

                    for (int offset = 0; offset < n; offset++)
                    {
                        foreach (byte mask in new byte[] { 0xFF, 0x01, 0x80 })
                        {
                            var mutated = (byte[])original.Clone();
                            mutated[offset] ^= mask;
                            Check(name, $"flip at {offset} mask {mask:X2}", mutated);
                        }
                    }

                    foreach (var extra in new[] { new byte[] { 0x00 }, new byte[] { 0xFF }, new byte[] { 0x00, 0x01, 0x02, 0x03 } })
                    {
                        var mutated = new byte[original.Length + extra.Length];
                        Buffer.BlockCopy(original, 0, mutated, 0, original.Length);
                        Buffer.BlockCopy(extra, 0, mutated, original.Length, extra.Length);
                        Check(name, $"append {extra.Length} bytes", mutated);
                    }

                    // Structural damage must be detected, not accepted as an empty db.
                    tag++;
                    cases++;
                    string shortPath = Path.Combine(dir, $"case_{tag}.db");
                    File.WriteAllBytes(shortPath, original[..Math.Min(31, n)]);
                    Assert.False(OpensSuccessfully(shortPath),
                        $"{name} truncated below the file header was accepted");

                    if (n > 0)
                    {
                        var magic = (byte[])original.Clone();
                        magic[0] ^= 0xFF;
                        tag++;
                        cases++;
                        string magicPath = Path.Combine(dir, $"case_{tag}.db");
                        File.WriteAllBytes(magicPath, magic);
                        Assert.False(OpensSuccessfully(magicPath),
                            $"{name} with corrupted magic was accepted");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Assert.True(cases > 1000, $"mutation set unexpectedly small: {cases}");
            Assert.True(rejected > cases / 10,
                $"only {rejected} of {cases} mutations were rejected - corruption detection looks broken");
        }
    }
}
