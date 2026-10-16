# CKV WAL 缺陷核对结论与修复计划

> **编制时间**: 2026-10-16 16:31:20（**修订**: 2026-10-16 16:54:00，见 [§7 实施前复核修订](#7-实施前复核修订第二轮追加章节)；
>   **再修订**: 2026-10-16 17:38:10，见 [§8 第三轮复核修订](#8-第三轮复核修订2026-10-16-173810)）
> **核对基线**: `dev` @ `3219d9c`
> **核对对象**: `docs/CODE-REVIEW-20261016{a,b,c}.md` 三份审查报告
> **核对方式**: 逐文件逐行核对源码（Rust / Go / .NET / Dart / Kotlin 五语言），
>   对每条论断定位到具体 `file:line`；**本文档只做核对与规划，未改动任何代码**
> **核对范围**: 三份报告的全部 P0/P1 论断 + 影响修复方向的 P2 论断 + 报告间的互相矛盾之处
> **实施前必读**: §7 追加了 4 项必须修订（M1–M4）、5 项建议补充（G1–G5）与 4 项口径修订（S1–S4）。
>   其中 **M1（Dart / Kotlin 同样存在先删后轮转）** 与 **G1（WAL 模式下 `.bak` 也会被截断）**
>   为三份审查报告均未提出的新发现；**M3** 要求把 C13 的恢复策略决策提前到 C4 之前。
> **§8 追加（第三轮）**: §0–§7 的 P0 结论与 M1/M2/G1/G3/G5 复核通过；但 §4/§5 有 3 处口径已被
>   `DECISION` 的 D1/D2 推翻（已在正文加注作废），§7.8 的 `allowTailTruncation` 结论有误，
>   另新增 5 项实施项（Kotlin `.bak` 撕裂、分支 ③ 处理、readOnly 组合等）与 T15–T17 门槛。
>   **动手前须一并阅读 §8**。

---

## 0. 核对结论速览

三份报告**部分重叠、部分互相矛盾、部分结论有误**。经源码核对：

- **真实且严重的问题：4 类 P0**（自动压实丢写入、自动压实嵌套锁、Batch 清空僵尸复活、中间坏帧整库截断）；
- **真实的 P1/P2 问题：15 类**（锁协议、Kotlin 写路径、恢复校验、跨语言漂移等）；
- **不成立或存疑的论断：5 条**（见 §3），照单修复会做无用功甚至改错方向；
- 报告 a 的性能/文档核对基本可信，报告 b/c 的架构判断基本可信，但两处细节论断有误。

| 编号 | 问题 | 严重度 | a | b | c | 核对结论 |
| :-- | :-- | :-- | :-: | :-: | :-: | :-- |
| C1 | 自动压实发生在 `apply_mem` 之前，触发写丢失（Rust） | **P0** | ✅ | ✅ | ✅ | **属实** |
| C2 | 自动压实嵌套获取文件锁（Go/.NET/Dart），压实失效 | **P0** | ✅ | ✅ | ✅ | **属实**；Dart 默认阈值 0 需显式开启才触发 |
| C3 | Batch 内 Put+ClearNamespace，重启后键复活（4 语言） | **P0** | ❌ | ✅ | ✅ | **属实**（Kotlin 无此 API，不涉及） |
| C4 | 中间坏帧被当残尾，整库静默截断（5 语言） | **P0** | ❌ | ✅ | ✅ | **属实** |
| C5 | Rust `rename_with_retry` 先 `remove` 再 `rename`，破坏两代备份 | P1 | ❌ | ✅ | ✅ | **属实** |
| C6 | Kotlin Snapshot 写路径先改内存、后 `begin_flush` 校验 | P1 | 部分 | ❌ | 部分 | **属实**（且比报告 c 描述的更广） |
| C7 | Kotlin 跨进程冲突检测偏弱（只比 TxID） | P1 | ✅ | ❌ | ✅ | **属实** |
| C8 | Kotlin 无 `compactionThresholdBytes`、无自动压实 → WAL 无界增长 | P1 | ✅ | ❌ | ✅ | **属实** |
| C9 | 锁文件每次 fsync + 句柄即关 + release TOCTOU | P1/P2 | ✅ | ✅ | ✅ | **属实** |
| C10 | open 阶段残尾截断未持锁 | P1 | ❌ | ✅ | ✅ | **属实**（竞态窗口窄） |
| C11 | .NET 单例注册表用 `OrdinalIgnoreCase` + `GetFullPath` | P1 | ✅ | ✅ | ✅ | **属实**（仅 Linux/macOS 受影响） |
| C12 | 恢复时不校验定长 value 长度与保留类型 | P2 | ❌ | ✅ | ✅ | **属实**（Rust 已验证，其余同构） |
| C13 | WAL 模式下 `.bak` 回退会回滚压实后的写入 | P2 | ✅ | ❌ | ❌ | **属实**（语义隐患） |
| C14 | `compaction_dead_ratio` 是死代码 | P2 | ✅ | ✅ | ✅ | **属实** |
| C15 | 每次写开/关 `.db` 句柄，无组提交、无常驻句柄 | P2 | ✅ | ✅ | ✅ | **属实** |
| C16 | Deferred 后台线程持写锁；`close()` 最长等一个 interval | P2 | ✅ | ✅ | ✅ | **属实** |
| C17 | 重复 open 时新 options 被静默忽略 | P2 | ❌ | ❌ | ✅ | **属实** |
| C18 | POSIX 轮转后未 fsync 父目录 | P2 | ❌ | ❌ | ✅ | **属实** |
| C19 | 五语言默认值/API 漂移（见 §2.3） | P2 | ✅ | ❌ | ✅ | **部分属实**（详见下文修正） |
| C20 | README 语言矩阵陈旧（缺 Dart/Kotlin） | P2 | ❌ | ❌ | ✅ | **属实** |
| C21 | 三份性能/文档数据问题 | 文档 | ✅ | ✅ | — | **基本属实**（一处需更正） |

---

## 1. P0 —— 必须在发布前修复

### C1【P0·静默数据丢失】自动压实发生在内存应用之前（Rust / 其余语言的同源逻辑）

**位置**
- Rust `src/rust/src/db.rs:1720-1742`（`apply_mutation`：先 `append_entries`，后 `apply_mem`）
- Rust `src/rust/src/db.rs:1709-1714`（`append_entries` 尾部自动压实触发）
- Rust `src/rust/src/db.rs:1854-1865`（`commit_flush` 序列化的是**当前** `self.data`）

**机理**：`append_entries` 把本次增量帧追加进 `.db` 后，立即用 `commit_flush` 以**尚未包含本次写入**的 `self.data` 生成全量快照覆盖 `.db`，本次帧被覆盖丢失；随后 `apply_mem` 才写入内存，造成"内存有、磁盘无"。`Immediate` 下 `is_dirty=false`，`close()`/析构都不会补救。

**实测复现（报告 a/b/c 一致）**：小阈值下连续写，跨阈值那次写在 `reopen` 后消失，且无异常无告警。

**核对结论**：**属实**。`append_entries` 与 `apply_mem` 的先后顺序在源码中确凿。

---

### C2【P0·压实永不执行/每次写阻塞一个锁超时】自动压实嵌套获取文件锁（Go / .NET / Dart）

**位置与持锁点**

| 语言 | 外层持锁 | 内部再次取锁 |
| :-- | :-- | :-- |
| Go | `src/go/db.go:1694` `defer lock.Release()` | `src/go/db.go:1765-1770` `db.beginFlush()` |
| .NET | `src/dotnet/src/Ckv/Db.cs:1083` / `:1184-1187` `finally { lockHandle.Dispose(); }` | `src/dotnet/src/Ckv/Db.cs:1178-1182` `BeginFlush(...)` |
| Dart | `src/dart/lib/src/db.dart:358` / `:419-421` `finally { lock.release(); }` | `src/dart/lib/src/db.dart:407-413` `_beginFlush()` |

**核对结论**：**属实**，三语言结构一致。`appendEntries` 在函数结束前（`defer` / `finally`）一直持有 `.db.lock`，随后在同一函数内再次获取同一把锁：

- **Go**：`AcquireLock` 用 `O_CREATE|O_EXCL`（`src/go/lock.go:47`），必失败 → 退避到 `LockTimeoutMs` 后返回 `LockedError`；而 `appendEntries` 用 `if err == nil { _ = db.commitFlush(...) }` **静默吞掉**（`db.go:1767-1769`）。→ 越过阈值后**每次写阻塞约 10s（默认）**，压实一次都不成功，文件只增不减，`.tmp` 残留。
- **.NET**：`LockFile.Acquire` 用 `FileMode.CreateNew`（`LockFile.cs:36`），失败后 `catch (IOException)` 判僵死 → 未超时则等待，直到 `LockTimeoutMs` 抛 `LockedError`（`LockFile.cs:111-116`）。该异常在 `finally` 释放外层锁之前抛出 → **每次越线写先卡 10s 再抛错**。
- **Dart**：`lock.dart:13-41` 用 `FileMode.write`（会截断重建）+ `lockSync`，同一文件嵌套 `lockSync` 在 Windows 上按 handle 互斥，将阻塞/失败。→ 注意 **Dart `compactionThresholdBytes` 默认值为 0（`src/dart/lib/src/options.dart:31`）**，故默认不触发；用户显式开启后才会命中。

> 说明：报告 a 称 .NET 抛 `UnauthorizedAccessException`。按当前源码，嵌套 `BeginFlush` 耗时到超时后应抛 `LockedError`（`UnauthorizedAccessException` 是后续"句柄未关导致轮转 rename 失败"的典型症状）。修复时以"嵌套取锁必失败"这一根因为准即可，**不必纠结异常类型**。

---

### C3【P0·重启后已删数据复活】Batch 内 `ClearNamespace` 不记录本批新增键的墓碑（Rust / Go / .NET / Dart）

**位置（构建 WAL 条目阶段遍历的是"尚未应用本批"的内存状态）**
- Rust `src/rust/src/db.rs:1016-1028`（`BatchOp::ClearNamespace` 遍历 `inner.data`）
- .NET `src/dotnet/src/Ckv/Db.cs:1928-1936`（遍历 `_namespaces`）
- Go `src/go/db.go:1236-1247`（遍历 `db.namespaces`）
- Dart `src/dart/lib/src/db.dart:955-970`（遍历 `_namespaces`）

**机理**：`batch { Put(ns,k); ClearNamespace(ns); }` 时，`ClearNamespace` 遍历的是**BATCH 之前的**内存，只对旧键生成 `OP_DELETE`，**没有**为本批刚 `Put` 的 `k` 生成删除条目。磁盘帧序列为 `[OP_PUT(k)]`（可能夹杂旧键删除），内存侧因为随后真的 `clear` 了所以当下读为空。**重启重放**时 `OP_PUT(k)` 被应用且无后续删除 → `k` 复活。

**核对结论**：**属实**（Rust/Go/.NET/Dart 四处代码形态完全一致）。Kotlin 的 `BatchWriter` 根本没有 `clearNamespace`（`src/kotlin/src/main/kotlin/ckv/Storage.kt:204-266`），因此**不涉及**；报告 b 的跨语言矩阵把 Kotlin 也标为"复活 Bug"是**不准确**的。

---

### C4【P0·整库静默截断】中间坏帧被当作断电残尾（全部 5 个实现）

**位置**
- Rust `src/rust/src/format.rs:275-420`（`load_with_recovery`），任一坏帧分支均 `file.set_len(offset); break;`
- Go `src/go/format.go:275-395`
- Kotlin `src/kotlin/src/main/kotlin/ckv/Format.kt:174-250` + `src/kotlin/src/main/kotlin/ckv/CkvDb.kt:104-113`
- Dart `src/dart/lib/src/format.dart:167-315` + `src/dart/lib/src/db.dart:114-120`
- .NET `src/dotnet/src/Ckv/Db.cs:491-624` + `:363`

**机理**：文件严格只追加，因此"坏帧之后还有合法帧"**只可能来自介质位翻转**（中间损坏）。当前实现遇到**第一个**坏帧（magic / 帧头 CRC / TxID 非单调 / OpCount / payload 长度 / payload CRC / entry 解码失败）就无条件 `truncate` 到该偏移并 `break`，**不做向后扫描**，于是坏帧之后的全部合法数据被一次性丢弃，且因为返回成功而**不触发 `.bak` 恢复**。

**核对结论**：**属实**。Rust 的 `scan_max_tx_id`（`format.rs:426-454`）同样遇到首个坏帧即整体报错，无定位能力。

---

## 2. P1 / P2 —— 真实但非"立刻丢数据级"的问题

### 2.1 锁与文件轮转

- **C5（P1）Rust 轮转先删后 rename**：`src/rust/src/db.rs:1957-1987`。`rename_with_retry` 在每次尝试前 `fs::remove_file(dst)`。POSIX 与 Windows（Rust 用 `MOVEFILE_REPLACE_EXISTING`）的 `rename` 本就覆盖目标，删除多余，且制造"`db→bak` 轮转期间 `.bak` 不存在"的两代窗口（copy 兜底路径 `:1979-1985` 同样先删后拷）。.NET 已改 `AtomicReplace`（`Db.cs:976-1022`），Go 用 `os.Rename`，**只有 Rust 未对齐**。
- **C6（P1）Kotlin Snapshot 写路径先改内存再校验**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:355-371`（`applyMutation` 的 Snapshot 分支是 `applyMem()` → `beginFlush()` → `commitFlush()`），`:484-495`（`incrementInt64`），`:538-549`（`dropNamespace`），`:593-624`（`batch`）。其余四语言均为 `begin_flush`（含磁盘预检，可能抛 `ConcurrentModification`/`Locked`）→ `apply_mem` → `commit_flush`。Kotlin 一旦 `beginFlush` 抛错，内存已改、磁盘未落 → 内存/磁盘分叉。**报告 c 只点了 `increment`，实际是全部 Snapshot 写路径。**
- **C7（P1）Kotlin 冲突检测偏弱**：`CkvDb.kt:288-303` 仅判断 `diskMaxTxId > currentMaxTxId`；Rust `db.rs:1818-1829`、Go `db.go:1578-1587`、.NET `Db.cs:802-809` 均比对 `(maxTxId, fileSize)` 与 `lastKnownDiskState`。外部改动导致磁盘 TxID **不高于**本进程内存 TxID（如外部回滚/替换为较小文件）时，Kotlin 静默继续写。
- **C8（P1）Kotlin 无自动压实**：`Options.kt:14-23` 无 `compactionThresholdBytes` 字段；`CkvDb.kt:283-353` 的 `appendEntries` 无阈值分支。WAL 只增不减，需人工 `compact()`。
- **C9（P1/P2）锁协议三点**：①每次 `acquire` 都对锁文件 `fsync`（Rust `lock.rs:63`、Go `lock.go:50`、.NET `LockFile.cs:45`、Dart `lock.dart:26`）——锁内容只需"原子创建 + 原子可见"，本不需 fsync，实测占写延迟大头（报告 a 测得单次约 1285µs）；②锁句柄写完即关（`lock.rs:61-68`），"持锁者存活则重命名失败"这一活性判据失效，只剩时间戳，长停顿会被误判僵死而窃取；③`release` 存在 TOCTOU（`lock.rs:145-163`：读 UUID 与 `remove_file` 之间锁可能被窃取重建，从而误删新主人的锁），.NET/Go 同构。
- **C10（P1）open 阶段截断未持锁**：`init_and_recover`（`db.rs:1544-1605`）→ `load_file` → `load_with_recovery` 的 `set_len` 全程未取 `FileLock`。两进程同时打开时，A 的截断可能截掉 B 刚追加的帧。窗口窄，但真实。
- **C11（P1）.NET 单例注册表跨平台错误**：`Db.cs:77-78` 用 `StringComparer.OrdinalIgnoreCase`；`:279` 用 `Path.GetFullPath`（不解析软链）。在 Linux/macOS 上 `/data/Foo.db` 与 `/data/foo.db` 会被错误合并为同一实例；Rust 用 `canonicalize_path`（含符号链接解析）。

### 2.2 恢复与格式

- **C12（P2）恢复不校验定长类型长度与保留类型**：`RawOpEntry::decode`（`format.rs:72-101`）只按长度切分，不校验 `value_type`；`apply_entries`（`db.rs:1750-1777`）只拒绝未知 `op_code`，不校验 `int64` 必须 8B、`bool` 必须 1B、`value_type>0x04` 的保留值。非法文件可通过 CRC 进入内存，错误延迟到读取该键时才暴露。
- **C13（P2）WAL 模式下 `.bak` 的语义隐患**：WAL 自动压实会把膨胀日志轮转为 `.bak`。`init_and_recover`（`db.rs:1567-1599`）在主库损坏时回退 `.bak`，而 `.bak` 是**压实前**的旧状态 → 会把压实后写入的数据整体回滚，仅发一条 `onRecoveredFromBackup`。Snapshot 模式下 `.bak` 是上一代快照（预期语义），WAL 模式下需要显式文档化并重新审视。

### 2.3 性能与跨语言一致性

- **C14（P2）`compaction_dead_ratio` 是死代码**：仅在 `options.rs:62,76` / `options.go:14,27` / `Options.cs:30,50` 声明，**无任何引擎代码读取**；Dart/Kotlin 连声明都没有。
- **C15（P2）无常驻句柄、无组提交**：`db.rs:1650-1662`、`db.go:1701-1705`、`Db.cs:1087`、`db.dart:360-362`、`CkvDb.kt:307-318` 每写都 open/close 一次 `.db`；无 group commit。
- **C16（P2）Deferred 后台线程持写锁**：Rust `db.rs:227-263`，后台线程以 `write()` 锁执行 Snapshot 全量序列化 + fsync，阻塞读者；`close()`（`db.rs:270-305`）需 `join`，最长等一个 `sync_interval_ms`。
- **C17（P2）重复 open 静默忽略 options**：Rust `db.rs:182-192`、.NET `Db.cs:279-308` 命中已有单例即直接返回，新的 `readOnly/syncMode/threshold/lockTimeout` 全部静默失效。
- **C18（P2）POSIX 缺父目录 fsync**：`commit_flush` 轮转后未 fsync 父目录，断电后 rename 可能丢失（Windows 可跳过，Linux/macOS 应补）。
- **C19（P2）跨语言漂移**（已逐项核对）：

  | 维度 | Rust | Go | .NET | Kotlin | Dart |
  | :-- | :-- | :-- | :-- | :-- | :-- |
  | `sync_interval_ms` 默认 | 100 | 100 | 100 | 100 | **50** |
  | `compaction_threshold` 默认 | 4MB | 4MB | 4MB | **无此选项** | **0（关闭）** |
  | `lock_timeout` 默认 | 10000 | 10000 | 10000 | **5000** | **5000** |
  | 自动压实 | 有（C1） | 有（C2） | 有（C2） | 无 | 有（默认关） |
  | 空 key | 允许 | 允许 | 允许 | **拒绝**（`CkvDb.kt:864`/`Storage.kt:272`） | 允许 |
  | 冲突检测语义 | 元组 | 元组 | 元组 | **仅 TxID** | 元组 |
  | 错误命名 | `KeyTooLarge`/`ConcurrentModification` | 同 Rust | 同 Rust | **`KeyTooLongError`/`TransactionConflictError`** | 接近 Rust |
  | 锁协议 | 32B 统一格式 | 32B 统一格式 | 32B 统一格式 | **内核文件锁**（无 32B、无僵死判定） | **内核文件锁 + 文本内容** |
  | `syncOnWrite` | 无 | 无 | **有**（`Options.cs:33`） | 无 | 有 |
  | `clear(ns)` 语义 | 清空保留 ns | 清空保留 ns | 清空保留 ns | **= dropNamespace**（`CkvDb.kt:554-556`） | 清空保留 ns |

  > **修正报告 a**：报告 a §4.3 称 `syncOnWrite` 为 "Dart 独有"，**不实**——.NET 也有该选项。其余默认值/命名漂移**属实**。
- **C20（P2）README 陈旧**：`README.md:34-41` 语言矩阵仍列 Python/Node.js/Java "规划中"，未列实际存在的 Dart/Kotlin。
- **C21（文档）**：报告 a 指出的"默认值 50ms 是基准配置而非默认值""WAF 为公式推算非实测""Compaction 回读验证不存在""`as_micros()` 截断致 P50/P99 归零"**均可在源码中核实**（`src/rust/tests/sync_wal_matrix_bench.rs:32,68-83`）。唯 `syncOnWrite` 一处需更正（见 C19）。

---

## 3. 报告中有误 / 存疑 / 需澄清的论断（避免误修）

1. **【不成立】报告 c："Kotlin `increment` 用普通 `+` 有溢出 panic 风险"** —— Kotlin 的 `Long` 加法是**静默回绕**（同 Java），不会 panic。`CkvDb.kt:480` 的 `current + delta` 与 Rust 的 `wrapping_add` 行为一致。**无需修改**。
2. **【不成立】报告 a：`syncOnWrite` 为 Dart 独有** —— .NET `Options.cs:33` 同样存在并被 `Db.cs:948,1301` 使用（见 C19）。
3. **【不准确】报告 b：Kotlin 存在 Batch Clear 键复活 Bug** —— Kotlin `BatchWriter` 没有 `clearNamespace`，不存在该路径（见 C3）。
4. **【存疑，不作为缺陷】报告 b M4："对刚打开未写入的 handle 调 fsync 无法刷新其他句柄的脏页"** —— `fsync`/`FlushFileBuffers` 作用于**文件（inode）级别**的脏页，与句柄是否写过无关。此条缺乏依据，建议先写一个最小实验证伪，**不要据此改动刷盘逻辑**。
5. **【需澄清】报告 a：Rust"提前释放锁破坏排他保护区间"** —— Rust 确实在 `db.rs:1707` 提前 `lock.release()` 后再取锁压实，但这**不是**数据丢失主因（主因是 C1）；Rust **不存在** C2 的嵌套锁死锁。修复时应把精力放在"压实移到 `apply_mem` 之后、且在独立锁周期内执行"，而非单纯重排锁的释放时机。

---

## 4. 修复计划

### 阶段 0：先立回归测试（所有修复的前置）

现有测试**零覆盖**自动压实路径（`grep compaction_threshold` 在测试目录 0 命中），这是三个 P0 漏网的直接原因。先补以下测试（先红后绿）：

| 编号 | 测试 | 覆盖 |
| :-- | :-- | :-- |
| T1 | 小阈值连续写跨阈值，**重开**后遍历全量键（不只看最后一个） | C1 |
| T2 | 阈值设为小值，断言写耗时不超过正常量级、且文件确实缩小 | C2 |
| T3 | `batch { Put; ClearNamespace }` → 重开 → 断言键不存在 | C3 |
| T4 | 健康文件**翻转中间帧** 1 字节（后面仍有合法帧）→ 期望 `Corrupted` 拒开，而非截断 | C4 |
| T5 | 健康文件**截掉尾部**若干字节 → 期望截断自愈且保留其余键 | C4 |
| T6 | 【口径已作废，见 §8.1】压实失败按 D2 验收：注入故障后断言 `onCompactionFailed` 告警 + `needs_compaction` 置位 + 退避重试生效，本次写**返回成功** | C2 |
| T7 | 压实/轮转后目录无残留 `.tmp`，多次触发不堆积 | C2/C5 |
| T8 | Linux 上大小写不同路径分别打开得到不同单例 | C11 |

### 阶段 1：修复 P0

**Fix C1 + C2（统一重构自动压实，五语言同构）**

原则：**禁止在 `append_entries` 内部触发压实**；压实必须在 `apply_mem` 之后、且在**独立的锁获取周期**（追加句柄已关、外层锁已释放）内执行。

```
apply_mutation(entries, apply_mem), WAL 分支改为：
    new_size = append_entries(entries)   // 只追加，返回新文件大小，不压实
    apply_mem(self)                      // 本次写入先进内存
    if threshold > 0 && new_size >= threshold:
        // 独立锁周期：begin_flush -> commit_flush，快照必然包含本次写入
        compact_now()                    // 【已作废，见 §8.1】"错误必须上抛" → 按 D2：告警 + 退避重试，本次写返回成功
```

- Rust `db.rs:1709-1714` 删除内嵌压实，`apply_entries` 改为返回文件大小；`apply_mutation`（`:1729-1731`）在 `apply_mem` 后调用压实。
- Go `db.go:1765-1770` 移到 `applyMutation` 的 `applyMem()` 之后；**去掉 `if err == nil` 静默吞错**，压实失败必须返回给调用方。
- .NET `Db.cs:1178-1182` 移到 `ApplyMutation` 的 `applyMem()` 之后（此时外层 `AppendEntries` 的锁与句柄均已释放）。
- Dart `db.dart:407-413` 同构调整（并把 `_appendEntries` 内的 `raf` 提前关闭逻辑清掉，避免 double-close）。
- Kotlin 若补自动压实（见 C8），直接按此顺序实现，避免重蹈。
- **验收**：T1/T2/T6/T7。

**Fix C3（Batch 清空语义）**

在构建 WAL 条目时维护"批内影子状态"，使 `ClearNamespace` 能覆盖**本批此前 `Put` 的键**：

- 方案 A（推荐，改动小）：遍历 `batch.ops` 时维护一个 `pending_keys: HashMap<ns, Set<key>>`；遇到 `ClearNamespace(ns)` 时，对"内存已有键 ∪ 本批已 Put 的同 ns 键"全部生成 `OP_DELETE`（或丢弃此前已收集的同 ns `OP_PUT`，避免写无用帧）。
- 方案 B（更彻底）：引入原生 `OP_CLEAR_NAMESPACE` 物理操作码，但需先改 `KV-DB-DESIGN-SPEC.md` 帧格式与跨语言解析；**不建议本轮做**。
- 四语言（Rust/Go/.NET/Dart）同步修改；Kotlin 无此 API，仅在文档中说明。
- **验收**：T3。

**Fix C4（中间坏帧防御）**

在 `allow_tail_truncation` 的截断分支前，实现"病因鉴别"：

```
坏帧（magic / 帧头 CRC / TxID 单调 / 长度 / payload CRC / entry 解码失败）：
  从 offset+1 起按帧边界扫描 FrameMagic，对候选做帧头 CRC + payload CRC + entry 解码校验
    ├─ 找到任一合法后续帧 → 中间损坏 → 抛 Corrupted（禁止截断）
    │    【第三轮复核更正，见 §8.1】"触发 .bak 恢复"在 D1 落地后默认不再成立
    └─ 扫描到文件末尾仍无合法帧 → 断电残尾 → 截断到 offset，并上报截断了多少字节
```

- 五语言统一实现（Rust `format.rs`、Go `format.go`、Kotlin `Format.kt`、Dart `format.dart`、.NET `Db.cs`）。
- 截断仍需在 `FileLock` 内进行（并入 C10）。
- **验收**：T4/T5。

### 阶段 2：修复 P1

1. **C5**：Rust `rename_with_retry` 去掉 `remove_file(dst)`，直接 `fs::rename`（覆盖），失败重试；copy 兜底改为"先写 `dst.tmp` → 校验 → 覆盖"。
2. **C6**：Kotlin 全部 Snapshot 写路径改为 `beginFlush()` → `applyMem()` → `commitFlush()`，与其余四语言一致（`applyMutation`、`incrementInt64`、`dropNamespace`、`batch`）。
3. **C7**：Kotlin 冲突检测升级为比对 `(diskMaxTxId, diskFileSize)` 与 `lastKnownDisk*`。
4. **C8**：Kotlin `Options.kt` 补 `compactionThresholdBytes`（默认 4MB）与自动压实（按阶段 1 的顺序）。
5. **C9**：①`acquire` 去掉锁文件 `fsync`；②POSIX 保持 fd + `fcntl/flock`、Windows 保持句柄（崩溃自动释放）；③`release` 基于 fd/唯一 rename，消除 TOCTOU；④`steal` 前加 PID 活性探测作为第三判据。
6. **C10**：WAL 打开的残尾截断纳入 `FileLock`（获取锁 → 重读尾部 → 截断 → 释放）。
7. **C11**：.NET 路径比较器平台化（Windows `OrdinalIgnoreCase`，其余 `Ordinal`），并用 realpath 等价逻辑规范化（可 `File.ResolveLinkTarget`）。
8. **C13**：文档明确 WAL 模式下 `.bak` 为"压实前状态"，回滚风险显式告知；评估是否在 WAL 模式下采用不同恢复策略。

### 阶段 3：P2 与一致性

9. **C12**：恢复时校验 `value_type` 与定长长度（`int64=8B`/`bool=1B`/`float64=8B`），`value_type>0x04` 报错。
10. **C14**：`compaction_dead_ratio` 要么实现（维护 `dead_bytes`，双条件触发 `file_size≥threshold && dead_bytes/file_size≥ratio`），要么删除该选项并更新文档。
11. **C15**：评估常驻追加句柄 + 进程内 mutex；引入 group commit（可在阶段 1 之后单独立项，避免与 P0 修复耦合）。
12. **C16**：后台刷盘改为不长时间持写锁（快照视图 + 短锁落盘）；文档强调"应显式 `close()`"。
13. **C17**：统一"重复 open 以首个选项为准"，关键选项冲突时返回 warning/错误。
14. **C18**：Rust/Go 轮转后 fsync 父目录（仅 Unix），失败降级 warning。
15. **C19**：统一默认值矩阵；Kotlin 空 key / `clear` / 错误命名对齐（或文档化）；Dart 锁协议二选一（对齐 32B 或明确文档化并补齐 force/timeout 语义）。**注意 C19 的 `syncOnWrite` 项按 §3 修正**。
16. **C20**：更新 `README.md` 语言矩阵（补 Dart/Kotlin，移除已不准确的"Java 规划中"或改述）。
17. **C21**：修正性能报告与设计文档中的数据/归因（默认 50ms、WAF 改为真实测量、删除不存在的"Compaction 回读验证"、计时改纳秒）。

---

## 5. 验收门槛

- 五语言全量测试通过，且 **T1–T17 全部为绿**（T9–T14 见 §7.4；T15–T17 见 §8.3）；
- `grep -r "compaction_threshold\|CompactionThreshold\|compactionThreshold"` 在测试目录**非空**（确保自动压实路径被真实驱动）；
- 凡触发自动压实的写路径：重开后**全量键**与写前一致；
- 越过阈值后的单次写耗时不超过"一次 fsync"量级（不再出现一个锁超时的阻塞）；
- `batch { Put; ClearNamespace }` 重开后键不存在；
- 中间坏帧文件被拒开（`Corrupted`）而非静默截断；
- 五语言同一操作序列产出的字节一致（golden 测试）。

---

## 6. 复查记录

- 本文档所有 `file:line` 均基于 `3219d9c` 实读，未修改任何生产代码，未执行 `git commit` / `push`。
- 与 `docs/CODE-REVIEW-20261016{a,b,c}.md` 的差异集中在 §3（5 条误判/存疑），修复时以本文档为准。

---

## 7. 实施前复核修订（第二轮，追加章节）

> **复核时间**: 2026-10-16 16:54:00
> **复核对象**: 本文档 §0–§5 的全部 `file:line` 与结论
> **复核方式**: 逐条回到 `3219d9c` 源码实读；Rust 测试数用 `cargo test -- --list` 实测，
>   Go / .NET / Kotlin / Dart 用测试属性计数实测
> **复核结论**: **可以对照实施**，但需先落实 §7.3 的 4 项必须修订与 §7.4 的 5 项建议补充。
>   P0 四条的根因、定位、修复方向全部属实；阶段划分与验收门槛总体合理。
> **基线一致**: 复核时 `git status` 仅有文档改动，无源码改动；测试目录
>   `compaction_threshold` 命中数仍为 0（阶段 0 的前提成立）。

### 7.1 抽样复核通过清单

| 编号 | 复核证据（实读行号） | 判定 |
| :-- | :-- | :-- |
| C1 | `db.rs:1709-1714` 压实内嵌于 `append_entries`；`apply_mem` 在 `db.rs:1731` | 属实 |
| C2 | Go `db.go:1694` `defer` + `:1766` 嵌套；.NET `Db.cs:1083/1180/1186`；Dart `db.dart:358/410/420`；`lock.go:47` `O_CREATE\|O_EXCL` | 属实 |
| C3 | Rust `db.rs:1016-1028`、Go `db.go:1236-1247`、.NET `Db.cs:1928-1936`、Dart `db.dart:955-970` —— 四处行号与代码形态**精确吻合** | 属实 |
| C4 | `format.rs:317-337` 等 7 处 `set_len` 无条件截断后 `break` | 属实 |
| C5 | `db.rs:1960-1962` 每次尝试前 `remove_file(dst)` | 属实（但范围有误，见 M1） |
| C6 | Kotlin `CkvDb.kt:361-365` / `:488-491` / `:542-545` / `:591+` 四处 Snapshot 路径均为 `applyMem → beginFlush → commitFlush` | 属实 |
| C7 | `CkvDb.kt:299-303` 仅比对 `diskMaxTxId > currentMaxTxId` | 属实 |
| C8 | `Options.kt:14-23` 无 `compactionThresholdBytes`；`CkvDb.kt:283-353` 无阈值分支 | 属实 |
| C9 | `lock.rs:63` fsync、`:61-68` 句柄即关、`:145-163` 释放 TOCTOU；`lock.go:47-51` 同构 | 属实 |
| C11 | `Db.cs:78` `OrdinalIgnoreCase`、`:279` `Path.GetFullPath` | 属实 |
| C14 | `options.rs:62,76` / `options.go:14,27` / `Options.cs:30,50` 仅声明，全工程无读取点 | 属实 |
| C16 | `db.rs:227-261` 后台线程持 `write()` 锁做全量序列化 + fsync | 属实 |
| C19 | Dart `options.dart:29/31/32` = 50/0/5000；Kotlin `Options.kt:16/20` = 5000/100；Rust `options.rs:73/75` = 100/4MB | 属实 |
| C21 | `sync_wal_matrix_bench.rs:32` 写死 `sync_interval_ms(50)`、`:47` `as_micros()` 整数截断 | 属实 |

### 7.2 测试基线实测（供 C21 与 §5 验收门槛引用）

| 语言 | 实测命令/计数 | 实测值 | 文档宣称 | 判定 |
| :-- | :-- | :-: | :-: | :-- |
| Rust | `cargo test -- --list` | 64 个 `#[test]` + 2 个 doc-test = **66** | 66（报告 a） | 属实 |
| Rust | 同上 | 66 | 249（报告 c） | **报告 c 有误** |
| Go | `rg -c "^func Test"` | **49** | 49 | 属实 |
| .NET | `rg -c "\[Fact\]\|\[Theory\]"` | **67** | 67 | 属实 |
| Kotlin | `rg -c "@Test"` | 18 | 未宣称 | — |
| Dart | `rg -c "^\s*test\("` | 17 | 未宣称 | — |

### 7.3 必须修订项（实施前逐条落实）

**M1. C5 的范围错误：不只是 Rust。**

| 语言 | 轮转实现 | 判定 |
| :-- | :-- | :-- |
| Rust | `db.rs:1960-1962` `remove_file(dst)` → `fs::rename` | ❌ 有两代窗口 |
| **Dart** | `db.dart:325-336` `_atomicReplace`：`dstFile.deleteSync()` → `renameSync` | ❌ **与 Rust 同构，计划漏列** |
| **Kotlin** | `CkvDb.kt:210-216`：`ATOMIC_MOVE` 失败兜底里 `dbFile.delete()` → `renameTo` | ❌ **兜底路径有窗口** |
| Go | `db.go:1841-1856` 纯 `os.Rename`，无预删 | ✅ 正确 |
| .NET | `Db.cs:976-998` `AtomicReplace` + `:1005+` `AtomicReplaceWithRetry` | ✅ 正确 |

> §2.1 C5 与 §4 阶段 2 第 1 条中的"**只有 Rust 未对齐**"应改为"**Rust / Dart / Kotlin 三语言未对齐**"。

**M2. C4 的判定算法按 §4 现有伪码不可实现。**

§4 的"从 `offset+1` 起**按帧边界**扫描 FrameMagic"是循环论证——坏帧之后的帧边界未知。修正后的算法：

```
坏帧（magic / 帧头 CRC / TxID 单调 / OpCount / 长度 / payload CRC / entry 解码）：
  从 offset+1 起**逐字节**搜索 4B FrameMagic 候选位，对每个候选做完整校验：
    ① 帧头 CRC 通过
    ② op_count ∈ [1, MAX_OPS_PER_FRAME]，payload_len ≤ MAX_PAYLOAD_LEN
    ③ payload CRC 通过
    ④ entry 全量解码成功
    ⑤ **tx_id > last_valid_tx_id**（单调性约束，防止 payload 内偶然出现的 magic 造成误判）
  ├─ 任一候选全部通过 → 中间损坏 → 抛 Corrupted（禁止截断；默认不自动回退，见 §8.1）
  └─ 扫描到文件末尾仍无合法候选 → 断电残尾 → 截断到 offset，并上报截断字节数
```

补充约束：

- 扫描最坏情况为 `O(file_size)`，仅在**已判定损坏**的路径上执行，不影响正常打开性能；
- **Snapshot 模式 `allow_tail_truncation=false`，本次修复不得改变其既有行为**（本就抛 `Corrupted`）；
- 扫描需设置候选数量上限，避免极端情况下 CPU 长时间占用。

**M3. C4 与 C13 存在未决策耦合，必须在动手改 C4 之前先定策略。**

WAL 模式下抛 `Corrupted` 会直接命中 `.bak` 自动回退（`db.rs:1567-1583`、`go db.go:1382-1398`、`Db.cs:369-393`、`CkvDb.kt:117-124`、`db.dart:128-136`），而 WAL 的 `.bak` 是**压实前的文件**。结果是：C4 把"静默截断整库"换成了"静默回滚掉压实之后的全部写入"——**P0 并未真正消除**。

> **决策要求**：把 C13 从阶段 2 的"写文档"**提前到阶段 1**，与 C4 一并落地：
> WAL 模式下 `.bak` 自动回退改为**显式开关**（如 `autoRecoverFromBackup` 在 WAL 模式下默认关闭）
> 或至少使用**独立的 `onWarning` code** 与残尾截断区分，并在文档中显式声明回滚语义。
>
> ✅ **已决策（2026-10-16 17:12）**：见 `DECISION-20261016-wal-recovery-and-compaction.md` §1。
> 结论为**统一收紧**——`autoRecoverFromBackup` 五语言默认 `false`（Rust / Go 补齐该字段），
> Snapshot 与 WAL 不做区分，损坏即抛 `CorruptedError` 拒开；`.bak` 重新定位为
> "人工恢复用的离线副本"，不再由引擎自动使用。

**M4. §3 误判清单漏了一条报告间矛盾。**

§3 现列 5 条，应补第 6 条：

> 6. **【不成立】报告 c："Rust 249 个测试全部通过"** —— 实测 `cargo test -- --list` 为
>    64 个 `#[test]` + 2 个 doc-test = **66** 个；报告 a 的 66 正确。Go 49 / .NET 67 亦经实测吻合。

### 7.4 建议补充的缺口

**G1. Rust / Go 在 WAL 模式下会把 `.bak` 本身也截断，并覆盖主库（三份报告均未提出）。**

`init_and_recover` 回退 `.bak` 时走的是同一个 `load_file`，而 `allow_tail_truncation` 由 options 推导（`db.rs:1608-1615`），于是回退分支 `db.rs:1570` 会对 `.bak` 执行残尾截断，随后 `fs::copy` 把**已被截断的备份**覆盖到主库（`go db.go:1384-1386` 完全同构）。

| 语言 | `.bak` 加载是否允许截断 | 证据 |
| :-- | :-- | :-- |
| Rust | ❌ 会截断 | `db.rs:1570` → `:1608` |
| Go | ❌ 会截断 | `db.go:1384` → `loadFile` |
| Kotlin | ✅ 不会 | `CkvDb.kt:115` 不传 `allowTailTruncation` |
| Dart | ✅ 不会 | `db.dart:131` |
| .NET | ✅ 不会 | `Db.cs:376` |

> **修法**：`.bak` 一律以 `allow_tail_truncation=false` 加载，截断只对主库生效。建议**并入 C10 一并实施**（两者都在 open 路径、都涉及 `FileLock`）。

**G2. T6 的验收口径与修复后的持久化语义冲突。**

按 §4 阶段 1 的新顺序（append → `apply_mem` → 压实），压实失败时数据**已经 fsync 落盘**。此时上抛错误等于告诉调用方"写失败"，但写实际已成功，调用方重试还会重复执行。

> **决策要求**：明确"压实失败"的契约，三选一：
> - (a) `onWarning` + 置 `needs_compaction` 标志，由下一次写或后台重试（**推荐**，与报告 a P0 建议 1 一致）；
> - (b) 直接上抛，但文档明确"错误仅表示压实失败、数据已持久化"；
> - (c) 增加 `strictCompaction` 选项，默认 (a)。
>
> 另：T6 的"目录只读"前提不成立——只读目录下 WAL 追加会**先**失败，测不到压实路径。
> 需改用故障注入点（如可注入的 `commit_flush` 失败钩子）。
>
> ✅ **已决策（2026-10-16 17:12）**：见 `DECISION-20261016-wal-recovery-and-compaction.md` §2。
> 采纳 **(c)**：默认 `onWarning("onCompactionFailed")` + `needs_compaction` 标志 + 退避重试
> （下一次写 + `close()` 兜底），本次写**返回成功**；新增 `strictCompaction`（默认 `false`）
> 作为"该失败就失败"的显式出口。**不上抛是硬约束**——`increment` 是 read-modify-write
> （`db.rs:543-558` / `db.go:502+` / `CkvDb.kt:471-483`），上抛会让调用方重试并重复累加 delta。
> T6 采用"仅测试可用的注入钩子 + 文件系统条件补充"。

**G3. C6 的实现细节会导致锁泄漏。**

Kotlin `beginFlush()` 返回 `Pair<LockFile, Long>`，锁的释放只在 `commitFlush` 内部的 `finally { lock.close() }`（`CkvDb.kt:222-224`）。把 `applyMem()` 插在两者之间后，一旦 `applyMem` 抛异常，**锁无人释放**。

| 语言 | 现有释放机制 | C6 修复是否需额外处理 |
| :-- | :-- | :-- |
| Rust | `impl Drop for FileLock`（`lock.rs:166-170`） | 否，RAII 兜底 |
| Go | `defer lock.Release()` | 否 |
| .NET | `finally { lockHandle.Dispose(); }` | 否 |
| **Kotlin** | 仅 `commitFlush` 的 `finally` | **是**，须改为 `lock.use { }` 或 `try/finally` |

**G4. 回归测试门槛覆盖不足。**

§4 阶段 0 的 T1–T8 只覆盖 C1 / C2 / C3 / C4 / C11，缺以下条目（可吸收报告 c 的 T5/T10/T13）：

| 编号 | 测试 | 覆盖 | 来源 |
| :-- | :-- | :-- | :-- |
| T9 | 轮转过程中断言 `.bak` 始终存在（覆盖态/注入点） | C5 | 报告 c T5 |
| T10 | 持锁进程存活时第二进程不可窃取；持锁进程被杀后可获取 | C9 | 报告 c T8/T9 |
| T11 | open 残尾截断与并发追加同时进行，追加帧不被截断 | C10 | 报告 c T10 |
| T12 | 恢复 int64≠8B / bool≠1B / 保留 vtype → `Corrupted` | C12 | 报告 c T13 |
| T13 | 回退 `.bak` 时**不截断 `.bak`**（构造残尾 `.bak` 验证其长度不变） | G1 | 本次新增 |
| T14 | WAL 模式下 `.bak` 回退的开关/告警语义 | M3 | 本次新增 |

> C9 是"锁协议整体重写"级改动（POSIX fd + `fcntl`/`flock`、Windows 保持句柄），
> 没有 T10 这类门槛时风险过高，**不应与 C1–C5 混在同一批次**。

**G5. Dart 的 C2 在 POSIX 与 Windows 上表现不同，测试断言需分平台。**

Dart `lock.dart:21` 用 `FileMode.write`（含 `O_TRUNC`，无 `O_EXCL`）+ `lockSync`：

| 平台 | 嵌套 `_beginFlush()` 的结果 | 实际缺陷形态 |
| :-- | :-- | :-- |
| Windows | 句柄互斥 → 退避到 `lockTimeoutMs` → 抛 `LockedError` | 阻塞 5s（默认）后写失败 |
| **Linux / macOS** | `fcntl` 记录锁同进程重入**成功** → 压实真的执行 | 随后内层 `release()` 的 `File(lockPath).deleteSync()`（`lock.dart:61`）删掉外层仍在持有的锁文件 → **无锁窗口的分裂脑** |

> T2 / T6 需按平台写两套断言；POSIX 侧应断言"压实后锁文件仍然存在"。

### 7.5 表述口径修订

| 编号 | 位置 | 现状 | 应改为 |
| :-- | :-- | :-- | :-- |
| S1 | §2 C2 | 只说"不必纠结异常类型" | 补明推演：默认 `ForceUnlock=false`（`Options.cs:13`）时嵌套取锁必然走到 `LockFile.cs:111-116` 抛 **`LockedError`**；报告 a 实测到的 `UnauthorizedAccessException` 仅在 `ForceUnlock=true`（立刻窃取成功 → 轮转 rename 失败）时才会出现。避免实施者被报告 a 带偏 |
| S2 | §1 C1 | 只写"每次越线写先卡 10s 再抛错" | 补记它同时消解**报告 a 的"缺陷 3"**：当前顺序下帧已落盘而 `apply_mem()` 未执行 → 内存/磁盘**永久分叉**，这是比"卡 10s"更严重的后果 |
| S3 | §4 阶段 2 第 1 条 | "copy 兜底改为先写 `dst.tmp` → 校验 → 覆盖" | 现有 `fs::copy(src, dst)`（`db.rs:1982`）本身不留空窗，但会产生**部分写入的 `dst`**。需写清校验对象与失败时保留哪一代 |
| S4 | §4 阶段 1 | "`apply_entries` 改为返回文件大小" | 应为 **`append_entries`**——`apply_entries`（`db.rs:1750`）是恢复重放函数，名称冲突。另：Dart 的 raf 双重 close 已被 `db.dart:415-417` 的 try/catch 兜住，"避免 double-close"表述不准，真实要删的是 `:409` 的提前关闭与 `:412` 的早退 |

### 7.6 修订后的实施顺序

阶段划分**不需要调整**，只需做两处插入：

```
[阶段 0.5 · 已完成]  决策会 → docs/DECISION-20261016-wal-recovery-and-compaction.md
  ① M3 / D1：已定 —— `autoRecoverFromBackup` 五语言默认 false，损坏即拒开（§1.2）
  ② G2 / D2：已定 —— 告警 + needs_compaction + 退避重试，新增 strictCompaction 出口（§2.2）
  注：API v1.0 尚未正式发布，Options 字段调整属发布前修订，不受 API-V1-FREEZE 的
      v2.0 流程约束；五语言可直接对齐。

阶段 0  回归测试 T1–T14（原 T1–T8 + §7.4 新增 6 条）
阶段 1  P0：C1 + C2（顺序倒置）、C3、C4（按 M2 修正算法）
        └─ 同批并入 G1（.bak 不截断）、C10（截断纳入 FileLock）
        └─ 同批并入 D1（autoRecoverFromBackup 默认 false、损坏即拒开）
阶段 2  P1：C5（范围改为 Rust/Dart/Kotlin）、C6（按 G3 处理锁释放）、
        C7、C8、C9（独立批次，配 T10）、C11
阶段 3  P2：C12、C14–C21
```

> **C13 已由 D1 消化**：`.bak` 的定位与损坏处理策略在阶段 1 一次性落地，
> 阶段 2 不再保留"仅补文档"的条目。

### 7.7 本次复核的边界

- 本次仅做复核与文档修订，**未改动任何生产代码**，未执行 `git commit` / `push`；
- 原文档 §0–§6 的结论**未被删除或改写**，修订一律以本章形式追加，便于对照；
- 修订前的原文已备份至 `temp/backups/FIX-PLAN-20261016-wal-core-issues.md.20261016-165400.bak`；
- §7.1 的行号基于 `3219d9c`；若在其后有代码提交，需重新核对。

### 7.8 决策回写（2026-10-16 17:12:00）

阶段 0.5 的两项决策已落定，决策依据与完整记录见
`docs/DECISION-20261016-wal-recovery-and-compaction.md`。决策原则：

> **减少回退，尽力进行，极端情况该失败就失败。**

| 项 | 决策结论 | 对本计划的影响 |
| :-- | :-- | :-- |
| M3 / D1 | `autoRecoverFromBackup` 五语言统一默认 `false`（Rust / Go 补齐字段）；Snapshot 与 WAL 不做区分；损坏即抛 `CorruptedError`；`.bak` 重新定位为人工恢复用的离线副本 | C13 从阶段 2 提前到阶段 1，与 C4 同批落地 |
| G2 / D2 | 压实失败 → 告警 + `needs_compaction` + 退避重试（下一次写 + `close()` 兜底），写**返回成功**；新增 `strictCompaction`（默认 `false`）作为显式失败出口；T6 用测试专用注入钩子 | T6 验收口径改写；C1 的"错误必须上抛"细化为"告警 + 重试" |

**顺带修正的一处事实错误**：报告 a §4.3 与本文档 §0 表格把
`allowTailTruncation / autoRecoverFromBackup` 一并称为"Kotlin 独有可配"，**不准确**。
实测（`dev` @ `3219d9c`）：

| 选项 | Rust | Go | .NET | Dart | Kotlin |
| :-- | :-: | :-: | :-: | :-: | :-: |
| `autoRecoverFromBackup` | ❌ 无字段 | ❌ 无字段 | ✅ 默认 `true` | ✅ 默认 `true` | ✅ 默认 `true` |
| `allowTailTruncation` | ❌ | ❌ | ❌ | ✅ | ✅ |

准确表述应为：**`autoRecoverFromBackup` 三语言有、Rust / Go 两语言缺失**。
D1 的实施因此是"补齐 Rust / Go + 统一收紧默认值"，而非五语言从零新增。

**（第三轮复核更正，2026-10-16 17:38:10，见 §8.1）**：下一句"`allowTailTruncation` 才确实只有 Dart 与 Kotlin 可配"
**不成立**——五语言一律由 `engineMode == AppendOnlyLog && !readOnly` 推导（Rust `db.rs:1608-1609`、
Go `db.go:1428`、.NET `Db.cs:355`、Dart `db.dart:109-110`、Kotlin `CkvDb.kt:104`）；
Dart 的 `DbOptions` **无该字段**，Kotlin 的 `Options.kt:18` 是**无读取点的死字段**。
准确表述应为：**`allowTailTruncation` 五语言均不可配，Kotlin 的声明是无效字段，Dart 连声明都没有**。
详见 `REVIEW-20261016-plan-verification.md` §2 V1。

---

## 8. 第三轮复核修订（2026-10-16 17:38:10）

> **复核报告**: `REVIEW-20261016-plan-verification.md`（V1–V10）
> **复核基线**: `dev` @ `3219d9c`，源码零改动，`git status` 仅文档改动
> **修订性质**: 事实台账更正 + 决策边界补充 + 6 处正文加注/更正口径
> **原文备份**: `temp/backups/FIX-PLAN-20261016-wal-core-issues.md.20261016-173810.bak`
> **重要**: §0–§7 的 P0 结论（C1–C4 的根因、定位、修复方向）与 M1 / M2 / G1 / G3 / G5 **全部复核通过**，
>   本节只修订与之冲突的过期口径与两处事实错误，**不改变任何修复方向**。

### 8.1 已作废口径与事实更正（4 条）

| # | 位置 | 原文 | 处置 | 依据 |
| :-: | :-- | :-- | :-- | :-- |
| V3 | §4 "Fix C4" 伪码、§7.3 M2 | "抛 Corrupted（**触发 .bak 恢复**，禁止截断）" | 已加注：D1 落地后默认**不再自动回退** | `DECISION` D1-1/D1-3 |
| D2-1 | §4 阶段 1 伪码 | `compact_now() // 错误必须上抛` | 已加注【已作废】 | `DECISION` D2-1：告警 + 退避重试，本次写返回成功 |
| D2-1 | §4 阶段 0 T6、§5 第一条门槛 | "压实失败时写操作返回错误" / "T1–T8 全部为绿" | T6 已改写为 D2 口径；门槛已扩至 **T1–T17** | `DECISION` D2-1；§7.4 G4 |
| V1 | §7.8 末段结论 | "`allowTailTruncation` 才确实只有 Dart 与 Kotlin 可配" | 已更正为：**五语言均不可配**，Kotlin 是无读取点的死字段、Dart 无该字段 | `db.rs:1608-1609`、`db.go:1428`、`Db.cs:355`、`db.dart:109-110`、`CkvDb.kt:104`；`Options.kt:18` 全工程无读取点 |

### 8.2 新增与调整的实施项（5 条）

1. **（V10，新增）Kotlin `.bak` 撕裂修复 —— 并入 M1 同批**。
   `CkvDb.kt:203-208` 生成 `.bak` 用的是 `Files.copy(dbFile, bakFile, REPLACE_EXISTING)`；
   `REPLACE_EXISTING` 会先截断目标再写入，中途崩溃即留下**半份 `.bak`**（上一代完整备份被覆盖丢失）。
   这与 M1 记录的"兜底路径 `delete() → renameTo` 窗口"是**两种不同失效形态**，M1 需同时收录。
   修法：写 `bak.tmp` → fsync → rename 覆盖，与 .NET `AtomicReplace`（`Db.cs:988-998`）同构。
   D1 把 `.bak` 重新定位为"人工恢复用的离线副本"之后，此处风险上升：撕裂的 `.bak` 会让人工恢复同样失败且无告警。
2. **（V5 / V6，新增）分支 ③ 的处理 —— 并入阶段 1，与 D1 同批**。
   打开路径实有三条分支（§ 见复核报告 §3），本计划原先只处理第二条。需补：
   - 主库缺失 + `.bak` 存在（`DECISION` D1-7）；
   - 主库缺失 + `.bak` 损坏**禁止静默开空库**（`DECISION` D1-8）。五语言现状证据：
     Rust `db.rs:1588-1599`、Go `db.go:1403-1419`、Kotlin `CkvDb.kt:134-146`、
     Dart `db.dart:147-160`、.NET `Db.cs:406-410`（.NET 注释为设计意图，其余四语言无注释）。
     属**第 5 处静默数据丢失**，与 C4 同源（都把"能判定损坏"降级成"打开成功"）。
3. **（V7，新增）`readOnly` × `autoRecoverFromBackup` 组合**（`DECISION` D1-9）：
   readOnly 下该开关不生效且回退路径不得写盘。现状 Rust `db.rs:210` 在 readOnly 下**也**执行
   `init_and_recover()`，只有残尾截断排除了 `read_only`（`db.rs:1608-1609`）。
4. **（V4，补充）§4 阶段 2 第 1 条与 §7.5 S3 需补**：`db.rs:1979-1981` 在 copy 兜底**之前**仍有
   `if dst.exists() { remove_file(dst) }`，故兜底整体仍存在"两代备份同时不存在"的窗口；
   且 `fs::copy` 失败会留下**半份 `dst`**。需写清校验对象与"失败时保留哪一代"。
5. **（V8 / V9，补充）D2 的实施细节**：明确退避计数语义（"跳过 N 次"还是"第 N 次"）、复位条件，
   并补一个**不依赖调用方 `close()`** 的触发口（时间下限或显式 `compact()` 强制重试）；
   `needs_compaction` 需可观测——默认路径当前只有 `onWarning` 一条通道，"文件无界膨胀"不可编程检测。

### 8.3 阶段 0 测试门槛补充（T15–T17）

| 编号 | 测试 | 覆盖 |
| :-- | :-- | :-- |
| T15 | 主库不存在 + `.bak` **损坏** → 断言不静默开空库（按 D1-8 取 `Corrupted`，或按新决策带告警） | V6 |
| T16 | `readOnly=true` + `autoRecoverFromBackup=true` + 损坏主库 → 断言**不写盘**、文件 mtime 不变 | V7 |
| T17 | 注入轮转中断 → 断言 `.bak` 要么完整可解析、要么不存在，**不存在半份状态** | V10 |

### 8.4 本节对原文的改动清单（便于回溯）

1. §4 "Fix C4" 伪码末行 → 加【第三轮复核更正】指注；
2. §4 阶段 1 伪码 `compact_now()` 行 → 加【已作废】指注；
3. §4 阶段 0 表 T6 行 → 改写为 D2 口径；
4. §5 第一条门槛 → T1–T8 改为 T1–T17（并注明新增项来源）；
5. §7.3 M2 伪码 → 加"默认不自动回退"指注；
6. §7.8 末段 → 更正 `allowTailTruncation` 结论并加 §8.1 指针。

> 以上 6 处**仅加注与更正口径，未删除或改写任何原始结论**（原结论在文内仍可见，便于对照）；
> 其余修订一律以本节追加形式呈现，沿用 §7.7 确立的"追加不覆盖"体例。

### 8.5 按主流对齐的削减项（3 项，2026-10-16 17:47:28）

> **依据**: 业主口径"不为更安全引入超出主流成熟实现的复杂度，对齐主流即可"（`DECISION` §0 判据 4）。
> **逐项对照**: `REVIEW-20261016-plan-verification.md` §8（对照 SQLite / RocksDB / bbolt / LMDB / Badger / PostgreSQL）。
> **性质**: 三项均为**减少**改动，不影响 P0 修复。

| # | 原计划条目 | 原方案 | 削减后 | 主流依据 |
| :-: | :-- | :-- | :-- | :-- |
| 1 | §4 阶段 2 第 5 条（C9） | ④ "steal 前加 PID 活性探测作为第三判据" | **不加此启发式**。采用 ② 的 `flock` / `fcntl` 内核锁后，锁在 `Open` 时获取、持有至连接关闭，进程退出由内核自动释放 → 时间戳 / UUID / PID 判活与 `steal` 协议**整体删除** | bbolt：`flock(db, !readOnly, Timeout)` 于 `Open` 获取并持有整个连接生命周期；LMDB：fcntl advisory lock + 进程退出自动释放。两者都无判活/窃取协议。Kotlin、Dart 已是内核锁，五语言收敛后代码量下降 |
| 2 | §4 阶段 3 第 11 条（C15） | 常驻追加句柄 **+ 组提交** | 只保留常驻句柄（或维持现状），**砍掉组提交** | SQLite / LMDB / bbolt 均无组提交；组提交是服务端（PostgreSQL / MySQL）概念，嵌入式单进程无收益 |
| 3 | §4 阶段 3 第 10 条（C14） | "要么实现（维护 `dead_bytes`），要么删除" | **删除字段**（v1.0 未发布，属发布前修订） | bbolt / SQLite 只有大小、页数触发；只有 Badger 有 discard 比例触发，且因其 KV 分离、GC 成本模型不同 |

**另需补 1 项（低于主流）**：`needs_compaction` 的可观测性（本节 §8.2 第 5 条 / 复核报告 V9）——
主流均有正式通道（SQLite 的 `-wal` 大小与 `wal_checkpoint` 三元组返回、bbolt 的 `Stats()`、
RocksDB 的 `Status::severity()` + `OnBackgroundError`），本计划只有一条 warning 回调。

**净效果**：阶段 2 / 阶段 3 比原计划**少 3 项改动、多 1 个查询接口**，整体复杂度低于原计划，
且未引入任何主流实现没有的机制。

### 8.6 结论：阶段 0 / 1 可以照做

- **阶段 0 / 阶段 1**（C1 + C2 + C3 + C4 + G1 + C10 + D1）修复方向与主流一致，**可以实施**；
  动手前只需补齐 4 个"一句话级"口径：① 开关作用域（已落为 `DECISION` D1-7 / D1-8 / D1-9）；
  ② D2 退避的计数语义（跳过 N 次 vs 第 N 次）；③ `needs_compaction` 是否对外可见（建议可见）；
  ④ 分支 ③ 是否并入阶段 1（建议并入，见 §8.2 第 2 条）。
- 阶段 2 / 阶段 3 按 §8.5 削减后照做。

### 8.7 C4 判别方式的简化（本地源码实证，2026-10-16 17:53:30）

> **依据**: `REVIEW-20261016-plan-verification.md` §8.7.2（本地 `temp/github/fjall` 源码实读）。
> **性质**: **净减少代码**——替代 §7.3 M2 的"逐字节扫描 FrameMagic"算法，属"对齐主流"的直接落地。
> **T4 / T5 的断言不变**：T4 仍要求中间坏帧 → `Corrupted`；T5 仍要求尾部截断自愈。

`fjall` 的日志恢复把失败按**类别**分流（`src/journal/batch_reader.rs:107-129,174-177`、`journal/reader.rs:53-78`）：

- **校验和不匹配 / 批次长度不足 / 条目过多** → **报错**（`ChecksumMismatch` / `InsufficientLength` / `TooManyItems`）；
- **文件在批次中间结束（缺 end marker）** → **截断到 `last_valid_pos` 并停止**
  （注释："missing terminator, but last batch, so probably incomplete, discarding to keep atomicity"）。

据此，C4 的判别可简化为 O(1) 判据（**替代 M2 的扫描伪码**）：

```
坏帧处理：
  ① 读取本帧所需字节之前即到文件末尾（short read）      → 断电残尾 → 截断到帧边界，上报截断字节数
  ② 剩余字节全为 0（预分配/未写区）                     → 断电残尾 → 截断（防御性；CKV 无预分配，见下）
  ③ 帧头/载荷字节齐全，但 magic / 帧头 CRC /
     payload CRC / entry 解码失败                       → 介质损坏 → 抛 Corrupted（禁止截断）
  ④ 全部校验通过但 tx_id 非单调                         → 内部不变量破坏 → 抛 Corrupted
```

- 无需 magic 逐字节搜索，无需候选数量上限，无需把 `tx_id` 单调性当作"防误判手段"（它回归为必查项 ④）；
- **M2 的"向后扫描"降级为可选诊断**：仅在报错信息中说明"损坏点之后可能仍有可抢救数据"，
  **不参与控制流**（若连诊断也不做，代码更少）；
- **关于 ②**：已 grep 确认五语言写路径**无任何预分配**（`db.rs`/`format.rs` 的 `set_len` 仅用于恢复期截断：
  `format.rs:279/297/325/341/358/373/403`、`CkvDb.kt:113`、`Db.cs:363`），故零填充尾部在 CKV 自身写入下
  不会出现；② 仅作防御（个别 FS 崩溃后可能留未写零区，RocksDB 默认模式亦显式容忍），成本一行。
- **须同步更正的文档口径**：§4 "Fix C4" 与 §7.3 M2 的伪码中，"（触发 `.bak` 恢复）"已于 §8.1 加注作废，
  本节进一步以类别判据**取代**其算法描述；实施时以本节为准。



