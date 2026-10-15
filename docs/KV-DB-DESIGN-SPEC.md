# 跨平台多语言纯原生 Key-Value 数据库架构设计与规范蓝图（v3.1 实施定稿版）

## 1. 设计定位、适用边界与 v3.1 修订说明

本项目旨在设计并实现一款面向桌面端（Windows、Linux）与移动端（Flutter/Dart、.NET MAUI 等）App 的高可靠、轻量级嵌入式 Key-Value（KV）数据库工具库，并面向开源社区发布。

### 1.1 核心工程前提与边界声明

1. **纯原生多语言实现（无 FFI/JNI）**：
   在 **.NET (C#)、Rust、Go、Dart、Node.js、Python**（后续可扩展 Kotlin/Swift）中采用各语言标准库 100% 纯原生实现，不依赖 `mmap`，底层完全建立在标准文件流操作（`seek`/`pread` + `read`/`write` + `fsync`）之上。
2. **跨平台字节级兼容与文件共享边界**：
   各语言生成的 `.db` 文件在字节级 100% 互通。适用场景包括：**跨语言/跨平台导入导出备份、宿主升级迁移、以及非并发的跨工具读写（如 GUI 关闭后由 CLI 工具读取或修改配置）**。
   * **明确不支持的场景**：**不支持多进程（如 CLI 与 GUI）同时并发读写同一个数据库文件**，也不提供多设备文件级双向同步的冲突合并（多端文件同步仅限整文件覆盖）。
3. **关键设计与演进纪律**：
   * 采用“写操作短持锁（Snapshot 模式）”或“会话独占锁 + 基于时间窗口与原子重命名的僵死锁回收协议”。
   * 采用“两代文件覆盖原子轮转”消除写放大，配合常驻 `.bak` 备份消除文件缺失窗口。
   * 用向后扫描 `FrameMagic` 重同步替代脆弱的固定长度阈值，实现断电半写（Torn Tail）与介质坏块（Bit-rot）的严格区分。
   * 6 种语言统一内联 IEEE 802.3 `CRC32` 查表法，实现跨语言真正的零外部依赖。

### 1.2 v3.1 实施就绪度修订重点说明

为彻底消除多语言分头独立实施时出现的未定义行为（Undefined Behavior）与联调分歧，v3.1 在 v3.0 基础上完成了以下 6 项实施闭环锁定：

1. **空数据库生命周期闭环**：正式确立 $N=0$（仅 32 字节文件头）为合法空数据库文件形态；消除空库下 `1 <= OpCount` 的规约冲突；定义空库初始 `TxID = 0`。
2. **两代原子轮转替换（Atomic Rotation）**：弃用先写 `.tmp` 再复制 `.bak.tmp` 的双倍写放大模式，改用“写 `.tmp` -> 原子轮转原 `.db` 至 `.bak` -> 原子覆盖 `.tmp` 至 `.db`”，彻底消除 200% I/O 放大与磁盘耗尽（ENOSPC）崩溃风险。
3. **类型系统与多态读写契约**：明确 `ValueType`（`Bytes` vs `Utf8String`）在 `getString`/`getBytes` 交叉读取时的解码策略与强类型异常行为；固化 `Entry` 结构体规范。
4. **内存数据模型与多语言排序器落地指导**：明确各语言必须基于无符号字节（`0..255`）逐字节对比；解决 C# `SortedDictionary` 默认 UTF-16 比较器与 Emoji 排序断层问题；固化 `scanRange` 的边界与空值语义。
5. **实例级线程安全性（Thread-Safety）**：明确规范所有语言的 DB 实例均为**进程内线程安全**，内部统一基于并发读写锁（`RWMutex`/`RwLock`）管理并发。
6. **Windows 锁窃取与轻量 TxID 定位规范**：吸收实机实测结果，规范在 Windows 上遭遇 `WinError 32/33` 时判定持锁者依然存活并禁止窃取的逻辑；规定快照模式下写锁检查通过只读 28B 帧头的轻量跳跃扫描定位最新 `TxID`。

---

## 2. 存储架构演进路线：整库快照 vs. 精简追加写日志

二进制格式定义为 **“1 个不可变文件头（32 Bytes） + $N$ 个自包含的原子批次帧（$N \ge 0$）”**，**整库快照模式（Snapshot Mode）** 与 **精简追加日志模式（Append-Only Mode）** 共享完全相同的底层文件格式：

| 评估维度 | 阶段一（v1.0 MVP）：整库快照 + 原子替换 | 阶段二（v1.1 按需演进）：精简追加写日志 |
| :--- | :--- | :--- |
| **核心写路径** | 内存驻留全量数据；每次写入将全库序列化至 `.tmp` -> `fsync` -> 原子轮转旧库至 `.bak` -> 原子覆盖 `rename` 至 `.db`。 | 内存驻留索引（或全量数据）；每次写入向 `.db` 末尾追加 1 个 `BatchFrame` 并立即 `fsync`，低频触发 `compact()`。 |
| **物理帧数量 ($N$)** | 空库时 $N = 0$；通常 $N = 1$（若单库总载荷超过 64MB，则自动切分为连续的多个 64MB `BatchFrame`）。 | $N \ge 0$（随每次写入递增，`compact()` 后重新收敛回紧凑快照）。 |
| **预估单语言代码量** | **约 400 – 600 行**（含多帧加载器、UTF-8 字节序比较、锁协议、Windows 重试、内联 CRC32）。 | **约 1,000 – 1,800 行**（在阶段一基础上增加 `FrameMagic` 重同步扫描与尾部截断恢复）。 |
| **句柄持有策略** | **平时不持有 `.db` 句柄**：加载完即关闭，写入时仅在毫秒级窗口内操作文件，天然免疫 Windows 句柄占用。 | **写者常驻持有 `.db` 句柄**：仅在 `compact()` 替换瞬间短暂关闭并重开句柄。 |
| **适用数据量与频率** | **< 1MB – 2MB**、每秒写入不超过数次的配置、账号凭证、轻量本地状态（参考 [Bitcask](https://riak.com/assets/bitcask-intro.pdf) 的紧凑态）。 | **1MB – 100MB**、高频即时落盘的离线缓存、会话记录（参考 [Hive CE](https://github.com/IO-Design-Team/hive_ce) 与 [rosedb](https://github.com/rosedblabs/rosedb)）。 |

> **关键设计约束**：
> 即便在阶段一（v1.0）只实现“快照模式写入器”，**所有语言的读取器（Loader）也必须从一开始就实现为完整的多帧循环加载器（`while offset < fileSize`）**。这样既能无缝读取大于 64MB 的多帧分片快照，也能前向兼容阶段二或其它语言追加模式写出的 $N > 1$ 文件。

---

## 3. 《Binary Format Specification v3.1》字节级规范

所有多字节整数与浮点数强制采用 **Little-Endian（小端序）**；所有字符串（Namespace 与 String Key）强制采用无 BOM 的 **UTF-8** 编码。

### 3.1 不可变文件头（Immutable File Header - Fixed 32 Bytes）

文件头在创建文件时一次性写入，**创建后永不原地修改**。

| 字节偏移 (Offset) | 长度 (Bytes) | 字段名称 | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- | :--- |
| `0..8` | 8 | `FileMagic` | `byte[8]` | 固定 ASCII 魔数：`0x55 0x4B 0x56 0x44 0x42 0x30 0x30 0x33`（`"UKVDB003"`） |
| `8..10` | 2 | `FormatReadVer` | `uint16` | 允许读取此文件的最低格式版本（v3.1 固定为 `3`） |
| `10..12` | 2 | `FormatWriteVer` | `uint16` | 允许写入此文件的最低格式版本（v3.1 固定为 `3`） |
| `12..16` | 4 | `HeaderFlags` | `uint32` | 预留全局标志位（v3.1 固定填 `0`；若读取端遇到未知非零必选位则拒绝打开） |
| `16..28` | 12 | `Reserved` | `byte[12]` | 预留扩展字节，写入时必须全部填充 `0x00` |
| `28..32` | 4 | `HeaderCRC32` | `uint32` | 覆盖偏移量 `0..28` 共 28 字节的 IEEE `CRC32` 校验值 |

* **空数据库文件形态（Empty DB State）**：
  新初始化的空数据库或所有键清空后的数据库文件**大小恰好为 32 字节（即仅包含上述不可变文件头，$N=0$ 个帧）**。
  读取器加载规则：当文件通过前 32 字节校验且 `fileSize == 32` 时，判定为合法空数据库，停止扫描并成功返回，内存状态为空，`currentMaxTxID = 0`。

### 3.2 复合批次帧（Batch Frame - 1 Batch = 1 Frame）

从文件偏移量 `32` 开始，文件由 $0..N$ 个紧邻的 **Batch Frame** 顺序拼接而成。每个 Batch Frame 包含 **28 字节固定帧头（Frame Header）** 与 **变长帧载荷（Frame Payload）**。

#### 3.2.1 帧头布局（Frame Header - Fixed 28 Bytes）

| 帧内偏移 (Offset) | 长度 (Bytes) | 字段名称 | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- | :--- |
| `0..4` | 4 | `FrameMagic` | `uint32` | 固定帧同步魔数：`0x4D52464B`（小端字节序对应 ASCII `"KFRM"`） |
| `4..8` | 4 | `HeaderCRC32` | `uint32` | **独立帧头校验**：覆盖帧内偏移量 `8..28` 共 20 字节的 IEEE `CRC32` |
| `8..16` | 8 | `TxID` | `uint64` | 严格单调递增的批次事务序列号（新写入从 `1` 开始；**执行 `compact()` 或快照重写时必须取 `currentMaxTxID + 1`，严禁重置为 `1`**） |
| `16..20` | 4 | `OpCount` | `uint32` | 当前帧内包含的操作条目数量（**硬性约束：`1 <= OpCount <= 1,000,000`，磁盘严禁出现 `OpCount = 0` 的空帧**） |
| `20..24` | 4 | `PayloadLen` | `uint32` | 紧随帧头之后的载荷总字节长度（硬上限：`64 MB = 67,108,864` 字节） |
| `24..28` | 4 | `PayloadCRC32` | `uint32` | **独立载荷校验**：覆盖帧内偏移量 `28..(28 + PayloadLen)` 全部字节的 IEEE `CRC32` |

#### 3.2.2 帧载荷内的操作条目布局（Operation Entry inside Payload）

在 `28..(28 + PayloadLen)` 的载荷区内，紧凑顺序排列 `OpCount` 个操作条目：

| 条目字段 | 长度 (Bytes) | 类型 | 说明与取值规范 |
| :--- | :--- | :--- | :--- |
| `OpCode` | 1 | `uint8` | `0x01` = `OpPut`（写入/更新键值）<br>`0x02` = `OpDelete`（删除指定键，`ValLen=0`）<br>`0x03` = `OpDropNamespace`（清空并删除指定命名空间，`KeyLen=0, ValLen=0`） |
| `ValueType` | 1 | `uint8` | **v1.0 MVP 必选实现**：<br>`0x00` = `Bytes`（原始无类型字节数组）<br>`0x01` = `Utf8String`（UTF-8 编码字符串）<br>**预留扩展类型码**：<br>`0x02` = `Int64`（8 字节小端有符号整数）<br>`0x03` = `Float64`（8 字节小端 IEEE 754 双精度浮点数）<br>`0x04` = `Bool`（1 字节：`0x00`=false，`0x01`=true）<br>（注：当 `OpCode != 0x01` 时，`ValueType` 固定填 `0x00`） |
| `NsLen` | 2 | `uint16` | 命名空间名称 UTF-8 字节长度（`0 <= NsLen <= 255`；**默认命名空间强制规范化为 `NsLen = 0`**） |
| `KeyLen` | 2 | `uint16` | Key 的 UTF-8 字节长度（`OpDropNamespace` 时为 `0`；其余操作允许 `0 <= KeyLen <= 65,535`） |
| `ValLen` | 4 | `uint32` | Value 的字节长度（硬上限 `16 MB = 16,777,216` 字节；删除类操作固定为 `0`） |
| `NamespaceBytes` | `NsLen` | `byte[]` | Namespace UTF-8 字节流 |
| `KeyBytes` | `KeyLen` | `byte[]` | Key UTF-8 字节流 |
| `ValueBytes` | `ValLen` | `byte[]` | Value 字节流 |

### 3.3 字节级确定性输出规则（Canonical Serialization Rules）

为确保不同语言对同一份内存状态执行快照保存或 `compact()` 时生成**逐字节 100% 相同**的 `.db` 文件（从而支持 Golden Test 直接比对 SHA-256），强制规定以下 5 条确定性规则：

1. **默认命名空间唯一表示法**：
   默认命名空间在磁盘上**唯一编码为 `NsLen = 0`（零字节）**。API 层若传入 `""`（空串）或 `"default"`，必须在内存层统一归一化为默认命名空间（`NsLen = 0`），严禁在磁盘上写出字面量 `"default"` 的 7 字节命名空间。
2. **快照 / Compaction 帧内条目排序与过滤规则**：
   写出全量快照或执行 `compact()` 时：
   * **仅写出当前存活的 `OpPut (0x01)` 条目**，已删除键与已被 Drop 的命名空间物理丢弃，严禁在快照中出现 `OpDelete` 或 `OpDropNamespace`。
   * 若当前数据库存活条目数为 0，直接写出 32 字节文件头即完成，不写出任何 BatchFrame。
   * 所有条目必须严格按照 **`(NamespaceBytes, KeyBytes)` 的无符号 UTF-8 字节字典序升序** 排列：先按 `NamespaceBytes` 升序排列（`NsLen = 0` 的默认命名空间自然排在最前），同一命名空间内按 `KeyBytes` 升序排列。
3. **超 64MB 快照的确定性多帧切分规则**：
   若全量快照序列化时累计载荷即将超过单帧硬上限 `64 MB`（`67,108,864` 字节）或 `OpCount` 达到 `1,000,000`，写入器应在当前条目之前闭合当前 `BatchFrame(TxID = T)`，并立即开启下一个 `BatchFrame(TxID = T + 1)` 继续写出剩余有序条目（若单个键值对本身超过上限则在 `put` 时直接报错拒绝）。
4. **批次内操作应用顺序**：
   在读取或重放任意 `BatchFrame` 时，严格按照帧内 `0 .. OpCount - 1` 的先后顺序依次应用到内存状态（例如同一批次内先 `OpDropNamespace("cache")` 再 `OpPut("cache", "k1", "v1")`，最终 `"k1"` 存活）。
5. **浮点数与 64 位整数规范化（当启用 `0x02`/`0x03` 类型时）**：
   * `Float64` 的所有 `NaN` 值在写入前必须统一规范化为 IEEE 754 标准静默 `NaN`（位模式 `0x7FF8000000000000`，小端字节序 `00 00 00 00 00 00 F8 7F`）；`-0.0`（`0x8000000000000000`）与 `+0.0` 原样保留。
   * Node.js 实现中的 `Int64` 接口必须强制使用原生 `bigint`（`Buffer.readBigInt64LE` / `writeBigInt64LE`），严禁转为 JS `number`。

### 3.4 崩溃恢复与基于 `FrameMagic` 重同步的损坏判定算法

**刷盘前置铁律**：在追加写模式（Append-Only Mode）下，**每一次写入新的 `BatchFrame` 必须完成操作系统级 `fsync` 后才能向调用方返回成功**，不提供延迟刷盘选项，防止操作系统乱序落盘在文件中间留下未写入的零洞。

当加载器从 `offset = 32` 循环读取帧直到 `fileSize` 时：

1. **合法帧递进**：
   若 `fileSize - offset >= 28`，且 `FrameMagic == 0x4D52464B`、`HeaderCRC32` 校验通过、`TxID > lastTxID`、`fileSize - (offset + 28) >= PayloadLen`、且 `PayloadCRC32` 校验通过 -> 按顺序应用帧内操作，更新 `lastTxID = TxID`，推进 `offset += 28 + PayloadLen`。
2. **异常触发向后重同步扫描（Forward Resync Scan）**：
   一旦在某个 `badOffset` 处遇到任何异常（包括：剩余字节不足 28 字节、`FrameMagic` 不匹配、`HeaderCRC32` 失败、剩余字节不足 `PayloadLen`、或 `PayloadCRC32` 失败）：
   * **第一步：检查是否为全零预分配尾段**：
     检查 `badOffset .. fileSize` 的所有字节是否全部为 `0x00`。若是，判定为文件系统预分配尾段，直接进入判定分支 B 按尾部截断处理。
   * **第二步：向后扫描寻找后续合法帧**：
     从 `scanPos = badOffset + 1` 循环遍历至 `fileSize - 28`，寻找字节匹配 `FrameMagic (0x4D52464B)` 的位置：
     * 若匹配到魔数，依次检验：
       1. `HeaderCRC32`（覆盖候选帧头 `8..28` 字节）是否通过；
       2. `PayloadLen <= 64MB` 且 `1 <= OpCount <= 1,000,000` 且 `candidateTxID > lastTxID`；
       3. 若 `fileSize - (scanPos + 28) >= PayloadLen`，验证 `PayloadCRC32` 是否通过。
     * 若上述条件**全部满足**：说明在坏块之后存在有效帧，直接判定为 **分支 A（中间介质损坏）**，停止扫描；
     * 若任一条件不满足（例如用户数据碰撞魔数）：继续推进 `scanPos += 1` 向后查找，直到文件结束。
   * **判定分支 A（找到后续合法帧 -> 确诊为中间损坏 Bit-rot）**：
     只要在 `badOffset` 之后找到了任何一个通过完整校验的后续帧，说明已落盘的历史数据发生了介质损坏或乱序缺失。**严禁执行任何截断操作！** 立即中止加载并向调用方抛出 `DataCorruptedError(badOffset)`。
   * **判定分支 B（未找到任何后续合法帧 -> 确诊为尾部半写 Torn Tail）**：
     说明损坏确实发生在日志的最末端（典型的断电半写或系统崩溃）。此时必须区分打开模式：
     * **只读模式（Read-Only Mode）**：忽略 `badOffset .. fileSize` 的残尾数据，保留已成功加载的 `0 .. badOffset` 内存状态，触发 `onTailIgnored(badOffset, fileSize)` 告警回调，**绝不修改或 `truncate` 磁盘文件**。
     * **可写模式（Write Mode）**：**强制**将 `badOffset .. fileSize` 的残缺字节完整备份写出至 `<db_path>.corrupt.<unix_ms>` 并执行 `fsync`，随后对主文件执行 `truncate(badOffset)` + `fsync`，并触发 `onTailTruncated(badOffset, corruptBackupPath)` 回调通知上层应用。

---

## 4. 跨 6 语言工程深水区统一实现规范

### 4.1 锁模型、并发安全性与僵死锁安全清理

#### 4.1.1 进程内线程安全（Thread-Safety）契约
* **全语言强制规范**：所有语言导出的 `Db` 实例必须是**进程内多线程并发安全（Thread-Safe）**的。
* **并发控制机制**：内部统一维护一把读写锁（Go `sync.RWMutex`、Rust `RwLock`、C# `ReaderWriterLockSlim`、Python `threading.RLock`、Node.js/Dart 基于事件循环的异步任务队列），允许多个读操作（`getString`/`getBytes`/`scanRange`/`keys`）无阻塞并发执行；写操作（`put`/`delete`/`batch`/`compact`）互斥独占。

#### 4.1.2 跨语言原子独占创建锁文件（`O_CREAT | O_EXCL`）
* **Node.js**：`fs.openSync(lockPath, 'wx')`
* **Python**：`os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)`
* **Go**：`os.OpenFile(lockPath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)`
* **Rust**：`OpenOptions::new().write(true).create_new(true).open(lock_path)`
* **.NET (C#)**：`new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)`
* **Dart**：`File(lockPath).createSync(exclusive: true)`
* **同进程防重入**：各语言内部维护进程级规范化路径集合（如 `Mutex<HashSet<CanonicalPath>>`），防止同一进程重复打开同一文件路径。

#### 4.1.3 锁生命周期与僵死锁安全窃取
1. **快照模式（推荐默认）：写瞬间短持锁（Short Write Lock）**：
   `open()` 时不创建常驻 `.lock`（仅加载内存）；每次调用写入保存时执行：
   * **步骤 A**：获取 `<db>.lock`（独占创建）。
   * **步骤 B**：轻量比对磁盘状态是否被外部修改：通过标准文件流仅读取文件头与各帧 28 字节帧头（顺着 `PayloadLen` 跳跃 Seek，无需读取大载荷），读取末尾帧最新的 `(MaxTxID, FileSize)`。若与当前实例上次已知状态不一致，抛出 `ConcurrentModificationError` 并释放锁。
   * **步骤 C**：写入 `.tmp` 并通过两代原子轮转替换正式文件。
   * **步骤 D**：删除释放 `<db>.lock`。
2. **僵死锁判定与 Windows 共享冲突约束**：
   单次写锁持有时间仅为毫秒级。若独占创建 `<db>.lock` 失败：
   * 读取 `<db>.lock` 的修改时间 `mtime`：
     * 若 `now - mtime < lockTimeoutMs`（默认 `10,000 ms`）：说明有并发写入在进行，按 `[10ms, 30ms, 60ms, 120ms]` 指数退避重试，超时抛出 `DatabaseLockedError`。
     * 若 `now - mtime >= 10,000 ms`：判定可能为上次进程崩溃遗留的僵死锁。
   * **安全窃取流程与实测约束**：
     * 生成带随机 UUID 的临时名 `<db>.lock.steal.<uuid>`；
     * 调用覆盖式重命名 `rename("<db>.lock", "<db>.lock.steal.<uuid>")`；
     * **关键实测约束**：在 Windows 上，若前一进程依然存活且持有句柄，`rename` 必定抛出 `WinError 32 / 33`（共享冲突）或 `WinError 5`（拒绝访问）。**一旦遇到句柄占用错误，严禁强行删除文件，必须立即判定持锁者依然存活**，转入退避重试或抛出 `DatabaseLockedError`；
     * 若 `rename` 成功，再次检查被移走文件的 `mtime` 确实已过期，删除 `.steal.<uuid>` 并重新执行独占创建 `<db>.lock`。

### 4.2 两代文件覆盖原子轮转（消除写放大与 ENOSPC 风险）

为彻底消除 v3.0 中“写 `.tmp` 后必须完整复制原库到 `.bak.tmp`”所带来的 200% 物理写放大与磁盘耗尽（ENOSPC）风险，v3.1 采用**两代原子重命名轮转（Atomic Rotation）**协议：

* **各语言跨平台覆盖式 `rename`**：
  * **Go**：`os.Rename(src, dst)`（Windows 底层自 Go 1.5 采用 `MOVEFILE_REPLACE_EXISTING`）
  * **Rust**：`std::fs::rename(src, dst)`
  * **Python**：`os.replace(src, dst)`
  * **Node.js**：`fs.renameSync(src, dst)`
  * **Dart**：`File(src).renameSync(dst)`
  * **.NET (C#)**：`File.Move(src, dst, overwrite: true)`
* **写入替换四步法**：
  1. **写临时文件**：将新数据全量写入 `data.db.tmp` -> 调用 `fsync` -> 关闭句柄。
  2. **原子轮转旧代备份**：若当前 `data.db` 存在，调用带 Windows 退避重试的覆盖式 `rename`，直接将 `data.db` 重命名覆盖至 `data.db.bak`（在 Windows / POSIX 上这仅仅是毫秒级目录项修改，**写放大为 0%**，即使磁盘剩余空间为 0 字节也能瞬间完成）。
  3. **原子提交正式文件**：调用覆盖式 `rename` 将 `data.db.tmp` 重命名覆盖为 `data.db`。
  4. **目录刷盘**：在支持目录 `fsync` 的操作系统上对父目录执行一次 `fsync(dirFd)`。
* **极简启动自愈算法（两级全文件校验）**：
  每次 `open("data.db")` 时：
  1. **清理未竟临时文件**：若存在残留的 `data.db.tmp`：
     * 若 `data.db` 缺失但 `data.db.tmp` 通过全文件校验：说明系统在上述步骤 2 与步骤 3 之间崩溃。直接将 `data.db.tmp` 重命名晋升为 `data.db`，恢复成功。
     * 若 `data.db` 已存在或 `.tmp` 校验失败：直接安全删除 `data.db.tmp`。
  2. **校验主文件**：对 `data.db` 执行全文件校验（验证 32B 文件头与所有帧的 CRC）。校验通过则完成加载。
  3. **自愈回退至 `.bak`**：若 `data.db` 不存在或校验失败（遭遇物理介质坏块），检查常驻的上一代备份 `data.db.bak`：
     * 若 `data.db.bak` 存在并通过全文件校验：将其复制恢复为 `data.db`（并 `fsync`），触发 `onRecoveredFromBackup` 告警回调，完成加载。
     * 若 `data.db.bak` 也不存在或同样损坏：抛出 `DataCorruptedError`，坚决拒绝静默清空用户数据。

### 4.3 内存数据模型、无符号 UTF-8 比较与内联 `CRC32`

#### 4.3.1 内存数据模型与多语言排序器规范
* **内存组织模型**：
  每个 DB 实例内部维护两级映射：`Map<NamespaceString, NamespaceStore>`。
  `NamespaceStore` 内部保存 Key 到条目的映射。为了支持高效的 `keys(ns, prefix)` 与 `scanRange(ns, startKey, endKey)`：
  * **Rust**：直接采用 `BTreeMap<Vec<u8>, InternalEntry>`（Rust 标准库无符号字节切片天然满足无符号 UTF-8 字典序）。
  * **Go**：采用内存有序结构（如红黑树/B-Tree），或使用 `map` 存储并在查询时按无符号字节序排序。
  * **C# / .NET**：**严格禁止直接使用默认的 `SortedDictionary<string, V>`**！因为 C# 默认基于 UTF-16 比较，对于非 BMP 字符（如 Emoji `😀` 与 `\uE000`）其排序与 UTF-8 字节序相反。必须提供自定义 `IComparer<string>`：
    ```csharp
    public sealed class Utf8StringComparer : IComparer<string> {
        public int Compare(string? x, string? y) {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            byte[] bx = Encoding.UTF8.GetBytes(x);
            byte[] by = Encoding.UTF8.GetBytes(y);
            return CompareUtf8Bytes(bx, by);
        }
    }
    ```
* **通用无符号字节对比算法（全语言统一）**：
  ```
  function compareUtf8Bytes(a: uint8[], b: uint8[]): int
      minLen = min(a.length, b.length)
      for i from 0 to minLen - 1:
          if a[i] != b[i]:
              return a[i] - b[i] // 必须按 0..255 无符号比较！
      return a.length - b.length
  ```

#### 4.3.2 六语言统一内联 IEEE 802.3 `CRC32`
* 所有语言统一在源码内联 256 项的查表算法（多项式 `0xEDB88320`，初始值 `0xFFFFFFFF`，最终异或 `0xFFFFFFFF`），消除所有第三方依赖与平台包差异。
* **CRC32 标准测试向量**：
  输入 ASCII 字符串 `"123456789"`（十六进制 `31 32 33 34 35 36 37 38 39`），计算所得 CRC32 必须严格等于 **`0xCBF43926`**。

---

## 5. 接口契约、类型行为与验证套件

### 5.1 核心接口规范与多态类型行为契约

#### 5.1.1 `Entry` 结构体规范
```typescript
interface Entry {
  readonly namespace: string;       // 命名空间（默认命名空间统一归一化返回 ""）
  readonly key: string;             // 键名
  readonly valueType: number;       // 0x00: Bytes, 0x01: Utf8String
  readonly rawBytes: Uint8Array;    // 原始字节数组
  stringValue(): string;            // 将 rawBytes 按 UTF-8 解码；若包含非法字节序列则抛出 InvalidUtf8Error
}
```

#### 5.1.2 核心方法清单与行为契约

| 方法名 | 参数签名 | 语义与多态行为规范 |
| :--- | :--- | :--- |
| `open(path, options?)` | `(string, DbOptions?) -> Db` | 检查并自愈恢复、执行全文件多帧校验加载，返回实例 |
| `close()` | `() -> void` | 释放内存与文件锁 |
| `getBytes(ns, key)` | `(string, string) -> uint8[]?` | 内存 O(1) 读取。**无论存入时是 String 还是 Bytes，均一律返回底层的原始字节数组**；不存在返回 `null`/`nil`/`None` |
| `getString(ns, key)` | `(string, string) -> string?` | 内存 O(1) 读取。若存储类型为 `Utf8String` 直接返回；若为 `Bytes`，尝试 UTF-8 解码，**若包含非法 UTF-8 字节必须抛出 `InvalidUtf8Error`**，禁止静默替换；不存在返回 `null` |
| `putBytes(ns, key, val)` | `(string, string, uint8[]) -> void` | 存入原始字节，磁盘标记 `ValueType = 0x00` |
| `putString(ns, key, val)` | `(string, string, string) -> void` | 存入字符串，磁盘标记 `ValueType = 0x01` |
| `delete(ns, key)` | `(string, string) -> void` | 单条删除指定键（若键不存在则视为空操作成功返回） |
| `dropNamespace(ns)` | `(string) -> void` | 原子清空并删除指定命名空间下的全部键值 |
| `batch(fn)` | `((BatchWriter) -> void) -> void` | 原子批次：在 `fn` 中累积操作并一次性原子落盘。若 `fn` 抛错则全部回滚；若为空批次则直接返回不落盘 |
| `keys(ns, prefix?)` | `(string, string?) -> string[]` | 按 **UTF-8 无符号字典序升序** 返回当前命名空间中所有匹配前缀的键名。`prefix` 为空或不传时返回全部键 |
| `scanRange(ns, startKey?, endKey?)` | `(string, string?, string?) -> Iterable<Entry>` | 按 **UTF-8 无符号字典序升序** 迭代 `[startKey, endKey)` 左闭右开区间条目。<br>• `startKey` 为空/null：从首个条目开始；<br>• `endKey` 为空/null：扫描至末尾；<br>• `startKey >= endKey`：返回空迭代器 |
| `compact()` | `() -> void` | 按 3.3 节规则将全库重写为最紧凑的快照文件，`TxID = currentMaxTxID + 1` |

### 5.2 跨语言一致性验证套件（Test Vectors）

#### 5.2.1 基础基准文件十六进制镜像（Hex Dumps）

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

#### 5.2.2 跨语言 Golden Test 矩阵
* **`logical_suite.json`**：包含 UTF-8 临界排序字符（`\u0000`、`a`、`z`、`\uE000`、`😀`）、空字符串键、默认命名空间归一化、批次内覆写等 50 组确定性测试用例。
* **`fault_injection/`**：包含断电残尾截断、中间坏块拒绝、`.bak` 自动恢复等标准故障样本。