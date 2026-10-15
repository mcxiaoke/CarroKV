# CKV 阶段二设计规范：可插拔增量追加写（Append-Only WAL）与紧凑化备份架构

- **状态**：Draft & Approved for Implementation
- **设计目标**：在不牺牲现有阶段一极简稳健性的前提下，以**可选参数（Pluggable Engine Mode）**方式引入高吞吐、微秒级延迟、低闪存磨损的增量追加写模式（Append-Only Log），并沿用 **`.bak` 文件级轮转备份机制**，守住“断电绝不导致整库 Broken”的核心可靠性底线。

---

## 1. 核心设计原则与架构边界

1. **可插拔性与平滑兼容（Zero Breaking Change）**：
   - 默认存储引擎模式保持为经典的 **阶段一：整库快照原子轮转 (`SnapshotAtomic`)**，为 90% 的普通 App（配置表、状态机、数据量 < 10MB）守住极简的物理隔离安全底线；
   - 在 `DbOptions` 中提供 `EngineMode = StorageEngineMode.AppendOnlyLog`，按需为高频写入、实时事件、低端闪存设备解锁阶段二；
   - 两种模式遵循统一的《Binary Format Specification v3.2》帧格式（`32B FileHeader + N 个 BatchFrame`），文件可互相打开与无缝转换。
2. **断电可靠性红线（Crash-Safe Guarantee）**：
   - 允许丢失最后一次未完成落盘的在途写入（此为不可抗物理定律）；
   - **绝不允许断电导致整库 broken 或历史存量数据全丢**；
   - 采用严格的**顺序追加写入（Strictly Append-Only）**，历史已提交帧永不原地修改；
   - 发生断电时，若文件尾部产生半写残缺坏帧，启动时自动执行**尾部截断自愈（Tail Truncation）**，前面所有已落盘数据 100% 完好；
3. **选定良机维护 `.bak` 文件级基准备份（Safe `.bak` Rotation）**：
   - 采纳关键指导思想：文件级的 `.bak` 备份与文件内部的追加日志无关，是防御物理坏道和整库崩溃的最强兜底屏障；
   - **最佳备份时机**：在**执行 Compaction 紧凑化替换的瞬间**完成轮转备份。
   - 此时磁盘上的 `.bak` 永远保持为**最近一次成功压实、全量 CRC32 强校验、零碎片、最健康的紧凑基准快照**。万一后续追加的 `.db` 遭遇物理介质故障，引擎永远能从 `.bak` 退回至上一个健康基准点。

---

## 2. 物理二进制帧格式（对齐规范 v3.2）

无论是整库快照（阶段一）还是增量追加日志（阶段二），物理磁盘上的布局完全一致：

```
+---------------------------------------------------------------------------------------------------+
| Immutable File Header (Fixed 32 Bytes, FileMagic = "UKVDB003", HeaderCRC32 = 0x0C44C937)          |
+---------------------------------------------------------------------------------------------------+
| BatchFrame 0 (Initial Snapshot or First Append, 32B FrameHeader + Payload + PayloadCRC32)         |
+---------------------------------------------------------------------------------------------------+
| BatchFrame 1 (Incremental Delta: Put / Delete / Increment, 32B FrameHeader + Payload + CRC32)     |
+---------------------------------------------------------------------------------------------------+
| BatchFrame 2 (Incremental Delta...)                                                               |
+---------------------------------------------------------------------------------------------------+
| ...                                                                                               |
+---------------------------------------------------------------------------------------------------+
```

### 2.1 增量 Frame 载荷编码规则

增量写只编码当前操作修改的条目（Delta），大幅减少 I/O：
- **单键 Put / 批量 Put**：仅编码本次写入的键值；
- **Delete**：编码为 `ValueType = 0xFF`（Tombstone 墓碑帧）；
- **DropNamespace**：编码为 `ValueType = 0xFE`；
- **ClearNamespace**：编码为 `ValueType = 0xFD`；
- 每次追加为一个独立的 `BatchFrame`，拥有单调递增的 `TxId`、`Timestamp` 和该帧的 `PayloadCRC32`。

---

## 3. 核心机制设计

### 3.1 启动与崩溃恢复状态机 (Reopen & Tail Crash Recovery)

当以 `AppendOnlyLog` 模式打开 `.db` 文件时，执行顺序扫描重放：

