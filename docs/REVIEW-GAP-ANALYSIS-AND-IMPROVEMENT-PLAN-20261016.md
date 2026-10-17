# CKV《缺口分析与改进方案》评审报告

> **评审日期**: 2026-10-16 22:20:56
> **评审对象**: [docs/GAP-ANALYSIS-AND-IMPROVEMENT-PLAN-20261016.md](GAP-ANALYSIS-AND-IMPROVEMENT-PLAN-20261016.md)（下称"原报告"）
> **评审基线**: `dev` 分支 HEAD `b79b62b`（feat(core): implement phase 3 fix plan across 5 languages）
> **评审方法**: 对原报告全部 11 项缺口与 8 项改进方案逐条对照当前 HEAD 的代码实证核实；对 §1.2 工程统计用脚本重算；对性能数据与同日《SyncMode 与 WAL 性能报告》交叉比对。所有证据均标注 `文件:行号`（除非另有说明均为当前 HEAD）。

---

## 1. 总体结论

原报告的**评估框架、取舍原则与多数机制描述是可靠的**：11 项缺口中 8 项经代码核实属实，改进方案的性价比判断大体正确。但存在 **2 处必须先解决的硬伤**（改进 6 回避了已沉底的 C15 决策与自家基准数据；改进 1 的 .NET 方案违反项目"零 P/Invoke"红线）、**1 处机理描述失实**（缺口 4）、**1 处虚构引证**（缺口 1 对 Kotlin `TreeMap` 的引用）与**一批可修复的数据瑕疵**。另有两个**被遗漏的确实缺口**（SnapshotAtomic+Immediate 双默认最差路径；Dart 默认值三处漂移）。

**裁决意见**：原报告可以继续作为认知基线，但在按下表修订/补充之前，**路线图不应直接施工**。本评审第 5 节给出修订后的行动计划（v1.1-rev / v2.0-rev），其第 6 节给出对原报告的手术式修改清单。

| 评审要点 | 结论 | 建议动作 |
| :--- | :--- | :--- |
| 缺口 1/2/3/5/8/9/11 | ✅ 机理属实（见 §2） | 保留 |
| 缺口 4（跨进程锁） | ⚠️ 结论保留、机理表述失实 | 按 §4.4 修订 |
| 缺口 7/10（压缩、故障注入） | ⚠️ 定性正确，选型需修正 | 按 §4.6 修订 |
| 改进 6（组提交） | ❌ 与 C15 决策及基准数据冲突 | 先重开决策流程，见 §4.1 |
| 改进 1（.NET 目录 fsync） | ❌ 方案违反零 P/Invoke 红线 | 按 §4.3 换方案 |
| 改进 2（批内读已写） | ⚠️ Rust 是 API 破坏性变更 | 挂起至冻结政策裁定，见 §4.5 |
| 两个遗漏缺口 / 数据瑕疵 | — | 按 §3、§4.7 补正 |

---

## 2. 十一个缺口的实证核实结果

