using System;
using System.IO;

namespace Ckv.Tests
{
    /// <summary>
    /// 仓库根目录定位。
    ///
    /// 不能只拿 <c>testdata/MANIFEST.txt</c> 当锚点：测试工程会把黄金向量复制到输出目录
    /// （bin/Release/net8.0/testdata/），所以该文件在输出目录里同样存在，向上查找会在
    /// 第一层就命中，于是 interop 夹具被写进 bin 下的 temp/interop，而 Rust 那边读的是
    /// 仓库根的 temp/interop —— CI 上表现为 "interop fixture missing"。
    ///
    /// 因此锚点必须是"只可能出现在仓库根"的组合：testdata/MANIFEST.txt 且 src/rust/Cargo.toml。
    /// 任何一侧对不上就继续向上找，全部落空则直接抛错，绝不回退到一个猜出来的路径。
    /// </summary>
    internal static class RepoLayout
    {
        internal static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "testdata", "MANIFEST.txt")) &&
                    File.Exists(Path.Combine(dir.FullName, "src", "rust", "Cargo.toml")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            // 导出/归档的源码树可能没有 .git，但仍带有 src/ 与 testdata/；
            // 两者都不在时说明这不是一个完整的源码树，宁可失败也不要写错位置。
            throw new DirectoryNotFoundException(
                "repository root not found: no ancestor of " + AppContext.BaseDirectory +
                " contains both testdata/MANIFEST.txt and src/rust/Cargo.toml");
        }

        /// <summary>跨语言夹具目录：&lt;repo&gt;/temp/interop（不存在则创建）。</summary>
        internal static string InteropDir()
        {
            string interopDir = Path.Combine(Root(), "temp", "interop");
            Directory.CreateDirectory(interopDir);
            return interopDir;
        }
    }
}
