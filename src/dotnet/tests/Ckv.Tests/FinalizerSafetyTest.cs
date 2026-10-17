using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// 条目 14（P1-18）：终结器安全网与泄露告警测试。
    /// 验证调用方遗漏 Dispose 时，GC 终结器能安全递减引用计数、触发泄露告警并释放底层排他锁。
    /// </summary>
    public class FinalizerSafetyTest
    {
        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_fin_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndLeakHandle(string path, List<string> warnings)
        {
            var opts = new DbOptions
            {
                OnWarning = (code, msg) =>
                {
                    lock (warnings) warnings.Add(code + ":" + msg);
                }
            };
            var db = CkvDb.Open(path, opts);
            db.PutString("", "k", "v");
            // 刻意不 Dispose，离开方法作用域使 db 失去强引用
            GC.KeepAlive(db);
        }

        [Fact]
        public void Finalizer_InvokesWarningAndReleasesUnderlyingResources()
        {
            string dir = NewDir("leak");
            string path = Path.Combine(dir, "leak.db");
            var warnings = new List<string>();

            try
            {
                CreateAndLeakHandle(path, warnings);

                // 触发 GC 并等待终结器执行
                for (int i = 0; i < 10; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    lock (warnings)
                    {
                        if (warnings.Exists(w => w.StartsWith("onFinalizerLeak")))
                        {
                            break;
                        }
                    }
                    Thread.Sleep(50);
                }

                // 验证告警被触发
                lock (warnings)
                {
                    Assert.Contains(warnings, w => w.StartsWith("onFinalizerLeak"));
                }

                // 验证底层连接锁与资源已释放，后续能正常独占 Open 并读取已提交数据
                using (var reopened = CkvDb.Open(path, new DbOptions { LockTimeoutMs = 1000 }))
                {
                    Assert.Equal("v", reopened.GetString("", "k"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