| # | 原报告论断 | 核实 | 证据（HEAD `b79b62b`） |
| :--- | :--- | :---: | :--- |
| 1 | 数据常驻内存、冷启动 O(N) 全量加载 | ✅ | Rust `db.rs:90`（`BTreeMap<Vec<u8>, Arc<BTreeMap<…>>>`）；`format.rs` `load_with_recovery` 自偏移 32 顺序加载。**但引用的 Kotlin `Storage.kt:19 ("TreeMap<ByteArray, InternalEntry>")` 不存在**——Kotlin 实现为 `ArrayList` + 二分查找（`Storage.kt:15-25`，`NamespaceStore.items`），全 `src/kotlin/` 检索不到 `TreeMap`（README 中"红黑树"的表述同样与 Kotlin 实现不符，宜一并修正） |
| 2 | Immediate 每写独占锁内 open + fsync、无组提交 | ✅ | `db.rs:1972-1976` 每次突变 `OpenOptions::open`；`db.rs:2015-2017` `sync_all`；调用方 `put_internal`→`apply_mutation` 全程持 `inner.write()` |
| 3 | 压实 = 全库快照重写、开销 O(N)、写栈内毛刺 | ✅ | `db.rs:2170-2218` `serialize_snapshot_to_tmp` 全量遍历；`commit_flush`（`db.rs:2225-2251`）在写锁内 rename 轮转 |
| 4 | "读写打开均排他锁定整库" | ⚠️ | 结论（不支持一写多读、不并发双写者）成立，但**只读连接之间是共享锁**：`lock.rs:28-29`（`try_lock_shared`）、`lock_unix.go:12`（`LOCK_SH`）、`LockFile.cs:51-55`（`FileShare.Read`）。原文"只读进程打开直接被拒绝"仅在"与写者并发"时发生 |
| 5 | BatchWriter 无批内读已写 | ✅ | Rust `Batch`（`db.rs:1339-1449`，`BatchWriter` 是 `db.rs:1463` 的类型别名）、Go `db.go:1153-1287`、.NET `Db.cs:2418-2517` 均只有 Put*/Delete/Drop/Clear，无 Get |
| 6 | 文档迭代器为内存迭代（非磁盘游标） | ✅ | `db.rs:1666-1674` `iter_range` 一次性物化 `scan_range` 的 `Vec`，非惰性游标 |
| 7 | 无数据压缩 | ✅ | 帧载荷为原始字节流（各语言 `BatchFrameWriter`/`flushFrame` 直写 payload） |
| 8 | 无 TTL；压实仅按文件字节阈值触发 | ✅ | `options.rs:61-63` 唯一触发键；`maybe_compact_after_write`（`db.rs:1872-1893`）无死键比例感知。**五语言均有自动压实**（Kotlin `CkvDb.kt:541-552`、Dart `db.dart:431-444` 亦确认，原报告 §2.2 无此问题） |
| 9 | .NET/Dart/Kotlin 缺目录 fsync | ✅ | Rust `db.rs:2353-2365`（cfg(unix)）与 Go `lock_unix.go:43-48` 有 `fsyncParentDir`；.NET `AtomicReplace`（`Db.cs:1194`）轮转后无目录同步；Dart `db.dart:407-410` 计入 `_lastKnownDisk*` 后直接返回；Kotlin `FileRotation.kt` 仅文件级 `fd.sync()`（第 79 行针对 `.stage`） |
| 10 | 无 VFS 级故障注入体系 | ✅ | 现有 FI 测试覆盖坏字节/截断/单例冲突，无写中崩溃与 fsync EIO 注入层 |
| 11 | 快照 CoW 整命名空间克隆膨胀 | ✅ | `db.rs:498-503` 快照克隆外层 map 共享内层 Arc；`db.rs:528` `Arc::make_mut` 于快照存续期间每次写入触发该命名空间全量深克隆 |

---

## 3. 原报告遗漏的两个确实缺口（建议新增）

### 新缺口 12：`SnapshotAtomic + Immediate` 是全局默认值组合即最差性能路径

- **证据**：双默认值即 `SnapshotAtomic`＋`Immediate`（Rust `options.rs:83/85`、Go `options.go:38/36`、Kotlin `Options.kt:27/29`、.NET `Options.cs`、Dart `options.dart:33/35`）。此组合下每次 `put` 都执行 `apply_mutation → commit_flush`（`db.rs:2053-2056`）：全量序列化整个库 + fsync + 两次 rename。同日《SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016》实测：Rust 253 ops/s、WAF 54x，写 200 次时 Go/.NET WAF 达 107.75x。
- **缺口性质**：原报告缺口 3 只处理了 WAL 模式的压实 stall；全性能谱系上最昂贵的路径（默认双值组合）反而没有被单独立项。这决定了对新用户"开箱即用"的第一印象。
- **处置建议**：不擅自修改默认值——持久性偏好是 D1/D2 同级的 ADR 决策。先做两件确定无争议的事：(a) 在 `KV-DB-DESIGN-SPEC.md` 与各语言 README 的快速上手章节明确"桌面通用负载推荐 `AppendOnlyLog`"，给出选型表（性能报告 §4 已有雏形，应前置到 README）；(b) 在 `open` 时可选告警（`onWarning("onSlowDefaultMode")`，默认关闭，避免打扰）。

