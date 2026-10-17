# 跨平台多语言纯原生 Key-Value 数据库架构设计与规范蓝图（v3.2 实施定稿终版）

## 1. 设计定位、适用边界与 v3.2 修订说明

本项目旨在设计并实现一款面向桌面端（Windows、Linux、macOS）与移动端（Flutter/Dart、.NET MAUI 等）App 的高可靠、轻量级嵌入式 Key-Value（KV）数据库工具库，并面向开源社区发布。

### 1.1 核心工程前提与边界声明

1. **纯原生多语言实现（无 FFI/JNI）**：
   在 **.NET (C#)、Rust、Go、Dart、Node.js、Python**（后续可扩展 Kotlin/Swift）中采用各语言标准库 100% 纯原生实现，不依赖 `mmap`，底层完全建立在标准文件流操作（`seek`/`pread` + `read`/`write` + `fsync`）之上。
2. **跨平台字节级兼容与文件共享边界**：
   各语言生成的 `.db` 文件在字节级 100% 互通。适用场景包括：**跨语言/跨平台导入导出备份、宿主升级迁移、以及非并发的跨工具读写（如 GUI 关闭后由 CLI 工具读取或修改配置）**。
   * **明确不支持的场景**：**不支持多进程（如 CLI 与 GUI）同时并发读写同一个数据库文件**，也不提供多设备文件级双向同步的冲突合并（多端文件同步仅限整文件覆盖）。
3. **关键设计与演进纪律**：
   * 采用“写操作短持锁（Snapshot 模式）”或“会话独占锁 + 基于时间窗口与原子重命名的僵死锁回收协议”。
   * 采用“两代文件覆盖原子轮转”消除写放大，配合常驻 `.bak` 备份消除文件缺失窗口。
   * 6 种语言统一内联 IEEE 802.3 `CRC32` 查表法，实现跨语言真正的零外部依赖。

### 1.2 v3.2 实施定稿关键裁决说明（彻底消除 6 语言分歧）

针对深度架构审计反馈，v3.2 完成了以下 7 项 P0 阻塞性决策与 P1 次要缺口的硬性锁定：

1. **§3.4 阶段隔离一刀切（解决实现模型矛盾）**：
   * **阶段一（v1.0 快照模式）**：正式文件通过 `.tmp` + 原子轮转生成，磁盘永无追加残尾。因此**加载器仅做严格的顺序帧加载校验**；遇任何校验失败直接判定为介质损坏并走 §4.2 的 `.bak` 自愈回退，**完全不实现也不执行向后重同步扫描与尾部截断**。
   * **阶段二（v1.1 追加写模式）**：才引入向后 `FrameMagic` 重同步与写者截断逻辑。
2. **TxID 单调性与空库保护闭环**：
   * 存活条目为 0 时磁盘落盘为 32B 文件头（$N=0$）。
   * 实例内存中**持久维护 `currentMaxTxID`，即使所有条目清空也严禁归零**；新帧分配规则写死为：`newTxID = max(memoryKnownMaxTxID, diskScannedMaxTxID) + 1`；冷启动空库（32B 且无历史帧）时初始为 0，首帧取 1。
3. **锁文件二进制格式与防 ABA 释放协议**：
   * 锁文件固定为 32 字节结构体：`PID (uint64_le) + LockUUID (16B) + CreateTimeMs (uint64_le)`。
   * 释放锁时**必须先读取文件内容比对 `LockUUID`**：完全匹配才执行删除；若已被窃取则严禁删除他人锁文件。
   * Windows 实测约束锁定：`rename` 遇到 `WinError 32/33/5` 时必须立即判定持锁者存活并禁止窃取。
4. **`DbOptions` 规范与标准化错误体系表**：
   * 完整定义 `DbOptions` 字段（`readOnly`, `lockTimeoutMs`, `forceUnlock`, `onWarning`）；
   * 固化 6 语言同名同义的标准错误体系（基类 `DbError` 及 6 个具体派生错误）。
5. **`batch()` 纯内存 Staging 机制**：
   * 阶段一 `batch(fn)` **不产生独立的物理增量帧**；
   * `fn` 执行期间所有写入暂存于内存 Staging 区，抛错则丢弃暂存；成功后一次性合并并触发全量快照落盘（仅写出存活 `OpPut`）；在 Node.js / Dart 中禁止 `fn` 内异步挂起（`await`）。
6. **多帧装箱确定性伪代码与 TxID 澄清**：
   * 给出严格无歧义的贪心切分条件：`(currentFrameOpCount >= 1_000_000) || (currentFramePayloadLen + entrySize > 67_108_864)`；
   * 澄清快照多帧切片属于同一快照版本的物理分片，连续递增 `TxID` 仅用于维持物理帧单调约束，重放时全量原子生效。
7. **目录 `fsync` 尽力而为与 Windows 退避失败保护**：
   * 目录 `fsync` 明确为 Best-effort，不支持平台安全跳过；**任何实现都不得因此让轮转失败**，失败只经 `onWarning("onDirSyncUnavailable")` 上报一次；
   * 平台能力矩阵（2026-10-17 实测）：Rust `File::open(dir).sync_all()` 与 Go `os.Open(dir).Sync()` 在 Unix 可用；Kotlin `FileChannel.open(dir, READ).force(true)` 在 Linux 可用、macOS 可能返回 EINVAL（best-effort）；**.NET 8 在 Linux 上 `File.OpenHandle(dir)` 实测抛 `UnauthorizedAccessException`，全托管路径当前不可用**，故降级为 no-op + 告警（不改用 P/Invoke）；Dart / Node.js 无目录句柄 API，为 no-op；
   * Windows 重试耗尽失败时抛出标准 I/O 错误，**且必须保留 `.tmp` 文件**，以供下次打开时自愈晋升。
8. **保留字与边界校验**：
   * API 强制校验 `NsLen <= 255`（超长抛 `InvalidNamespaceError`）；
   * 明确 `"default"` 是系统保留别名，与 `""` 等价映射至 `NsLen = 0`，调用方无法创建物理名为 `"default"` 的隔离命名空间。

---

## 2. 存储架构演进路线：整库快照 vs. 精简追加写日志

二进制格式定义为 **“1 个不可变文件头（32 Bytes） + $N$ 个自包含的原子批次帧（$N \ge 0$）”**：

| 评估维度 | 阶段一（v1.0 MVP）：整库快照 + 原子轮转 | 阶段二（v1.1 按需演进）：精简追加写日志 |
| :--- | :--- | :--- |
| **核心写路径** | 内存驻留全量数据；每次写入将全库序列化至 `.tmp` -> `fsync` -> 原子轮转旧库至 `.bak` -> 原子覆盖 `rename` 至 `.db`。 | 内存驻留索引（或全量数据）；每次写入向 `.db` 末尾追加 1 个 `BatchFrame` 并立即 `fsync`，低频触发 `compact()`。 |
| **物理帧数量 ($N$)** | 空库时 $N = 0$；通常 $N = 1$（若单库总载荷超过 64MB，则自动切分为连续的多个 64MB `BatchFrame`）。 | $N \ge 0$（随每次写入递增，`compact()` 后重新收敛回紧凑快照）。 |
| **崩溃恢复算法** | **极简自愈**：顺序加载多帧；遇坏块直接判定损坏并尝试 `.bak` 回退，**不扫描不截断**。 | **向后扫描重同步**：执行 `FrameMagic` 扫描，确诊中间坏块拒开，尾部半写截断备份。 |
| **句柄持有策略** | **平时不持有 `.db` 句柄**：加载完即关闭，写入时仅在毫秒级窗口内操作文件，天然免疫 Windows 句柄占用。 | **写者常驻持有 `.db` 句柄**：仅在 `compact()` 替换瞬间短暂关闭并重开句柄。 |
| **适用数据量与频率** | **< 1MB – 2MB**、配置、账号凭证、轻量本地状态（参考 [Bitcask](https://riak.com/assets/bitcask-intro.pdf) 紧凑态）。 | **1MB – 100MB**、高频落盘缓存、离线事件日志（参考 [Hive CE](https://github.com/IO-Design-Team/hive_ce) 与 [rosedb](https://github.com/rosedblabs/rosedb)）。 |

---

## 3. 《Binary Format Specification v3.2》字节级规范

所有多字节整数与浮点数强制采用 **Little-Endian（小端序）**；所有字符串强制采用无 BOM 的 **UTF-8** 编码。

### 3.1 不可变文件头（Immutable File Header - Fixed 32 Bytes）

文件头在创建文件时一次性写入，**创建后永不原地修改**。

| 字节偏移 (Offset) | 长度 (Bytes) | 字段名称 | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- | :--- |
| `0..8` | 8 | `FileMagic` | `byte[8]` | 固定 ASCII 魔数：`0x55 0x4B 0x56 0x44 0x42 0x30 0x30 0x33`（`"UKVDB003"`） |
| `8..10` | 2 | `FormatReadVer` | `uint16` | 允许读取此文件的最低格式版本（v3.2 固定写 `3`；**读取端若 `FormatReadVer > 3` 必须拒绝打开并抛出 `UnsupportedVersionError`**） |
| `10..12` | 2 | `FormatWriteVer` | `uint16` | 允许写入此文件的最低格式版本（v3.2 固定写 `3`） |
| `12..16` | 4 | `HeaderFlags` | `uint32` | 预留全局标志位（v3.2 固定填 `0`；若读取端遇到未知非零必选位则拒绝打开） |
| `16..28` | 12 | `Reserved` | `byte[12]` | 预留扩展字节，写入时必须全部填充 `0x00` |
| `28..32` | 4 | `HeaderCRC32` | `uint32` | 覆盖偏移量 `0..28` 共 28 字节的 IEEE `CRC32` 校验值（空头固定为 `0x0C44C937`） |

* **空数据库文件形态（Empty DB State）**：
  新初始化的空数据库或所有键被清空/删除后的数据库文件**大小恰好为 32 字节（即仅包含上述文件头，$N=0$ 个帧）**。
  读取器加载规则：当文件通过前 32 字节校验且 `fileSize == 32` 时，判定为合法空数据库，停止扫描并成功返回，内存状态为空，`currentMaxTxID = 0`。

### 3.2 复合批次帧（Batch Frame - 1 Batch = 1 Frame）

从文件偏移量 `32` 开始，文件由 $0..N$ 个紧邻的 **Batch Frame** 顺序拼接而成。每个 Batch Frame 包含 **28 字节固定帧头（Frame Header）** 与 **变长帧载荷（Frame Payload）**。

#### 3.2.1 帧头布局（Frame Header - Fixed 28 Bytes）

| 帧内偏移 (Offset) | 长度 (Bytes) | 字段名称 | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- | :--- |
| `0..4` | 4 | `FrameMagic` | `uint32` | 固定帧同步魔数：`0x4D52464B`（小端字节序对应 ASCII `"KFRM"`） |
| `4..8` | 4 | `HeaderCRC32` | `uint32` | **独立帧头校验**：覆盖帧内偏移量 `8..28` 共 20 字节的 IEEE `CRC32` |
| `8..16` | 8 | `TxID` | `uint64` | 严格单调递增的事务序列号（**新帧分配规则见下方 TxID 单调性铁律**） |
| `16..20` | 4 | `OpCount` | `uint32` | 当前帧内包含的操作条目数量（**硬性约束：`1 <= OpCount <= 1,000,000`，磁盘严禁出现 `OpCount = 0` 的空帧**） |
| `20..24` | 4 | `PayloadLen` | `uint32` | 紧随帧头之后的载荷总字节长度（硬上限：`64 MB = 67,108,864` 字节） |
| `24..28` | 4 | `PayloadCRC32` | `uint32` | **独立载荷校验**：覆盖帧内偏移量 `28..(28 + PayloadLen)` 全部字节的 IEEE `CRC32` |

* **TxID 单调性铁律（内存实例维护与落盘继承）**：
  1. 数据库打开时：若从磁盘顺序加载了 $K$ 个帧，取最后一帧的 `TxID` 初始化内存 `currentMaxTxID = lastFrame.TxID`；若磁盘为 32B 空库，初始化 `currentMaxTxID = 0`。
  2. 运行期间：**即使用户调用 `delete` 或 `dropNamespace` 将数据库所有键删光，内存中的 `currentMaxTxID` 严禁重置为 0**！
  3. 产生新帧时（快照落盘或追加写入）：新帧的序列号必须计算为：
     $$\text{newTxID} = \max(\text{memoryKnownMaxTxID}, \text{diskScannedMaxTxID}) + 1$$
     并立即将内存 `currentMaxTxID` 更新为 `newTxID`。

#### 3.2.2 帧载荷内的操作条目布局（Operation Entry inside Payload）

在 `28..(28 + PayloadLen)` 的载荷区内，紧凑顺序排列 `OpCount` 个操作条目：

| 条目字段 | 长度 (Bytes) | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- |
| `OpCode` | 1 | `uint8` | `0x01` = `OpPut`（写入/更新键值）<br>`0x02` = `OpDelete`（删除指定键，`ValLen=0`）<br>`0x03` = `OpDropNamespace`（清空并删除指定命名空间，`KeyLen=0, ValLen=0`） |
| `ValueType` | 1 | `uint8` | **v1.0 MVP 必选实现**：<br>`0x00` = `Bytes`（原始无类型字节数组）<br>`0x01` = `Utf8String`（UTF-8 编码字符串）<br>**预留扩展类型码**：<br>`0x02` = `Int64`（8 字节小端有符号整数）<br>`0x03` = `Float64`（8 字节小端 IEEE 754 双精度浮点数）<br>`0x04` = `Bool`（1 字节：`0x00`=false，`0x01`=true）<br>（注：当 `OpCode != 0x01` 时，`ValueType` 固定填 `0x00`） |
| `NsLen` | 2 | `uint16` | 命名空间名称 UTF-8 字节长度（**硬上限 `0 <= NsLen <= 255`，超长在 API 校验时立即抛出 `InvalidNamespaceError`**；默认命名空间强制编码为 `NsLen = 0`） |
| `KeyLen` | 2 | `uint16` | Key 的 UTF-8 字节长度（`OpDropNamespace` 时为 `0`；其余操作允许 `0 <= KeyLen <= 65,535`） |
| `ValLen` | 4 | `uint32` | Value 的字节长度（硬上限 `16 MB = 16,777,216` 字节；删除类操作固定为 `0`） |
| `NamespaceBytes` | `NsLen` | `byte[]` | Namespace UTF-8 字节流 |
| `KeyBytes` | `KeyLen` | `byte[]` | Key UTF-8 字节流 |
| `ValueBytes` | `ValLen` | `byte[]` | Value 字节流 |

### 3.3 字节级确定性输出规则（Canonical Serialization Rules）

1. **默认命名空间唯一表示法与系统保留别名**：
   * 默认命名空间在磁盘上**唯一编码为 `NsLen = 0`（零字节）**。
   * **保留字警示**：字符串 `"default"` 被定义为默认命名空间的保留别名。API 层若传入 `""`（空串）或 `"default"`，统一归一化为默认命名空间（`NsLen = 0`）。调用方无法在物理上创建一个真实名为 `"default"` 的独立命名空间。
2. **快照 / Compaction 帧内条目排序与过滤规则**：
   写出全量快照或执行 `compact()` 时：
   * **仅写出当前存活的 `OpPut (0x01)` 条目**，已被删除的键与已被 Drop 的命名空间物理剔除，快照文件严禁出现 `OpDelete` 或 `OpDropNamespace`。
   * 若当前存活条目数为 0，直接写出 32 字节文件头即完成，不写出任何 BatchFrame。
   * 所有条目严格按照 **`(NamespaceBytes, KeyBytes)` 的无符号 UTF-8 字节字典序升序** 排列：先按 `NamespaceBytes` 升序排列（`NsLen = 0` 的默认命名空间自然排在最前），同一命名空间内按 `KeyBytes` 升序排列。
3. **超 64MB 快照的确定性装箱伪代码（Greedy Packing）**：
   当存活条目总量较大时，序列化器必须严格按以下确定性伪代码进行贪心切分：
   ```text
   const MAX_PAYLOAD = 67,108,864; // 64 MB
   const MAX_OPS = 1,000,000;
   const ENTRY_FIXED_HEADER = 10;  // 1B OpCode + 1B ValueType + 2B NsLen + 2B KeyLen + 4B ValLen

   currentTxID = max(memoryKnownMaxTxID, diskScannedMaxTxID) + 1;
   frame = NewBatchFrame(TxID = currentTxID);

   for each entry in sorted_alive_entries:
       entryBytesLen = ENTRY_FIXED_HEADER + entry.NsLen + entry.KeyLen + entry.ValLen;
       // 严格判定：操作数满 100 万 或 累计载荷加上本条将超过 64MB 时切分
       if (frame.OpCount >= MAX_OPS) or (frame.PayloadLen + entryBytesLen > MAX_PAYLOAD):
           frame.FinalizeAndFlush();
           currentTxID = currentTxID + 1;
           frame = NewBatchFrame(TxID = currentTxID);
       
       frame.AppendEntry(entry);

   frame.FinalizeAndFlush();
   memoryKnownMaxTxID = currentTxID; // 更新内存 TxID
   ```
   *说明：快照的多帧切片属于同一快照版本的物理分片，连续递增 `TxID` 仅为满足物理帧单调性，重放加载时顺序全量应用。*
4. **批次内操作应用顺序**：读取或重放任意 `BatchFrame` 时，严格按 `0 .. OpCount - 1` 顺序依次应用到内存。
5. **浮点数与 64 位整数规范化**：`Float64` 的 `NaN` 规范化为小端 `00 00 00 00 00 00 F8 7F`；Node.js 的 `Int64` 强制使用原生 `bigint`。

### 3.4 阶段隔离的崩溃恢复与损坏处理

* **阶段一（v1.0 快照模式）极简自愈规则**：
  * 加载器从 `offset = 32` 循环读取帧至 `fileSize`。每帧验证 `FrameMagic == 0x4D52464B`、`HeaderCRC32`、`TxID > lastTxID` 及 `PayloadCRC32`。
  * **一刀切原则**：由于阶段一通过原子轮转写入，正式文件若发生任何损坏，必定是扇区介质损坏或外部篡改。**加载器坚决不执行向后魔数扫描，坚决不截断修改正式文件**，直接判定为文件损坏，转入 §4.2 的 `.bak` 自愈回退。
* **阶段二（v1.1 追加写模式）向后扫描机制**：
  * 仅在阶段二启用：遇到坏块时向后扫描寻找匹配 `FrameMagic`、`HeaderCRC32` 且 `candidateTxID > lastTxID` 的后续帧。找到合法后帧确诊为中间坏块抛错拒开；未找到则确诊为断电残尾，写者备份至 `.corrupt.<unix_ms>` 并截断主文件。

---

## 4. 跨 6 语言工程深水区统一实现规范

### 4.1 锁模型、防 ABA 协议与并发安全性

#### 4.1.1 进程内线程安全（Thread-Safety）契约
* **全语言强制规范**：所有语言导出的 `Db` 实例必须是**进程内多线程并发安全（Thread-Safe）**的。
* **并发控制机制**：内部统一维护一把读写锁（Go `sync.RWMutex`、Rust `parking_lot::RwLock` 或 `std::sync::RwLock`、C# `ReaderWriterLockSlim`、Python `threading.RLock`、Node.js/Dart 基于单线程任务调度），读操作无阻塞并发；写操作（`put`/`delete`/`batch`/`compact`）互斥独占。

#### 4.1.2 锁文件 32 字节二进制结构与防 ABA 释放协议
为彻底杜绝“进程挂起超时被窃取锁后复活，误删他人新持有的锁”这一经典 ABA 竞态，锁文件采用固定 32 字节二进制格式：

| 偏移 | 长度 | 字段名称 | 类型 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `0..8` | 8 | `PID` | `uint64` | 持有锁的操作系统进程 ID（小端序） |
| `8..24` | 16 | `LockUUID` | `byte[16]` | 本次加锁会话随机生成的 16 字节 UUID |
| `24..32` | 8 | `CreateTimeMs` | `uint64` | 加锁时的 Unix 毫秒时间戳（小端序） |

* **加锁流程**：以 `O_CREAT | O_EXCL`（独占创建）模式打开 `<db>.lock`，写入上述 32 字节后 `fsync` 关闭句柄（或保持句柄，视平台而定）。
* **释放锁防 ABA 校验铁律**：
  释放锁时，**必须先读取 `<db>.lock` 的前 32 字节，比对其中的 `LockUUID`**：
  * 若文件存在且 `LockUUID` 与本实例持有的 UUID 完全相等：调用文件系统删除操作 `unlink(<db>.lock)`。
  * 若文件不存在，或 `LockUUID` 与本实例不匹配（说明自身挂起期间锁已被判定超时并被新进程合法窃取）：**严禁删除文件**！放弃删除操作并记录告警。
* **僵死锁判定与 Windows 共享冲突约束**：
  独占创建 `<db>.lock` 失败时读取其创建时间戳与修改时间：
  * 若 `now - lockTime < lockTimeoutMs`（默认 `10,000 ms`，由 `DbOptions` 传入）：按 `[10ms, 30ms, 60ms, 120ms]` 指数退避重试，超时抛出 `DatabaseLockedError`。
  * 若 `now - lockTime >= lockTimeoutMs`：判定可能为僵死锁，生成随机临时名 `<db>.lock.steal.<uuid>` 执行 `rename("<db>.lock", "<db>.lock.steal.<uuid>")`：
    * **Windows 实测硬约束**：若前一进程依然存活且持有句柄，`rename` 必定抛出 `WinError 32 / 33`（共享冲突）或 `WinError 5`（拒绝访问）。**一旦遇到句柄占用错误，严禁强行删除文件，必须立即判定持锁者依然存活**，转入退避重试或抛出 `DatabaseLockedError`；
    * 若 `rename` 成功，删除 `.steal.<uuid>` 并重新执行独占创建。

#### 4.1.3 快照模式写锁前置检查（轻量跳跃扫描）
写操作获取短锁后，比对磁盘最新状态：
* 以只读方式打开磁盘 `data.db`，读取 32B 文件头；
* 顺着各帧的 `PayloadLen` 执行跳跃 `Seek(28 + PayloadLen)`，仅读取每个帧头的 28 字节，定位末尾帧的 `TxID` 与文件大小；
* 若最新 `(MaxTxID, FileSize)` 与本实例上次已知状态不一致，说明有外部进程修改了文件，抛出 `ConcurrentModificationError` 并释放锁。

### 4.2 两代文件覆盖原子轮转（消除写放大与 ENOSPC 风险）

* **写入替换四步法**：
  1. **写临时文件**：将新快照全量写入 `data.db.tmp` -> 调用 `fsync` -> 关闭句柄。
  2. **原子轮转旧代备份**：若当前 `data.db` 存在，调用带 Windows 退避重试的覆盖式 `rename`，直接将 `data.db` 重命名覆盖至 `data.db.bak`（在 Windows / POSIX 上这仅仅是毫秒级元数据修改，**写放大为 0%**，即使磁盘剩余空间为 0 字节也能瞬间完成）。
  3. **原子提交正式文件**：调用覆盖式 `rename` 将 `data.db.tmp` 重命名覆盖为 `data.db`。
  4. **目录刷盘（Best-Effort）**：在支持目录 `fsync` 的操作系统（Linux/macOS POSIX）上对父目录执行 `fsync(dirFd)`；在不支持的平台（Windows、Dart、Node.js）安全跳过。
* **Windows 覆盖重试与保护**：
  在 Windows 上调用覆盖式 `rename` 遇到 `WinError 5` 或 `WinError 32` 时，按 `[5ms, 15ms, 30ms, 60ms, 120ms]` 重试 5 次（总耗时约 230ms）。若重试耗尽仍失败，抛出 `IoError`，**且必须保留 `data.db.tmp` 文件不删**，以供后续自愈。
* **极简启动自愈算法（两级全文件校验）**：
  每次 `open("data.db")` 时：
  1. **清理未竟临时文件**：
     * 若 `data.db` 缺失但 `data.db.tmp` 通过全文件校验：说明系统在上文步骤 2 与 3 之间崩溃。直接将 `data.db.tmp` 重命名晋升为 `data.db`。
     * 其余情况下安全清理残留的 `data.db.tmp`。
  2. **校验主文件**：对 `data.db` 执行全文件校验（验证 32B 文件头与所有帧的 CRC）。全部通过则完成加载。
  3. **自愈回退至 `.bak`**：若 `data.db` 不存在或校验失败，检查 `data.db.bak`：
     * 若 `data.db.bak` 存在且通过全文件校验：将其复制恢复为 `data.db`（并 `fsync`），触发 `onRecoveredFromBackup` 告警回调，完成加载。
     * 若 `data.db.bak` 也不存在或同样损坏：抛出 `DataCorruptedError`，坚决拒绝静默清空用户数据。

### 4.3 内存数据模型与六语言统一内联 `CRC32`

#### 4.3.1 内存模型与强制无符号字节排序
* 内存顶层映射：`Map<NamespaceString, NamespaceStore>`。
* 排序铁律：全语言必须基于 **0..255 无符号字节序（Unsigned Byte Order）** 逐字节比较；
  * **Rust**：直接使用 `BTreeMap<Vec<u8>, InternalEntry>`；
  * **Go**：必须在内存中维护有序数据结构（如内联红黑树/AVL树，或有序切片索引），避免全表全量排序；
  * **C# / .NET**：严格禁止使用默认 `SortedDictionary`，必须显式注入基于 `compareUtf8Bytes` 的 `IComparer<string>`。

#### 4.3.2 统一内联 IEEE 802.3 `CRC32`
* 所有语言统一在源码内联 256 项的查表算法（多项式 `0xEDB88320`，初始值 `0xFFFFFFFF`，最终异或 `0xFFFFFFFF`），消除所有第三方依赖。
* **CRC32 标准验证向量**：ASCII 字符串 `"123456789"` 计算结果严格等于 **`0xCBF43926`**。

### 4.4 追加写句柄生命周期与并发架构（C15 决策）

1. **取消组提交（Group Commit）**：
   嵌入式 KV 引擎（SQLite / LMDB / bbolt）面向进程内单写者，与分布式/网络关系型数据库（如 PostgreSQL / MySQL）由连接池并发提交事务的场景不同。在嵌入式单写者架构下引入组提交队列不仅无法提升吞吐，反而会增加锁等待与上下文切换延迟，因此正式削减该设计。
   * **2026-10-17 复核维持本决策**：`AppendOnlyLog` 模式下取消每写 fsync 仅提升 15%~31% 吞吐，说明瓶颈不在 fsync 次数，组提交的收益模型（N 次 fsync 合并为 1 次）不成立。重开条件为：先按同口径归因 WAL 每写约 1.9ms 的固定开销（句柄生命周期 / 进程写锁 / `precheck` 的 stat / AV 扫描），归因结果显示合并 fsync 能带来数量级提升时，再走正式决策复查单。

2. **按需轻量追加句柄（Non-Resident Append Handle）**：
   - 在追加写（`AppendOnlyLog`）模式下，每次写入按需打开句柄并在操作完成（或 fsync）后随作用域关闭；
   - **Windows 文件共享冲突防护**：Windows 操作系统对处于打开状态的文件执行覆盖式重命名（`MoveFileEx` / `ReplaceFile`）极为敏感，若维持常驻写句柄，在触发自动压实（Compaction）、故障恢复或外部工具轮转时极易导致 `WinError 32`（`ERROR_SHARING_VIOLATION`）；
   - **性能验证**：基于现代 OS 的 VFS / Dentry 缓存，打开句柄开销仅为纳秒级，非立即刷盘模式下吞吐量已超过 68 万 ops/sec；而立即刷盘（`Immediate`）模式下瓶颈纯为磁盘 fsync（微秒/毫秒级），句柄开销占比不足 0.05%。因此维持按需轻量句柄是保障跨平台（特别是 Windows）原子轮转稳定性的最佳实践。

### 4.5 自动压实触发策略（C8 增补，2026-10-17）

`AppendOnlyLog` 模式的自动压实由**两个触发器**共同决定，二者任一命中即执行一次压实（复用同一套退避/失败计数体系，见 D2）：

| 触发器 | 条件 | 语义 |
| :--- | :--- | :--- |
| 文件大小（原有） | `fileSize >= compactionThresholdBytes` | 日志增长到阈值即回收 |
| 死键比例（新增） | `fileSize >= 2 MB` 且 `(fileSize − livePayload) / fileSize >= 50%` | 大批量删除/覆写后日志长不到阈值，按死数据占比回收 |

- **`compactionThresholdBytes == 0` 仍关闭全部自动压实路径**，包括死键比例触发；这是唯一的"总开关"，不做部分开启。
- **`livePayload` 定义**：全部命名空间存活条目的 `Σ(len(key) + len(value))`，不含命名空间名、帧头与文件头。因此它恒小于文件大小，计算比例时需夹紧（`live > fileSize` 视为 `live = fileSize`）。
- **必须 O(1) 增量维护**：在内存变更的单一收口处（Rust `mem_put/mem_delete/mem_drop/mem_clear`；Go/Kotlin/Dart/.NET 的 `NamespaceStore.Put/Delete`）记账；禁止在每次写入时遍历全库重算。绕过 `put/delete` 的批量装载路径（如 .NET 快照快速装载）必须在装载完毕后重算一次。
- **不新增公开选项**：阈值常量（2 MB / 50%）固定在实现内部，调整属于版本级决策。

---

## 5. 接口契约、类型行为与验证套件

### 5.1 核心配置与标准错误体系

#### 5.1.1 `DbOptions` 配置规范
| 字段名称 | 类型 | 默认值 | 语义说明 |
| :--- | :--- | :--- | :--- |
| `readOnly` | `bool` | `false` | 为 `true` 时以只读模式打开，禁止写操作，不获取写锁 |
| `lockTimeoutMs` | `uint32` | `10000` | 判定僵死锁的超时毫秒数（10 秒，C19 统一） |
| `forceUnlock` | `bool` | `false` | 启动时若发现遗留锁，强制清除（单实例桌面 App 崩溃重启场景） |
| `onWarning` | `callback?` | `null` | 告警通知回调：`onWarning(type, message)`（如从备份自愈恢复） |
| `autoRecoverFromBackup` | `bool` | `false` | 主库损坏时是否自动用 `.bak` 替换（D1：`.bak` 落后一代，默认关闭） |
| `syncMode` | `enum` | `Immediate` | `Immediate` / `Deferred` / `Manual` 刷盘策略 |
| `syncIntervalMs` | `uint32` | `100` | `Deferred` 模式的后台刷盘周期 |
| `engineMode` | `enum` | `SnapshotAtomic` | `SnapshotAtomic` / `AppendOnlyLog` 写入引擎 |
| `compactionThresholdBytes` | `uint64` | `4194304`（4 MB） | `AppendOnlyLog` 自动压实阈值；`0` 表示关闭自动压实（C8 统一） |
| `strictCompaction` | `bool` | `false` | 自动压实失败是否向调用方报错（D2：默认仅 `onWarning` 并退避重试） |
| `syncOnWrite` | `bool`（仅部分实现） | `true` | 每次写提交是否显式 fsync（.NET / Dart 提供；Rust / Go / Kotlin 由 `syncMode` 等价表达） |

##### 5.1.1.1 默认值原则：可靠性优先

1. **默认值必须是"最保守、最不容易丢数据"的组合**：`SnapshotAtomic` + `Immediate`。每次写入全量序列化 → `fsync` → 原子轮转，磁盘上不存在半成品状态，掉电最多丢失正在进行的一次写。默认值不为吞吐让步，性能敏感场景由调用方显式切换到 `AppendOnlyLog`。
2. **默认值跨语言逐项一致**，由跨语言默认值矩阵测试守护；"字段是否存在"与"默认值"同等重要，不得出现某语言默认关闭自动压实这类行为级漂移。
3. **改默认值属于 ADR 级决策**（与 D1/D2 同级），必须记录重开理由、同口径基准数据与受影响的语言清单。

#### 5.1.2 统一错误体系表（Standard Error Hierarchy）
所有语言必须定义基类 `DbError`，并派生以下同名同义错误类型：

| 错误类型名称 | 继承关系 | 触发场景 |
| :--- | :--- | :--- |
| `DbError` | Exception / Error | 所有库内部错误的公共基类 |
| `CorruptedError` | `DbError` | 格式魔数错误、CRC32 校验失败、主库与备份均损坏时抛出 |
| `LockedError` | `DbError` | 获取 `.lock` 失败且退避重试超时时抛出 |
| `ConcurrentModificationError`| `DbError` | 快照落盘前检查发现磁盘 `TxID/FileSize` 被外部修改时抛出 |
| `InvalidUtf8Error` | `DbError` | 调用 `getString()` 时底层的原始字节序列并非合法 UTF-8 编码 |
| `TypeMismatchError` | `DbError` | 读取或对键执行原子增减时，底层实际 `ValueType` 与请求目标类型不一致时拦截抛出 |
| `KeyTooLargeError` | `DbError` | Key 超过 65,535 字节或 Value 超过 16 MB 限制 |
| `InvalidNamespaceError` | `DbError` | 命名空间 UTF-8 字节长度超过 255 字节 |
| `UnsupportedVersionError` | `DbError` | 文件头 `FormatReadVer > 3` 时抛出 |

### 5.2 核心接口与 `batch()` 纯内存暂存契约

#### 5.2.1 `Entry` 结构体规范
```typescript
interface Entry {
  readonly namespace: string;       // 命名空间（默认命名空间统一归一化返回 ""）
  readonly key: string;             // 键名
  readonly valueType: number;       // 0x00: Bytes, 0x01: String, 0x02: Int64, 0x03: Float64, 0x04: Bool
  readonly rawBytes: Uint8Array;    // 原始字节数组

  isBytes(): boolean;
  isString(): boolean;
  isInt64(): boolean;
  isFloat64(): boolean;
  isBool(): boolean;

  asString(): string;              // valueType == 0x01 时按 UTF-8 解码；类型不匹配抛 TypeMismatchError，非法编码抛 InvalidUtf8Error
  asInt64(): bigint;               // valueType == 0x02 时按 8B 小端解码；类型不匹配抛 TypeMismatchError
  asFloat64(): number;             // valueType == 0x03 时按 IEEE 754 8B 小端解码；类型不匹配抛 TypeMismatchError
  asBool(): boolean;               // valueType == 0x04 时解码 1B 布尔；类型不匹配抛 TypeMismatchError
}
```

#### 5.2.2 核心方法清单与行为契约

| 方法名 | 参数签名 | 语义与多态行为规范 |
| :--- | :--- | :--- |
| `open(path, options?)` | `(string, DbOptions?) -> Db` | 检查并自愈恢复、全文件多帧校验加载，返回实例 |
| `close()` | `() -> void` | **生命周期与刷盘终止**：释放内存资源与跨进程文件锁，唤醒并终止后台刷盘工作线程。**强要求**：在 `Deferred` / `Manual` 同步模式下，写操作可能滞留在内存脏状态，应用退出前**必须显式调用 `close()` / `Dispose()`** 以保证脏数据同步刷盘并重试待压实任务；禁止依赖隐式垃圾回收或进程退出。 |
| `containsKey(ns, key)` | `(string, string) -> bool` | **P0**：内存 O(1) 查询键是否存在，避免反序列化开销 |
| `count(ns)` | `(string) -> int` | **P0**：返回指定命名空间下的有效键总数 |
| `getBytes(ns, key)` | `(string, string) -> uint8[]?` | 内存 O(1) 读取底层原始字节数组；不存在返回空 |
| `getString(ns, key)` | `(string, string) -> string?` | 读取字符串；若 `ValueType != 0x01` 抛 `TypeMismatchError`；非法编码抛 `InvalidUtf8Error` |
| `getInt64(ns, key)` | `(string, string) -> int64?` | 读取 64 位整数；若 `ValueType != 0x02` 抛 `TypeMismatchError` |
| `getFloat64(ns, key)` | `(string, string) -> float64?`| 读取 64 位双精度浮点；若 `ValueType != 0x03` 抛 `TypeMismatchError` |
| `getBool(ns, key)` | `(string, string) -> bool?` | 读取布尔值；若 `ValueType != 0x04` 抛 `TypeMismatchError` |
| `putBytes(ns, key, val)` | `(string, string, uint8[]) -> void` | 存入原始字节（`0x00`），触发原子快照落盘并通知 Watcher |
| `putString(ns, key, val)` | `(string, string, string) -> void` | 存入 UTF-8 字符串（`0x01`），触发原子快照落盘并通知 Watcher |
| `putInt64(ns, key, val)` | `(string, string, int64) -> void` | 存入 8 字节小端整数（`0x02`），触发原子快照落盘并通知 Watcher |
| `putFloat64(ns, key, val)` | `(string, string, float64) -> void`| 存入 8 字节小端浮点（`0x03`，NaN 规范化），触发落盘并通知 Watcher |
| `putBool(ns, key, val)` | `(string, string, bool) -> void` | 存入 1 字节布尔（`0x04`），触发落盘并通知 Watcher |
| `incrementInt64(ns, key, delta)`| `(string, string, int64) -> int64` | **P0 原子累加器**：若键不存在则初始化为 `delta`；若存在且为 `int64` 则加上 `delta`；**若存在但非 `int64` 则拦截抛出 `TypeMismatchError`** |
| `delete(ns, key)` | `(string, string) -> bool` | 单条删除指定键，返回是否实际删除了键；成功触发落盘并通知 Watcher |
| `dropNamespace(ns)` | `(string) -> bool` | 物理彻底删除指定命名空间，返回是否命中存在；成功触发落盘并通知 Watcher |
| `clearNamespace(ns)` | `(string) -> bool` | **P0**：清空命名空间内的所有条目但保留命名空间自身；成功触发落盘并通知 Watcher |
| `batch(fn)` | `((BatchWriter) -> void) -> void` | **纯内存 Staging 机制**：`fn` 执行期间写入暂存区，抛错则丢弃；成功后原子合入主状态并落盘，在锁外按序派发所有变更事件 |
| `watch(callback)` | `((KeyChangeEvent) -> void) -> void`| **P1**：注册数据变更监听回调，写事务在完全释放写锁后执行回调 |
| `keys(ns, prefix?)` | `(string, string?) -> string[]` | 按 **UTF-8 无符号字典序升序** 返回匹配前缀的所有键名 |
| `scanRange(ns, startKey?, endKey?)` | `(string, string?, string?) -> Iterable<Entry>` | 按 **UTF-8 无符号字典序升序** 迭代 `[startKey, endKey)` 左闭右开区间条目 |
| `compact()` | `() -> void` | 按确定性排序规则重写为紧凑快照文件，`TxID = currentMaxTxID + 1` |

#### 5.2.3 P1 数据变更通知机制（Watcher 规范）
1. **事件定义**：
   ```text
   KeyChangeOp:
     Put = 1            // 键新增或值更新（含 incrementInt64）
     Delete = 2         // 键被删除
     DropNamespace = 3  // 整个命名空间被删除（Key 为空串）
     Clear = 4          // 命名空间被清空（Key 为空串）

   KeyChangeEvent:
     Namespace: string
     Key: string
     Op: KeyChangeOp
   ```
2. **死锁防御核心铁律（Lock-Free Callback Contract）**：
   - 库内部在持有写锁期间仅**同步收集变更事件快照**；
   - **严禁在持有写锁期间调用 Watcher 用户回调**；
   - 必须在写锁完全释放后，再遍历事件快照通知各监听回调；
   - 保证用户在 Watcher 回调内部再次调用 `db.getBytes()`、`db.containsKey()`、`db.count()` 等读接口时，绝不会引发重入读写死锁。

### 5.3 跨语言黄金测试向量（Golden Test Vectors）

#### 5.3.1 基础基准文件十六进制镜像（Python 实测 100% 验证通过）

1. **合法空数据库标准镜像（`empty_v3.db` - 32 Bytes）**：
   * 文件十六进制数据（小端序）：
     ```text
     00000000: 55 4b 56 44 42 30 30 33 03 00 03 00 00 00 00 00  UKVDB003........
     00000010: 00 00 00 00 00 00 00 00 00 00 00 00 37 c9 44 0c  ............7.D.
     ```
   * 结构解析：
     * `0..8`: ASCII `"UKVDB003"`
     * `8..12`: `ReadVer=3`, `WriteVer=3`
     * `12..28`: 16 字节全零（`HeaderFlags` + `Reserved`）
     * `28..32`: `HeaderCRC32 = 0x0C44C937`（覆盖前 28 字节，小端对应 `37 c9 44 0c`）
   * 对应文件 SHA-256：`06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328`

2. **单条 Put 记录快照镜像（`single_put_v3.db` - 80 Bytes）**：
   * 写入单条数据：默认命名空间 `""`，Key 为 `"hello"`，Value 为 `"world"`（`ValueType = 0x01`），`TxID = 1`。
   * 文件十六进制数据（小端序）：
     ```text
     00000000: 55 4b 56 44 42 30 30 33 03 00 03 00 00 00 00 00  UKVDB003........
     00000010: 00 00 00 00 00 00 00 00 00 00 00 00 37 c9 44 0c  ............7.D.
     00000020: 4b 46 52 4d 3e b9 1d 50 01 00 00 00 00 00 00 00  KFRM>..P........
     00000030: 01 00 00 00 14 00 00 00 33 db cb 59 01 01 00 00  ........3..Y....
     00000040: 05 00 05 00 00 00 68 65 6c 6c 6f 77 6f 72 6c 64  ......helloworld
     ```
   * 结构解析：
     * `0..32`: 32 字节文件头（同上）；
     * `32..60`: 28 字节帧头：
       * `32..36`: `FrameMagic = 0x4D52464B` (`"KFRM"`)
       * `36..40`: `HeaderCRC32 = 0x501DB93E`（覆盖帧偏移 `40..60` 共 20 字节，小端 `3e b9 1d 50`）
       * `40..48`: `TxID = 1`（`01 00 00 00 00 00 00 00`）
       * `48..52`: `OpCount = 1`（`01 00 00 00`）
       * `52..56`: `PayloadLen = 20`（`14 00 00 00`）
       * `56..60`: `PayloadCRC32 = 0x59CBDB33`（小端 `33 db cb 59`）
     * `60..80`: 20 字节载荷（Payload）：
       * `60`: `OpCode = 0x01` (`OpPut`)
       * `61`: `ValueType = 0x01` (`Utf8String`)
       * `62..64`: `NsLen = 0`（`00 00`）
       * `64..66`: `KeyLen = 5`（`05 00`）
       * `66..70`: `ValLen = 5`（`05 00 00 00`）
       * `70..75`: Key `"hello"`（`68 65 6c 6c 6f`）
       * `75..80`: Value `"world"`（`77 6f 72 6c 64`）
   * 对应文件 SHA-256：`f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a`