```
Offset 32: 读取 Frame 0  ──► CRC 校验成功 ──► 重放到内存
Offset X:  读取 Frame 1  ──► CRC 校验成功 ──► 重放到内存 (覆盖旧值或应用墓碑)
...
Offset N:  遇到文件末尾坏帧 (断电截断/半写)
               │
               ├── 判定条件: 当前偏移 + 32 > FileLength 或 Payload CRC 失败，且已到达文件末尾
               │
               ▼
   【触发自动尾部截断 (Tail Truncation)】
   FileStream.SetLength(Offset N) 丢弃残缺半帧！
   历史已落盘的所有 Frame 全部有效保留，数据库秒级正常启动！
```

- **中间坏帧防御**：若不是文件末尾坏帧，而是中间帧损坏（如硬件位翻转），判定为物理损坏，触发 `.bak` 备份恢复流程。

---

### 3.2 增量写入流程 (Append Write Path)

1. **写路径**：
   - 保持打开的追加写句柄（`FileAccess.Write, FileShare.Read`）；
   - 在写锁保护下：
     1. 将本次操作（单写或 Batch）序列化为 1 个小巧的 `BatchFrame` 内存字节包（通常仅几十至几百字节）；
     2. 追加写入文件末尾（`Write`）；
     3. 若 `SyncOnWrite == true`，执行 `Flush(flushToDisk: true)`（单写落盘延时从 350ms 骤降至 <1ms）；
     4. 更新内存 CoW 树状态与最大 `TxId`；
     5. 统计已追加字节数与被覆写的垃圾数据估计值（`_deadBytes`）；
     6. 若满足 Compaction 触发条件，触发协作式压实。

---

### 3.3 Compaction 压实与 `.bak` 轮转备份机制

随着频繁覆写与删除，追加日志中会积累失效的历史帧（垃圾数据）。必须低频进行 Compaction 将数据重新收敛为 1 个干净的紧凑帧。

#### A. 触发时机
- **自动触发**：文件大小超过阈值（如 `4MB`）且垃圾数据占比超过阈值（如 `DeadRatio >= 30%`）；
- **显式触发**：应用层主动调用 `db.Compact()`。

#### B. 压实执行步骤（短持锁 + 安全轮转）
1. **获取只读视图**：利用现有的**无锁只读快照机制（Snapshot Isolation）**，获取当前全库数据的干净只读视图。此步骤完全不阻塞并发读取；
2. **写入全新紧凑文件**：将当前全量数据序列化为单个标准的全新紧凑快照文件，写入 `.compact.tmp` 并物理 `fsync`；
3. **关键切换窗口（毫秒级持有写锁）**：
   - 关闭当前的追加写句柄；
   - **轮转旧库为 `.bak`**：调用 `File.Copy(dbPath, bakPath, true)` 或重命名。
     > **至此，磁盘上的 `.bak` 刷新为当前时刻经过全量 CRC 校验的完美基准！**
   - **原子替换**：将 `.compact.tmp` 重命名覆盖 `.db`；
   - 重新以追加模式打开 `.db`，重置追加与垃圾统计计数器；
4. **释放写锁**。

#### C. Compaction 期间断电自愈矩阵
- **断电在写 `.compact.tmp` 期间**：`.db` 毫发无损，下次启动自动删除废弃的 `.compact.tmp`；
- **断电在原子替换瞬间**：`.bak` 已经生成，即使 `.db` 暂时缺失，也能根据 `.compact.tmp` 晋升或从 `.bak` 恢复。

---

## 4. API 设计与配置选项

### 4.1 `DbOptions` 扩展

```csharp
public enum StorageEngineMode
{
    /// <summary>
    /// 阶段一（默认）：整库快照原子轮转 (SnapshotAtomic)。
    /// 适用：数据量 < 10MB、读多写少、配置表、追求极致简单与物理隔离。
    /// </summary>
    SnapshotAtomic = 1,

    /// <summary>
    /// 阶段二：增量追加写日志 (AppendOnlyLog)。
    /// 适用：数据量 > 10MB、高频单条写、低端闪存防擦穿磨损。
    /// </summary>
    AppendOnlyLog = 2
}

public class DbOptions
{
    // 默认保持阶段一
    public StorageEngineMode EngineMode { get; set; } = StorageEngineMode.SnapshotAtomic;

    // 触发自动压实的最小文件增量大小（默认 4MB）
    public long CompactionThresholdBytes { get; set; } = 4 * 1024 * 1024;

    // 触发自动压实的垃圾数据占比（默认 30%）
    public double CompactionDeadRatio { get; set; } = 0.30;
}
```

### 4.2 核心方法扩展
- `public void Compact()`: 显式触发一次日志压实，收敛文件碎片并刷新 `.bak` 基准备份。
- `public long GetFileSize()`: 获取当前数据库文件物理大小。