### 新缺口 13：Dart 语言的 ADR 对齐欠账（C8 / C19 漏改）

- **证据**（五语言默认值矩阵，逐文件核实）：

| 选项 | Rust `options.rs:80/84/86` | Go `options.go:34/37/39` | .NET `Options.cs:17/25/31` | Kotlin `Options.kt:23/28/34` | **Dart `options.dart:37/34/36`** |
| :--- | :---: | :---: | :---: | :---: | :---: |
| `lockTimeoutMs` | 10000 | 10000 | 10000 | 10000 | **5000** |
| `syncIntervalMs` | 100 | 100 | 100 | 100 | **50** |
| `compactionThresholdBytes` | 4MB | 4MB | 4MB | 4MB | **0（自动压实默认关闭）** |

- Kotlin 注释（`Options.kt:17-23`）明确记载 lockTimeout"按 C19 统一为 10000、compactionThreshold 按 C8 统一为 4MB（与其余四语言一致）"——**唯独 Dart 未跟上**。Dart 的 WAL 自动压实因此默认全关，这是行为差异而不仅是数值差异。
- **附带发现（选项覆盖面漂移）**：`syncOnWrite` 选项仅 .NET（`Options.cs:34`）与 Dart（`options.dart:22`）存在，Rust/Go/Kotlin 无此字段。建议制作跨语言 `DbOptions` 字段覆盖矩阵作为规范 §5.1.1 的附录，把"字段是否存在 + 默认值"纳入一致性检查。
- **处置**：补正属缺陷修复（bug-fix）级别，零架构成本，建议立即列入 P0（详见 §5 RA3）。

---

## 4. 对原报告改进方案的修订意见

### 4.1 改进 6（组提交）：先重开 C15 决策，且现有收益预估不成立

**决策冲突**：`KV-DB-DESIGN-SPEC.md §4.4`（C15）明确规定："在嵌入式单写者架构下引入组提交队列不仅无法提升吞吐，反而会增加锁等待与上下文切换延迟，因此正式削减该设计"，并已沉底 `API-V1-FREEZE.md §3.17`。原报告将组提交列为阶段二方案时未引用、未回应、未附重开流程。

**数据矛盾（更实质的问题）**：组提交的收益模型是"N 个 fsync 合并为 1 个"。但《SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016.md §3.1》显示 WAL 模式下**把 fsync 从每次写中完全拿掉，吞吐几乎不变**：

| 语言 | WAL+Immediate | WAL+Deferred（无每写 fsync） | 差距 |
| :--- | :---: | :---: | :---: |
| Rust | 446 ops/s | 513 ops/s | +15% |
| Go | 464 ops/s | 607 ops/s | +31% |
| .NET | 439 ops/s | 557 ops/s | +27% |

fsync 只占到 WAL 写成本的 ~15-30%，**每写约 1.7-2.0ms 的固定开销另有其物**（嫌疑项：per-write 句柄 open/close、进程写锁、`precheck` 的 `path.exists()+fs::metadata`、Windows Defender 等扫描器）。C15 注释引用的"Deferred 吞吐已达 68 万 ops/s"是 `SnapshotAtomic+Deferred` 的数据，将其作为"句柄开销不足 0.05%"论据用于 WAL 模式属于跨口径引用，**支撑与反对组提交的两边都需要同一口径的新基准**。

