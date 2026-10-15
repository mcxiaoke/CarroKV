using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ckv
{
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
            uint pid = (uint)Process.GetCurrentProcess().Id;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    using (var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] lockData = new byte[32];
                        BinaryHelper.WriteUInt32LE(lockData.AsSpan(0, 4), pid);
                        myUuid.CopyTo(lockData, 4);
                        ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        BinaryHelper.WriteUInt64LE(lockData.AsSpan(20, 8), nowMs);
                        // Reserved 28..32 are 0
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
                            var fi = new FileInfo(lockPath);
                            var ageMs = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMilliseconds;
                            if (ageMs > options.LockTimeoutMs)
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
                                    // Another process might have moved it
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
                        throw new DatabaseLockedException(
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
                    if (currentData.Length >= 20)
                    {
                        bool match = true;
                        for (int i = 0; i < 16; i++)
                        {
                            if (currentData[4 + i] != _lockUuid[i])
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
