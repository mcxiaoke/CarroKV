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
    ///
    /// <para><b>本测试类只断言"路径身份"，绝不触碰 <c>Dispose</c> 语义。</b>
    /// 旧版本在 <c>DotAndAbsoluteFormsShareTheSameInstance</c> 里先 <c>db2.Dispose()</c>
    /// 再 <c>db1.Dispose()</c> 且不报错——那等于把"Dispose 任意一个即干掉全部共享实例"
    /// 断言成了正常行为，正是 P0-4（真实线上崩溃根因）被测试反向固化的原因：
    /// 当一个 API 的所有权模型没有写进规范时，测试作者只能按 .NET 默认心智模型
    /// （<c>IDisposable</c> = 物理销毁）来写，于是测试"通过"了，错误却被放大。
    /// 单例的生命周期由 <see cref="SingletonOwnershipTest"/> 单独覆盖。</para>
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
                var second = CkvDb.Open(lower);
                bool caseInsensitive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                if (caseInsensitive)
                {
                    // 同一个文件：注册表必须命中同一底层引擎（ADR-20261017 句柄包装模型）
                    Assert.Same(first.Root, second.Root);
                    first.PutString("", "k", "v");
                    Assert.Equal("v", second.GetString("", "k"));
                    second.Dispose();
                    Assert.Equal("v", first.GetString("", "k"));
                    first.Dispose();
                }
                else
                {
                    // 两个不同的文件：必须互不干扰（旧实现会在这里拿到同一实例）
                    Assert.NotSame(first, second);
                    Assert.Null(second.GetString("", "k"));
                    first.PutString("", "k", "v");
                    second.PutString("", "other", "value");
                    Assert.Equal("v", first.GetString("", "k"));
                    Assert.Equal("value", second.GetString("", "other"));
                    // 两个不同实例各自独立 Dispose，互不影响
                    second.Dispose();
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
                var db2 = CkvDb.Open(oddForm);

                // 断言路径身份：命中同一底层引擎（ADR-20261017 句柄包装模型）。
                Assert.Same(db1.Root, db2.Root);
                db1.PutString("", "k", "v");
                Assert.Equal("v", db2.GetString("", "k"));

                db2.Dispose();
                Assert.Equal("v", db1.GetString("", "k"));
                db1.Dispose();
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
                var seed = CkvDb.Open(realPath);
                seed.PutString("", "k", "v");
                seed.Dispose();

                try
                {
                    File.CreateSymbolicLink(linkPath, realPath);
                }
                catch (Exception)
                {
                    return; // 无法创建符号链接：跳过（不是被测行为的缺陷）
                }

                var viaReal = CkvDb.Open(realPath);
                var viaLink = CkvDb.Open(linkPath);

                // 断言路径身份：符号链接解析到同一注册表键 → 同一底层引擎（ADR-20261017 句柄包装模型）。
                Assert.Same(viaReal.Root, viaLink.Root);
                Assert.Equal("v", viaLink.GetString("", "k"));

                viaLink.Dispose();
                Assert.Equal("v", viaReal.GetString("", "k"));
                viaReal.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