**结论**：
1. 在按 §4.1 所列嫌疑项完成 WAL 写路径固定开销的归因基准测试之前，组提交不立项；
2. 若归因结果证实"合并 fsync 可达 5~10 倍 WAL 吞吐"，再以**正式决策复查单**（记录对规范 §4.4 三条理由的重新评估）重开 C15，并连带讨论锁模型重组（当前 `inner.write()` 独占锁横跨整个 fsync，是任何写合并的前置改造）；
3. 原报告"10,000~30,000 ops/s"的预估在上述证据落地前应标注为待验证假说。

**诊断步骤（X-1 定位实验，测试构建专用，不入产线）**：
- (a) 建仅测试用的实验分支，维持**常驻追加句柄**（违反 C15 的配置独立开关），量化 open/close delta；
- (b) 纯 open+close 循环基准（同文件同盘），得到句柄生命周期单项成本；
- (c) 在关/开 Defender 实时扫描的两种状态下复跑 (a)(b)(c)；
- (d) 复核 `precheck`（`db.rs:2119-2130` 的 `exists()+metadata()`）与 `last_known_disk_state` 内存化后的必要性。

### 4.2 改进 3（自适应压实）：方案认可，补一个 O(1) 实现细化

原方案方向正确。补充实现要点（原报告未给出，直接决定该功能是否可行）：

- `livePayloadSize` **可增量维护且零额外 I/O**：内存态本就持有全部存活 KV，初始打点 = Σ 内存条目 `encoded_len()`；此后每次 `put` 做 `live += entry_size(new) − entry_size(old)`、`delete` 做 `live −= old`、`clear/drop` 做 `live −= ns_total`，每次突变 O(1)（`apply_mutation` 内挂账即可）。
- `deadRatio = 1 − live/fileSize`，`fileSize` 直接复用 `last_known_disk_state.1`（已有缓存，无新 syscall）。
- 触发条件合并进现有 `maybe_compact_after_write`（`db.rs:1872-1893`）：沿用现有 `needs_compaction` 退避/失败计数体系（`db.rs:1863-1957`），不新造重试机制。
- 该计数器与**改进 4（内存配额）共享**：`livePayloadSize` × 节点开销系数即可作为内存预算的粗排，两个特性实现共用一套账本，减少五语言重复开发面。

### 4.3 改进 1（跨语言目录 fsync）：.NET 方案必须更换，Kotlin/Dart 平台差异须如实标注

原 .NET 方案"通过 P/Invoke 或 POSIX API"**直接违反 README 核心设计理念第一条（"彻底杜绝 P/Invoke"）与原报告 §4 自己重申的红线**。修订为：

- **.NET（.NET 8+）**：优先验证全托管路径 `File.OpenHandle(dirPath, FileAccess.Read) → RandomAccess.FlushToDisk`（.NET 8 引入的托管 fsync）。**该路径对目录句柄是否可用尚待在 Linux/macOS 实测**（.NET 在 Unix 上对目录的句柄打开能力不受保证）；实测失败则降级为 no-op + `onWarning("onDirSyncUnavailable")`，netstandard2.0/Unity 目标直接定位为 no-op。**禁止引入 P/Invoke**——宁可降级并文档化，不可破红线。
- **Kotlin**：Linux 下 `FileChannel.open(dir, READ).force(true)` 可行；macOS 对目录 fd 的 fsync 行为不可靠（可能 EINVAL 或无副作用），必须 **best-effort try/catch**＋平台差异文档化，不得让轮转失败冒泡。
- **Dart**：`dart:io` 无目录 fd API，**没有真正可行的实现**。可选：(a) no-op + 文档标注（建议，默认）；(b) 可选开关下于轮转后 `Process.runSync("sync")`（Linux 全局 sync，代价大且依赖系统工具存在，不适合移动端，仅作桌面可选项）。
- **统一框架**：三语言方案均收口为"尽力而为 + 失败不轮转失败 + onWarning 上报"，Rust/Go 既有实现（`db.rs:2356`、`lock_unix.go:44`）保持为参照解。预期收益表述也应降级为"消除 Linux 上 rename 元数据丢失窗口的确定性补强"，而非"彻底消除"——Windows 本就 no-op（NTFS 元数据由操作系统保证）。

