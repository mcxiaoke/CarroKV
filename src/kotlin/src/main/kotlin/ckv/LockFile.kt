package ckv

import java.io.File
import java.io.RandomAccessFile
import java.nio.channels.FileChannel
import java.nio.channels.FileLock
import java.nio.channels.OverlappingFileLockException

/**
 * 跨进程**连接锁**（C9）：`<db>.lock` 上的内核锁，由 [CkvDb] 构造时获取、持有整个连接
 * 生命周期，`close()` 时释放。
 *
 * 与 Rust / Go / .NET / Dart 一致：不看锁文件内容、没有基于时钟的判活、没有窃取协议；
 * 崩溃的持有者靠进程退出由内核释放。锁文件只创建不删除。
 */
class LockFile private constructor(
    private val file: File,
    private val raf: RandomAccessFile,
    private val channel: FileChannel,
    private val lock: FileLock
) : AutoCloseable {

    override fun close() {
        try {
            lock.release()
        } catch (_: Exception) {}
        try {
            channel.close()
        } catch (_: Exception) {}
        try {
            raf.close()
        } catch (_: Exception) {}
    }

    companion object {
        fun acquire(dbPath: String, options: DbOptions): LockFile {
            val lockFile = File("$dbPath.lock")
            lockFile.parentFile?.mkdirs()

            val timeoutMs = options.lockTimeoutMs
            val startTime = System.currentTimeMillis()
            var delayMs = 5L

            while (true) {
                var raf: RandomAccessFile? = null
                var channel: FileChannel? = null
                var lock: FileLock? = null
                try {
                    raf = RandomAccessFile(lockFile, "rw")
                    channel = raf.channel
                    // 自 C9 起锁由 open 获取、持有整个连接生命周期；只读连接取共享锁，
                    // 可写连接取排他锁（bbolt 的 flock(db, !readOnly) 语义）：
                    // 读者之间互不排斥，只排斥写者。
                    lock = channel.tryLock(0L, Long.MAX_VALUE, options.readOnly)
                    if (lock != null) {
                        return LockFile(lockFile, raf, channel, lock)
                    }
                } catch (_: OverlappingFileLockException) {
                    // Lock held by this JVM or another thread
                } catch (_: Exception) {
                    // Could not acquire
                }

                try { lock?.release() } catch (_: Exception) {}
                try { channel?.close() } catch (_: Exception) {}
                try { raf?.close() } catch (_: Exception) {}

                if (System.currentTimeMillis() - startTime >= timeoutMs) {
                    throw LockedError("Failed to acquire file lock for '$dbPath' within ${timeoutMs}ms")
                }

                Thread.sleep(delayMs)
                delayMs = minOf(delayMs * 2, 100L)
            }
        }
    }
}
