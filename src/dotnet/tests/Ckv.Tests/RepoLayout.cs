using System;
using System.IO;

namespace Ckv.Tests
{
    /// <summary>
    /// 跨语言互操作夹具目录定位。
    ///
    /// 优先从环境变量 <c>CKV_INTEROP_DIR</c> 读取绝对路径（CI 与外部脚本驱动）；
    /// 未设置时降级到系统临时目录下的 <c>ckv_interop</c>，
    /// 绝不依赖特定 monorepo 目录结构、相对路径层级或探测其它语言项目文件（如 Cargo.toml）。
    /// </summary>
    internal static class RepoLayout
    {
        /// <summary>跨语言夹具目录（不存在则创建）。</summary>
        internal static string InteropDir()
        {
            string? env = Environment.GetEnvironmentVariable("CKV_INTEROP_DIR");
            string dir = !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Path.GetTempPath(), "ckv_interop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
