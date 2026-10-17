package ckv

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.channels.FileChannel
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.nio.file.StandardOpenOption

/**
 * 文件轮转原语（C5 / V10）。
 *
 * 两条不变量：
 *  1. **绝不先删除目标再 rename**（C5）：`Files.move` / `renameTo` 本身就能覆盖目标，
 *     先删除只会制造"`.db` 与 `.bak` 同时不存在"的窗口——轮转若在此窗口内失败，两代
 *     数据全部消失；
 *  2. **绝不就地截断目标再写入**（V10）：生成 `.bak` 若直接用
 *     `Files.copy(REPLACE_EXISTING)`，目标会先被截断；中途崩溃即留下半份 `.bak`，
 *     上一代完整备份同时丢失。改为"写 `.stage` → fsync → 原子替换"。
 *
 * `internal`（而非 `private`）：同模块的测试源集需要直接验证这两条不变量——让"活库
 * 轮转"失败在公开 API 上无法构造。
 */
internal object FileRotation {

    private val backoffs = longArrayOf(5, 15, 30, 60, 120)

    /** 把 [src] 覆盖到 [dst]：优先 ATOMIC_MOVE，失败后退化为 REPLACE_EXISTING（仍覆盖目标）。 */
    internal fun replace(src: File, dst: File) {
        try {
            Files.move(
                src.toPath(), dst.toPath(),
                StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE
            )
        } catch (_: Exception) {
            // ATOMIC_MOVE 并非所有平台/文件系统都支持；REPLACE_EXISTING 依旧保证覆盖目标，
            // 但（C5）绝不能先删除目标。
            Files.move(src.toPath(), dst.toPath(), StandardCopyOption.REPLACE_EXISTING)
        }
    }

    /**
     * [replace] 加上针对瞬时占用（杀毒、索引、尚未关闭的句柄）的重试阶梯；
     * 全部失败后用"暂存副本"兜底：先把 [src] 完整复制到 `dst.stage` 并校验长度，
     * 再原子替换 [dst]。若连暂存也失败，则保留暂存副本并向上抛错，而不是静默吞掉。
     */
    internal fun replaceWithRetry(src: File, dst: File) {
        for (ms in backoffs) {
            try {
                replace(src, dst)
                return
            } catch (_: Exception) {
                Thread.sleep(ms)
            }
        }

        val staged = File("${dst.path}.stage")
        src.copyTo(staged, overwrite = true)
        val srcLen = src.length()
        if (staged.length() != srcLen) {
            throw IOException(
                "staged copy of '${src.path}' is ${staged.length()} bytes instead of $srcLen"
            )
        }
        replace(staged, dst)
        if (!src.delete() && src.exists()) {
            throw IOException("failed to remove '${src.path}' after replacing '${dst.path}'")
        }
    }

    /**
     * 尽力而为地 fsync 目录，使 `rename` 产生的目录项在掉电后仍然存在（规范 §1.2 第 7 条）。
     *
     * 返回 `false` 表示该平台/文件系统拒绝目录级刷盘（macOS 对目录 fd 的 `fsync` 可能返回
     * EINVAL 或没有实际语义），**调用方不得因此让轮转失败**。Windows 直接返回 `true`：
     * NTFS 的元数据由操作系统保证，且 `FileChannel.open` 无法以目录为参数打开。
     */
    internal fun fsyncDirectory(dir: File): Boolean {
        if (isWindows) return true
        return try {
            FileChannel.open(dir.toPath(), StandardOpenOption.READ).use { it.force(true) }
            true
        } catch (_: Exception) {
            false
        }
    }

    private val isWindows: Boolean =
        System.getProperty("os.name")?.lowercase()?.startsWith("windows") == true

    /**
     * 用 [src] 重新生成 [dst]，且**不会**就地截断已有的 [dst]（V10）。
     *
     * 用于生成上一代 `.bak`：`.bak` 是人工恢复用的离线副本，一旦被半份内容覆盖，
     * 人工恢复也会失败且没有任何告警。
     */
    internal fun stageAndReplace(src: File, dst: File) {
        val staged = File("${dst.path}.stage")
        src.copyTo(staged, overwrite = true)
        FileOutputStream(staged, true).use { fos -> fos.fd.sync() }
        replaceWithRetry(staged, dst)
    }
}
