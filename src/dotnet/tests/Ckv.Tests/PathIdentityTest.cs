using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 单例注册表键的路径标识规则（C11）。
    ///
    /// 旧实现无条件用 <c>StringComparer.OrdinalIgnoreCase</c> + <c>Path.GetFullPath</c>：
    ///  - 在 Linux / macOS 上会把 <c>/data/Foo.db</c> 与 <c>/data/foo.db</c>（两个不同的
    ///    文件）错误地合并为同一个实例；
    ///  - 不解析符号链接，<c>/data/link.db</c> 与 <c>/data/real.db</c> 会得到两个实例，
    ///    两个引擎同时写同一个文件。
    ///
    /// 注意：大小写敏感的那一半只能在 Linux / macOS 上被真正验证；在 Windows 上该分支的
    /// 断言验证的是"不敏感平台上确实合并"这一半（Windows 文件系统本身不区分大小写）。
    /// </summary>
    public class PathIdentityTest
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_pathid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void PathCaseFollowsPlatformFilesystemSemantics()
        {
            string tempDir = NewTempDir();
            try
            {
                string upper = Path.Combine(tempDir, "Case.db");
                string lower = Path.Combine(tempDir, "case.db");

                var first = CkvDb.Open(upper);
                try
                {
                    first.PutString("", "k", "v");

                    var second = CkvDb.Open(lower);
                    bool caseInsensitive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                    if (caseInsensitive)
                    {
                        // 同一个文件：注册表必须命中同一实例
                        Assert.Same(first, second);
                        Assert.Equal("v", second.GetString("", "k"));
                    }
                    else
                    {
                        // 两个不同的文件：必须互不干扰（旧实现会在这里拿到同一实例）
                        Assert.NotSame(first, second);
                        Assert.Null(second.GetString("", "k"));
                        second.PutString("", "other", "value");
                        Assert.Null(first.GetString("", "other"));
                    }

                    second.Dispose();
                }
                finally
                {
                    first.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void DotAndAbsoluteFormsShareTheSameInstance()
        {
            string tempDir = NewTempDir();
            try
            {
                string dbPath = Path.Combine(tempDir, "same.db");
                string oddForm = Path.Combine(tempDir, ".", "sub", "..", "same.db");

                var db1 = CkvDb.Open(dbPath);
                try
                {
                    var db2 = CkvDb.Open(oddForm);
                    Assert.Same(db1, db2);
                    db2.Dispose();
                }
                finally
                {
                    db1.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void SymlinkedPathResolvesToTheSameInstance()
        {
            string tempDir = NewTempDir();
            try
            {
                string realPath = Path.Combine(tempDir, "real.db");
                string linkPath = Path.Combine(tempDir, "link.db");

                // 建立真实文件与指向它的符号链接；创建链接需要权限（Windows 需管理员或
                // 开发者模式），权限不足时本用例无断言可做，直接返回。
                using (var seed = CkvDb.Open(realPath))
                {
                    seed.PutString("", "k", "v");
                }

                try
                {
                    File.CreateSymbolicLink(linkPath, realPath);
                }
                catch (Exception)
                {
                    return; // 无法创建符号链接：跳过（不是被测行为的缺陷）
                }

                var viaReal = CkvDb.Open(realPath);
                try
                {
                    var viaLink = CkvDb.Open(linkPath);
                    Assert.Same(viaReal, viaLink);
                    Assert.Equal("v", viaLink.GetString("", "k"));
                    viaLink.Dispose();
                }
                finally
                {
                    viaReal.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
