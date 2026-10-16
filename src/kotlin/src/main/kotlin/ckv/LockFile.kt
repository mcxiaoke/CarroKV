package ckv

import java.io.File
import java.io.RandomAccessFile
import java.nio.channels.FileChannel
import java.nio.channels.FileLock
import java.nio.channels.OverlappingFileLockException

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

            val timeoutMs = options.fileLockTimeoutMs
            val startTime = System.currentTimeMillis()
            var delayMs = 5L

            while (true) {
                var raf: RandomAccessFile? = null
                var channel: FileChannel? = null
                var lock: FileLock? = null
                try {
                    raf = RandomAccessFile(lockFile, "rw")
                    channel = raf.channel
                    lock = channel.tryLock()
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
