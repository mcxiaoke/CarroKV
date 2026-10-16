using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ckv
{
    /// <summary>
    /// 跨进程<strong>连接锁</strong>（C9）。
    ///
    /// 自 C9 起锁是**内核锁**（Windows 上以独占/共享共享模式打开句柄；.NET 在 Unix 上同样把
    /// <see cref="FileShare"/> 映射为 flock），由 <c>Open</c> 获取、持有整个连接生命周期。
    /// 这样一整类失败随之消失：
    ///
    ///   - 基于锁文件内容（PID | UUID | CreateTimeMs）的时间戳判活，会把**活着**的持有者误判为
    ///     僵死并窃取它的锁（一次长 GC、换页或大快照就足够），于是同一份库上出现两个写者；
    ///   - 崩溃的持有者会留下一个锁文件，后续进程不得不去"推理"它是否有效。
    ///
    /// 锁由操作系统持有：进程退出即释放，崩溃的写者不可能把库锁死；活着的写者也不可能被误判。
    /// 这正是 bbolt（Open 时 <c>flock(db, !readOnly)</c>，持有到连接关闭）与 LMDB（fcntl
    /// 咨询锁，随进程退出释放）的做法，两者都没有任何存活探测或窃取协议。
    ///
    /// 可写连接取排他锁（<see cref="FileShare.None"/>），只读连接取共享锁（<see cref="FileShare.Read"/>）：
    /// 读者之间互不排斥，只排斥写者。
    ///
    /// 锁文件**只创建、不删除**：删掉它会让第二个连接锁住一个新的文件句柄，而第一个仍锁着旧的，
    /// 双方都不会报错。
    /// </summary>
    internal sealed class LockFile : IDisposable
    {
        private readonly string _lockPath;
        private FileStream? _stream;
        private bool _isDisposed;

        private LockFile(string lockPath, FileStream stream)
        {
            _lockPath = lockPath;
            _stream = stream;
        }

        /// <summary>锁文件路径（诊断用）。</summary>
        public string Path => _lockPath;

        /// <summary>
        /// 打开 <c>&lt;db&gt;.lock</c> 并取得内核锁，最多等待 <paramref name="dbOptions"/> 的
        /// <c>LockTimeoutMs</c>。超时一律抛 <see cref="LockedError"/>，绝不窃取。
        /// </summary>
        public static LockFile Acquire(string dbPath, DbOptions dbOptions)
        {
            string lockPath = dbPath + ".lock";
            bool exclusive = !dbOptions.ReadOnly;
            // 只读连接只申请**读**权限：Windows 的共享检查会把"申请写权限"与
            // "只共享读的持有者"判为冲突，那会让两个读者互相排斥。
            FileAccess access = exclusive ? FileAccess.ReadWrite : FileAccess.Read;
            FileShare share = exclusive ? FileShare.None : FileShare.Read;
            uint timeoutMs = dbOptions.LockTimeoutMs;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    var stream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, access, share);
                    return new LockFile(lockPath, stream);
                }
                catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs)
                {
                    // 有人正持有该锁：退避后重试，直到超时
                    Thread.Sleep(10);
                }
                catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs)
                {
                    Thread.Sleep(10);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 超时：另一个连接正在使用这个数据库（绝不允许窃取）
                    _ = ex;
                    throw new LockedError(
                        $"Database '{dbPath}' locked: timed out waiting for the file lock '{lockPath}' after {timeoutMs}ms");
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            // 关闭句柄即释放锁；锁文件保留（见类型注释）
            _stream?.Dispose();
            _stream = null;
        }
    }
}