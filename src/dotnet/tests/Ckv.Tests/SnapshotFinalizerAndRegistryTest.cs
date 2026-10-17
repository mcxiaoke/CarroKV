using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Ckv.Tests
{
    /// <summary>
    /// FIX-05 / FIX-07 回归。
    ///
    /// FIX-05：<see cref="Snapshot"/> 没有终结器时，调用方忘记 Dispose 会让 _snapshotCount 永久
    /// 大于 0，此后每一次写都退化为全量克隆命名空间（静默、不可恢复的性能降级）。计数是私有
    /// 实现细节，这里用反射断言，因为可观测的外部行为（读写结果）在同一状态下完全正确。
    ///
    /// FIX-07：Dispose 必须按 (key, value) 移除注册表条目。只按键移除会在“本实例已关闭、
    /// 同路径又被新实例注册”的时序下删掉新实例的条目。
    /// </summary>
    public class SnapshotFinalizerAndRegistryTest
    {
        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ckv_snap_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static int SnapshotCount(CkvDb db)
        {
            var field = typeof(CkvDb).GetField("_snapshotCount", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return (int)field.GetValue(db)!;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LeakSnapshot(CkvDb db)
        {
            // 故意不 Dispose，也不返回引用，模拟调用方遗漏。
            var snapshot = db.CreateSnapshot();
            GC.KeepAlive(snapshot);
        }

        [Fact]
        public void UndisposedSnapshotIsReleasedByTheFinalizer()
        {
            string dir = NewDir("final");
            string path = Path.Combine(dir, "s.db");
            try
            {
                using (var db = CkvDb.Open(path))
                {
                    db.PutString("", "k", "v");
                    LeakSnapshot(db);
                    Assert.Equal(1, SnapshotCount(db));

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    Assert.Equal(0, SnapshotCount(db));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void DisposeDoesNotEvictAnotherInstancesRegistration()
        {
            string dir = NewDir("registry");
            string path = Path.Combine(dir, "r.db");
            var registry = (ConcurrentDictionary<string, WeakReference<CkvDb>>)typeof(CkvDb)
                .GetField("Registry", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            try
            {
                var db = CkvDb.Open(path);
                string canonical = (string)typeof(CkvDb)
                    .GetField("_canonicalPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(db)!;
                Assert.True(registry.ContainsKey(canonical));

                // 用一个不同的 WeakReference 实例（指向同一对象）替换自身记录：
                // 若 Dispose 只按键移除，条目会被删掉；按值移除则保留。
                var differentRef = new WeakReference<CkvDb>(db);
                typeof(CkvDb)
                    .GetField("_selfWeak", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(db, differentRef);

                db.Dispose();

                Assert.True(registry.ContainsKey(canonical),
                    "disposing an instance must not remove a registry entry it does not own");
                registry.TryRemove(canonical, out _);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