### 4.4 缺口 4 表述修订

将原报告"读写打开均排他锁定整库"修订为："**可写连接持排他锁；只读连接持共享锁（只读进程间可多开并发），但不支持一写多读（写者排斥读者）与双写者**。" 缺口本身成立；"跨进程单写多读协议（IPC Read Sharing）"若进阶段二，须以现有共享锁语义为改造起点，而非从"全排他"起步。

### 4.5 改进 2（批内读已写）：挂起至 API 冻结政策裁定，并补读点语义设计

- **各语言影响矩阵**（原报告未评估，差异很大）：

| 语言 | 现状 | 改造成本 |
| :--- | :--- | :--- |
| Go | `BatchWriter` 已持有 `db *DB`（`db.go:1154`） | 低：加 `Get` 方法即可 |
| .NET | `BatchWriter` 已持有 `CkvDb`（`Db.cs:2423`） | 低：加 `Get` 方法即可 |
| Rust | `batch(f: FnOnce(&mut Batch))` 闭包无 Db 访问（`db.rs:1248-1255`），`write_batch` 仅在提交时取写锁（`db.rs:1101`） | **公开 API 签名破坏性变更**，撞 `API-V1-FREEZE` |
| Kotlin | `BatchWriter internal constructor()`（`Storage.kt:204`），无 db 引用 | 中：内部构造器改签名，公开 API 不变 |
| Dart | `BatchWriter` 仅 `ops` 列表（`storage.dart:261`），无引用 | 中：同 Kotlin |

