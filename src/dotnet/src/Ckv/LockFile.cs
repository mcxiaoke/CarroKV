using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ckv
{
    /// <summary>
    /// 32 字节锁文件（规范 §4.1.2）：
    ///   PID (uint64_le) @ 0..8 | LockUUID (16B) @ 8..24 | CreateTimeMs (uint64_le) @ 24..32
    /// 释放锁前必须读取文件内容比对 LockUUID，完全匹配才删除，严防 ABA 误删他人锁。
    /// </summary>
    internal sealed class LockFile : IDisposable
    {
        private readonly string _lockPath;
        private readonly byte[] _lockUuid;
        private bool _isDisposed;

        private LockFile(string lockPath, byte[] lockUuid)
        {
            _lockPath = lockPath;
            _lockUuid = lockUuid;
        }

        public static LockFile Acquire(string dbPath, DbOptions options)
        {
            string lockPath = dbPath + ".lock";
            byte[] myUuid = Guid.NewGuid().ToByteArray(); // 16 bytes
            ulong pid = (ulong)Process.GetCurrentProcess().Id;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    using (var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] lockData = new byte[32];
                        BinaryHelper.WriteUInt64LE(lockData.AsSpan(0, 8), pid);
                        myUuid.CopyTo(lockData, 8);
                        ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        BinaryHelper.WriteUInt64LE(lockData.AsSpan(24, 8), nowMs);
                        // Reserved 28..32 之前无保留区；全 32B 已用满
                        fs.Write(lockData, 0, lockData.Length);
                        fs.Flush(true);
                        return new LockFile(lockPath, myUuid);
                    }
                }
                catch (IOException)
                {
                    // File already exists
                    if (File.Exists(lockPath))
                    {
                        try
                        {
                            // 与 Go/Rust 对等的僵死判定：优先读取锁内 CreateTimeMs，失败回退文件 mtime
                            ulong lockTime = 0;
                            try
                            {
                                byte[] existing = File.ReadAllBytes(lockPath);
                                if (existing.Length >= 32)
                                {
                                    lockTime = BinaryHelper.ReadUInt64LE(existing.AsSpan(24, 8));
                                }
                            }
                            catch
                            {
                                // 读取失败回退 mtime
                            }

                            ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            bool isStale;
                            if (options.ForceUnlock)
                            {
                                // 规范 §5.1.1：启动时发现遗留锁，强制清除
                                isStale = true;
                            }
                            else if (lockTime > 0 && nowMs >= lockTime)
                            {
                                isStale = (nowMs - lockTime) >= options.LockTimeoutMs;
                            }
                            else
                            {
                                var fi = new FileInfo(lockPath);
                                double ageMs = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMilliseconds;
                                isStale = ageMs >= options.LockTimeoutMs;
                            }

                            if (isStale)
                            {
                                // Stale lock detected, rename away
                                string stealPath = lockPath + ".steal." + Guid.NewGuid().ToString("N");
                                try
                                {
                                    File.Move(lockPath, stealPath);
                                    try { File.Delete(stealPath); } catch { }
                                    continue;
                                }
                                catch
                                {
                                    // Windows WinError 32/33/5：持锁者存活，禁止窃取，继续等待
                                }
                            }
                        }
                        catch
                        {
                            // Inaccessible or deleted in race
                        }
                    }

                    if (sw.ElapsedMilliseconds >= options.LockTimeoutMs)
                    {
                        throw new LockedError(
                            $"Database '{dbPath}' locked: timed out waiting for lock file '{lockPath}' after {options.LockTimeoutMs}ms"
                        );
                    }

                    Thread.Sleep(20);
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (File.Exists(_lockPath))
                {
                    byte[] currentData = File.ReadAllBytes(_lockPath);
                    if (currentData.Length >= 24)
                    {
                        // 防 ABA：仅当锁文件仍是我们自己写入的（UUID 完全匹配）才允许删除
                        bool match = true;
                        for (int i = 0; i < 16; i++)
                        {
                            if (currentData[8 + i] != _lockUuid[i])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                        {
                            File.Delete(_lockPath);
                        }
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors on disposal
            }
        }
    }
}