- **前置条件**：Rust 侧需要冻结政策的 v1.1 修订案（闭包签名演进 `FnOnce(&mut Batch)` → `FnOnce(&mut BatchWriter<'_>)`），Kotlin/Dart 属内部重构，先取近乎零成本的 Go/.NET 亦无妨，但五语言签名必须同批对齐。
- **读点语义（必须先定，否则并发下不可解）**：`batch()` 闭包在无锁运行，主库可能被另一线程并发写。建议语义："staging 命中优先（墓碑→未命中）；未命中时读**进入 batch() 时刻的主状态借用**"。Rust 实现上即入口处做一次 O(#ns) 的外层浅克隆（同 `snapshot()`，`db.rs:498-503`），成本可忽略，且与快照语义完全同源，跨语言解说一致。

### 4.6 改进 8（压缩）：选型改为 Deflate 优先，并预置互通性决策

- **五语言标准库支持矩阵**（决定单选谁）：

| 语言 | std 自带 Deflate | std 自带 LZ4 |
| :--- | :---: | :---: |
| Rust | ❌（无，纯 Rust crate 属外部依赖，非 native lib） | ❌（同左） |
| Go | ✅ `compress/flate` | ❌ |
| .NET | ✅ `DeflateStream`（netstandard2.0 ✅，Unity ✅） | ❌ |
| Kotlin | ✅ `java.util.zip.Deflater` | ❌ |
| Dart | ✅ `dart:io` zlib/deflate | ❌ |

- **结论**：全语言零依赖可用的交集只有 Deflate。v2.0 一期只做 `0x01=Deflate`；LZ4 全槽位无 std 支撑，延后或与"允许外部 crate"的红线一起评审（Rust `lz4_flex` 等纯实现可以另行讨论，但五个槽位要保持两两互通需五份实现或五份依赖，性价比低）。
- **互通性风险（必须先决策再写码）**：压缩后磁盘帧字节不再跨语言逐字节一致，现行黄金测试向量（锁定帧字节）需要拆为两层黄金向量：格式级（未压缩向量不变）+ 压缩级（逐语言 roundtrip + 已知明文样本）。压缩参数不指定跨语言统一级别，仅要求解码互通与上界校验。上界保护仍需照原方案保留（解压后尺寸 > `MAX_PAYLOAD_LEN` 即拒）。

### 4.7 原报告自身的数据修正

**§1.2 统计修正**（脚本重算）：

| 语言 | 原报告 | 实测 | 备注 |
| :--- | :--- | :--- | :--- |
| Rust | 8 / 3,230 / 23 / 3,531 | 相符 | — |
| Go | 9 / 3,088 / 18 / 4,077 | 相符 | — |
| **.NET** | 9 / **3,754** / 19 / 3,853 | 9 / **3,207** / 19 / 3,853 | 源码 LOC 偏差 17%；测源比应为 **1.20:1** 而非 1.03:1（统计口径若含 `obj/` 生成文件也不自洽：那只有 3,303） |
| Dart | 11 / 2,178 / 11 / 1,520 | 相符 | — |
| Kotlin | 11 / 2,039 / 11 / 1,537 | 相符 | — |

**性能数据口径**：原报告 §1.3 引"Go 强持久化 1,029 ops/s（平均 0.96ms）"，与同日性能报告的 Go WAL+Immediate 464 ops/s（2.15ms）矛盾且无方法论（ops 数、warm-up、磁盘状态、引擎模式均未记录）。两份数据必须同源或注明环境差异，否则基准不可引用。另原报告 §1.3 把"WAL 增量写吞吐 513~2,558 ops/s"列为 Deferred 模式数据，需补注其含义（性能报告口径为单写逐笔追加，见 §4.1 表）。

**行号引述对照**（HEAD `b79b62b`）：

| 原报告引用 | 实际位置 | 漂移 |
| :--- | :--- | :---: |
| `db.rs:90`（data 声明） | `db.rs:90` | ✅ 精确 |
| `db.rs:528`（`Arc::make_mut`） | `db.rs:528` | ✅ 精确 |
| `db.rs:1650-1662`（append_entries） | `db.rs:1964-2028` | ≈300 行 |
| `db.rs:1880-1925`（compact_internal） | `Compact()` `db.rs:1258`；`commit_flush` `db.rs:2225` | ≈300 行 |
| `db.go:1701-1715`（appendEntries） | `db.go:1981` | ≈280 行 |
| `db.go:1630-1675`（compactInternal） | `Compact()` `db.go:1443`；`commitFlush` ≈`db.go:1797` | ≈200 行 |
| `Db.cs:976-1022`（AtomicReplace） | `Db.cs:1194`（WithRetry :1223） | ≈220 行 |
| `Db.cs:1900-1950`（BatchWriter） | `Db.cs:2418-2517` | ≈500 行 |
| `Storage.kt:19`（TreeMap） | **该类型不存在**（Kotlin 为 ArrayList+二分，`Storage.kt:15-25`） | ❌ 虚构 |

建议：文档正文引用行号时附注"行号随主版本漂移，以符号名为准"，或直接以函数/符号名引注。

---

## 5. 修订后的实施路线图（v1.1-rev）

```
P0（零 API 破坏，可立即开工，收益确定。标签前缀 R- 以免与既有决策编号 C#/D# 混淆）
├─ RA1 目录 fsync 补齐（改进1-rev，§4.3 方案）：.NET 托管验证→降级；Kotlin best-effort；Dart no-op+文档
├─ RA2 自适应压实（改进3 + §4.2 细化）：livePayloadSize O(1) 增量账本，并入现有退避体系
├─ RA3 Dart 默认值对齐（新缺口13）：lockTimeout 5000→10000、syncInterval 50→100、compaction 阈值 0→4MB（补 C8/C19 欠账）
└─ RA4 SnapshotAtomic+Immediate 选型指引（新缺口12，docs-only）：README/规范前置选型表，改默认值另立 ADR

P1（防退化基建，与现有测试体系无缝）
└─ RB1 VFS 故障注入（改进5）：注入层走内部接线，不进公开 API；与 golden/FaultInjectionTest 合并演进

P2（需先过政策闸门）
├─ RC1 批内读已写（改进2，§4.5）：Frozen API v1.1 修订案 → 五语言同批对齐 → 读点语义定稿
├─ RC2 WAL 固定开销归因（X-1，§4.1）：实验分支定位 ~1.9ms/写根因（锁/句柄/precheck/AV）
└─ RC3 内存预算（改进4）：与 RA2 共享计数器；反压语义与快照引用计数的关系单独立设计（防死锁）

阶段二 v2.0（保持原报告框架，附加闸门）
├─ RD1 组提交：闸门 = X-1 归因结论 + C15 重开决策单通过
├─ RD2 分段 WAL（改进7）：新增风险提示——多段文件下单文件 `.tmp→.bak→.db` 两代轮转语义、
│    C5 两代缺口防护与既有决策 D2（压实失败不回滚）的契约需按段重定义，设计先行评审再动工
└─ RD3 Deflate 块压缩（改进8-rev，§4.6）：黄金向量双层化方案先定稿

不做 / 维持原判
├─ C15 组提交：维持取消，直到 RD1 闸门的归因数据与重开决策单齐备
├─ 零 Native / 零 P/Invoke 红线：含目录 fsync 的 .NET 实现路径
└─ TTL、跨进程 MVCC 等：原报告 §4 既有结论不变
```

**What NOT to do 增补两条**（并入原报告 §4）：
1. **性能类改进必须在同机同负载下有可复现数据后才立项**，且新方案文档必须显式引用其继承或重开的决策编号（C8/C9/C15/C19/D1/D2…），禁止用跨引擎模式的数据交叉论证（如用 SnapshotAtomic+Deferred 的 68 万 ops/s 论证 WAL 模式的句柄成本）。
2. **平台能力缺口宁可"托管尝试 + 优雅降级 + onWarning"，不可引入 P/Invoke/FFI 补齐**——降级路径必须写进规范。

---

## 6. 对原报告的手术式修改清单（若选择直接修订原文件）

1. §2.1 缺口 1：删除"`TreeMap<ByteArray, InternalEntry>`"引述，改为"Kotlin：`NamespaceStore.items` 有序 ArrayList + 二分（`Storage.kt:15-25`）"；README"红黑树"表述同步修正；
2. §2.1 缺口 4：机理段改为"可写排他 + 只读共享（多读者互通），不支持一写多读与双写者"；
3. §2.2 缺口 8 第 2 条：补充"五语言当前均无死键比例感知"的准确前提（删除任何暗示部分语言连阈值压实都没有的旧印象）；
4. §3.1 改进 1：整段替换为 §4.3 修订版（.NET 弃 P/Invoke、Kotlin best-effort、Dart 声明不可行）；
5. §3.2 改进 2：加"前置：Rust API 冻结修订案 + 读点语义定义（§4.5）"；
6. §3.2 改进 3：补 O(1) 账本细化（§4.2）；
7. §3.2 改进 6（组提交）：加"闸门：X-1 归因 + C15 重开决策单"，删除或标注待验证的吞吐预估；
8. §3.2 改进 8：改"Deflate 优先 + 向量双层化"（§4.6）；
9. §1.2：修正 .NET 源码 LOC 与测源比；§1.3 为性能数据补方法论与环境注释；
10. 全文行号统一按 §4.7 对照表重校，并加"以符号名为准"注；
11. §3 路线图：用本评审 §5 的 v1.1-rev 替换，或并列引用；
12. 新增缺口章节：并入新缺口 12、13。

---

## 7. 评审中已核实、可直接复用的结论快照

- 五语言默认值矩阵、.NET 统计修正、BatchWriter 影响矩阵——均已在上文核实为可执行输入，后续设计文档直接引用本评审的证据行号即可，无需重复调查。
- 本次评审未运行任何跨语言测试套件；"332 passed"仅与 CHANGES-20261016 记录交叉验证（91+51+92+47+51=332 精确吻合），未独立复跑。
