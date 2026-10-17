# CKV WAL 恢复与压实失败契约 —— 决策记录

> **决策编号**: D1（WAL 模式 `.bak` 回退）/ D2（压实失败契约）
> **编制时间**: 2026-10-16 17:03:37
> **决策时间**: 2026-10-16 17:12:00
> **状态**: **已决策**（第三轮复核后补充 D1-7/D1-8/D1-9 并更正 2 处事实，见 §6；2026-10-16 17:38:10）
> **阻塞项**: `FIX-PLAN-20261016-wal-core-issues.md` §7.6 的"阶段 0.5 决策会"
> **关联缺陷**: M3、G2
> **API 约束**: CKV v1.0 **尚未正式发布**，`API-V1-FREEZE.md` 的 Frozen 状态按"发布前基线"理解——
>   Options 字段与默认值调整属**发布前修订**，不走 v2.0 流程，五语言可直接对齐。

---

## 0. 决策原则

> **减少回退，尽力进行，极端情况该失败就失败。**

推论出的三条判据：

1. **能明确判定为"损坏"的状态，一律抛错，不做自动降级**——自动降级会把"数据丢了"伪装成"打开成功"；
2. **可自动完成的动作（压实、重试）尽力做完**，但失败不得反过来污染已经成功的写语义；
3. **兜底路径必须显式**——`.bak` 这类资源保留，但不再由引擎自动使用。
4. **不与主流成熟实现拉开复杂度**（业主口径，2026-10-16 17:45）——不引入 SQLite / RocksDB / bbolt / LMDB /
   Badger / PostgreSQL 中没有的机制；凡超出主流的处置一律降级或删除。逐项对照与削减清单见
   `REVIEW-20261016-plan-verification.md` §8（其中 C9 的 PID 活性探测、C15 的组提交、C14 的死比例触发为三项应削减项）。

---

## 1. D1 —— `.bak` 自动回退：**默认关闭，损坏即拒开**

### 1.1 已核实的事实

1. **WAL 模式下 `.bak` = 压实前的文件**。轮转顺序是先 `db → bak` 再 `tmp → db`：
   Rust `db.rs:1906-1913`、Go `db.go:1665-1673`、.NET `Db.cs:958-962`、Dart `db.dart:311-314`、
   Kotlin `CkvDb.kt:204-216`（Kotlin 用 `Files.copy` 而非 rename，是另一处漂移）。
   因此 `.bak` 逻辑上**包含**截至上次压实的全部数据，但**不含压实之后写入的数据**。
2. **主库加载失败即自动用 `.bak` 覆盖主库**，只发一条 `onRecoveredFromBackup` 告警：
   Rust `db.rs:1567-1583` 与 `:1587-1599`、Go `db.go:1382-1398`、.NET `Db.cs:369-393`、
   Dart `db.dart:128-136`、Kotlin `CkvDb.kt:116-124`。
3. **`autoRecoverFromBackup` 的现状是三语言有、两语言无**（此处更正了报告 a 与
   FIX-PLAN §0 表格中"仅 Kotlin 独有可配"的笼统说法——`allowTailTruncation` 才确实只有 Kotlin）：
   **（第三轮复核追加更正）**：`allowTailTruncation` 在五语言中**一律由 `engineMode == AppendOnlyLog && !readOnly` 推导**，
   Rust `db.rs:1608-1609`、Go `db.go:1428`、.NET `Db.cs:355`、Dart `db.dart:109-110`、Kotlin `CkvDb.kt:104` —— 五处形态一致，
   **没有任何语言可配置**；Kotlin 的 `Options.kt:18` 是无读取点的死字段，Dart 连字段都没有。详见 §6.1。

| 语言 | `autoRecoverFromBackup` | `allowTailTruncation` |
| :-- | :-- | :-- |
| Rust | ❌ **无此字段**，无条件回退 | ❌ 由 `engine_mode` 推导 |
| Go | ❌ **无此字段**，无条件回退 | ❌ 由 `engine_mode` 推导 |
| .NET | ✅ `Options.cs:34`，默认 `true` | ❌ 由 `engine_mode` 推导 |
| Dart | ✅ `options.dart:27`，默认 `true` | ❌ **无此字段**（第三轮复核更正，见 §6.1） |
| Kotlin | ✅ `Options.kt:17`，默认 `true` | ⚠️ `Options.kt:18` 已声明但**从未被读取**（死字段，见 §6.1） |

### 1.2 决策内容

| # | 决策项 | 结论 |
| :-: | :-- | :-- |
| D1-1 | 默认行为 | **`autoRecoverFromBackup` 默认 `false`**：Rust / Go 补齐该字段；Kotlin / .NET / Dart 由 `true` 改为 `false` |
| D1-2 | 引擎模式差异 | **不做区分**。Snapshot 与 WAL 一律默认不回退（早期设想的"仅 WAL 默认关"过于复杂，且与原则 1 冲突） |
| D1-3 | 关闭时的行为 | 抛 `CorruptedError` **拒开**；错误信息中说明 `.bak` 是否存在、以及如何显式恢复 |
| D1-4 | 开启时的行为 | 保留现有自动回退，并沿用**既有**的独立告警码 `onRecoveredFromBackup`（与 `onTailTruncated` 本已分离，无"共用语义"需拆，见 §6.1） |
| D1-5 | `.bak` 的新定位 | **人工恢复用的离线副本**，不再是引擎的自动回滚机制。文档须同步改写 |
| D1-6 | `allowTailTruncation` | **不在本次收紧范围**。残尾截断是 WAL 的正常崩溃自愈，不是"回退"，与 `.bak` 无关 |
| D1-7 | **开关的作用域**（第三轮复核补充） | 只门控"**主库损坏 → `.bak`**"这一条分支；"`.tmp` 晋升/清理"分支不受影响；"**主库缺失 → `.bak`**"分支按 D1-8 单独处理。禁止把开关套在整个回退段上（见 §6.2） |
| D1-8 | **主库缺失且 `.bak` 损坏**（第三轮复核补充） | **抛 `CorruptedError` 拒开**，禁止静默按空库打开。五语言现状会静默开空库（见 §6.2），与 §0 判据 1 冲突 |
| D1-9 | **`readOnly=true` 的组合**（第三轮复核补充） | `autoRecoverFromBackup` 在该模式下**不生效**；不得修改 `.db` / `.bak` **数据文件**（锁文件可写，见 §6.2 与 `REVIEW-20261016-plan-verification.md` §8.4） |

### 1.3 理由

1. **这是让 C4 闭环的必要条件**：C4 让中间坏帧抛 `CorruptedError`，而该异常今天正是自动回退的触发条件。
   不改 D1，P0-2 只是从"静默截断整库"变成"静默回滚压实后写入"，P0 并未消除；
2. **静默降级掩盖数据丢失**：`onRecoveredFromBackup` 只是一条 warning，调用方拿到的是"打开成功"，
   与"数据少了一代"之间没有任何可编程的区分；
3. **Rust / Go 连关闭的开关都没有**，属于能力缺失而非策略选择，先补齐能力再统一收紧默认值；
4. **不做模式区分**降低了跨语言漂移面：一条规则覆盖两种引擎，比"按模式分叉"更容易验证。

### 1.4 破坏性影响（须显式声明）

| 变更 | 影响 | 处理 |
| :-- | :-- | :-- |
| `.NET` / `Dart` / `Kotlin` 默认值 `true → false` | **行为变更**：原本能自动打开的损坏库，现在抛 `CorruptedError` | 在 CHANGES 与 API 文档显式声明；补测试锁定新默认值 |
| Rust / Go 新增字段 | 源码兼容（默认值即新行为） | 无 |
| `readOnly=true` 下的 `.bak` 回退 | 原本会写盘（`API-V1-FREEZE.md` §3.15） | 默认关闭后该路径不再触发；§3.15 需标注"仅在显式开启时适用" |

### 1.5 剩余实现细节（无需决策）

- Rust / Go 的字段命名与 builder 方法名：`auto_recover_from_backup` / `AutoRecoverFromBackup`；
- `CorruptedError` 消息模板：需包含 `.bak` 路径与"设置 `autoRecoverFromBackup=true` 可强制恢复"的指引；
- `onRecoveredFromBackup` 的告警级别是否从 warning 提升为 error（倾向保持 warning，避免影响既有回调方）。

---

## 2. D2 —— 压实失败：**告警 + 退避重试，默认不上抛**

### 2.1 已核实的事实

1. 修复后的写路径顺序为 **append（已 fsync）→ `apply_mem` → 压实**
   （FIX-PLAN §4 阶段 1）。落盘点：Rust `db.rs:1696-1702`、Go `db.go:1749-1754`、
   .NET `Db.cs:1162-1171`、Dart `db.dart:395-400`、Kotlin `CkvDb.kt:338-343`。
2. **压实失败时，数据已经持久化**——`commit_flush` 之前的 WAL 帧是完整的。
3. **`increment` 不是幂等操作**：五语言都是 read-modify-write
   （Rust `db.rs:543-558` 读当前值后 `wrapping_add`；Go `db.go:502+`；Kotlin `CkvDb.kt:471-483`）。
   若因为"压实失败"向调用方返回错误，调用方重试会**再累加一次 delta**。

### 2.2 决策内容

| # | 决策项 | 结论 |
| :-: | :-- | :-- |
| D2-1 | 默认行为 | 压实失败 → `onWarning("onCompactionFailed", ...)` + 置 `needs_compaction = true`；**本次写返回成功** |
| D2-2 | 为什么不上抛 | 写已落盘，报错等于让调用方重试一个已经成功的写；而 `increment` 重试会**重复累加 delta**（2.1.3）。这是数据正确性问题，不是风格问题 |
| D2-3 | 重试时机 | **下一次写时重试** + **`close()` / `Dispose()` 时兜底重试一次**。不在 Deferred 后台线程里做（后台线程持写锁，见 C16） |
| D2-4 | 退避策略 | 失败后跳过的写次数按 `2^n` 退避（起始 64，上限 4096），避免持续失败时每次写都重试导致延迟升高 |
| D2-5 | 逃生口 | 新增 `strictCompaction`（默认 `false`）：显式开启时压实失败**直接上抛**，供"文件绝不能膨胀"的场景使用 |
| D2-6 | T6 的故障注入 | 各语言加**仅测试可用的注入钩子**（Rust `#[cfg(test)]` / Go 包级变量 / .NET `internal static` / Kotlin、Dart `internal var`），并以真实文件系统条件作为补充 |

### 2.3 理由

1. **原则 2 的直接落地**：压实是可自动完成的动作，应尽力做完（退避重试 + close 兜底），
   但失败不得污染已成功的写语义；
2. **原则 3 的体现**：`strictCompaction` 就是"该失败就失败"的显式通道——
   默认安全，但用户可以主动选择严格模式；
3. **Go 的现状是从"静默吞错"（`db.go:1767-1769`）改为"告警 + 重试"**，比现状更可见，
   又不像上抛那样破坏调用方语义；
4. **不用后台线程重试**：后台刷盘线程持写锁执行全量序列化（C16 / `db.rs:227-261`），
   把压实塞进去会同时踩两个坑。

### 2.4 剩余实现细节（无需决策）

- 退避常量（起始 64 次、上限 4096 次）需在测试中可注入，否则无法稳定验证；
- `needs_compaction` 标志的存放位置：五语言均放在各自的 `DbInner` / 实例字段中，与
  `lastKnownDiskState` 同级；
- `onCompactionFailed` 的告警消息需带"当前文件大小 / 阈值 / 上次失败原因"。

---

## 3. 决策后的回写清单

决策已落定，以下文档需在**编码时同步更新**（目前均未改动）：

| # | 文档 | 需要更新的内容 |
| :-: | :-- | :-- |
| 1 | `FIX-PLAN-20261016-wal-core-issues.md` §7 | 已回写（见 §7.8） |
| 2 | `API-V1-FREEZE.md` §3 | 追加一致性修复条目：`autoRecoverFromBackup` 默认值变更、新增 `strictCompaction`、新告警码 |
| 3 | `KV-DB-DESIGN-SPEC.md` 恢复协议章节 | `.bak` 定位改为"人工恢复用离线副本"；损坏即拒开的流程 |
| 4 | `PHASE2-APPEND-LOG-COMPACTION-DESIGN.md` §3.1 / §3.4 | 中间坏帧鉴别 + 压实失败契约 |
| 5 | `SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016.md` §2.3 | 该节把 `.bak` 称为"完整的两代容灾回滚能力"，在 WAL 语境下需按 D1 结论重述 |
| 6 | `PHASE2-SYNCMODE-AND-WAL-SUMMARY-20261016.md` | 同上 |
| 7 | `REVIEW-20261016-plan-verification.md` | **本轮（第三轮）复核报告**：V1–V10 的实证依据、D1-7/D1-8/D1-9 的定位与建议 |

---

## 4. 决策带来的净变化

| 项 | 决策前 | 决策后 |
| :-- | :-- | :-- |
| 主库损坏（默认配置） | 自动回退 `.bak`，仅一条 warning，调用方无感 | **抛 `CorruptedError` 拒开** |
| 压实失败（默认配置） | Go 静默吞 / 其余上抛（且写未生效） | 告警 + 退避重试 + close 兜底，**写正常返回** |
| Rust / Go 的 `.bak` 开关 | 无 | 有（`auto_recover_from_backup`） |
| 严格模式可用性 | 无 | `strictCompaction` |

---

## 5. 本文档的边界

- 本文档只记录决策与依据，**未改动任何生产代码**，未执行 `git commit` / `push`；
- 全部 `file:line` 基于 `dev` @ `3219d9c` 实读；
- 证据行号若在后续提交中变动，需重新核对。

---

## 6. 第三轮复核修订（2026-10-16 17:38:10）

> **复核报告**: `REVIEW-20261016-plan-verification.md`（V1–V10）
> **修订性质**: 事实性更正 + 决策边界补充，**未推翻 D1-1…D1-6 的任何结论**；
> **基线**: `dev` @ `3219d9c`，源码零改动。
> **原文备份**: `temp/backups/DECISION-20261016-wal-recovery-and-compaction.md.20261016-173810.bak`

### 6.1 事实性更正（2 条）

| # | 原文位置 | 原文 | 更正后 | 实证 |
| :-: | :-- | :-- | :-- | :-- |
| V1 | §1.1 表格 Dart / Kotlin 行的第三列 | Dart / Kotlin 的 `allowTailTruncation` "可配" | Dart **无此字段**；Kotlin **已声明但从未被读取** | Dart `options.dart:25-36` 构造参数全集无该字段，`db.dart:109-110` 由 `engineMode && !readOnly` 推导；Kotlin `Options.kt:18` 声明，`CkvDb.kt:104` 用本地推导值，全工程无读取点 |
| V2 | §1.2 D1-4 后半句 | "并发独立告警码（**不再与残尾截断共用语义**）" | 无"共用语义"需拆：两类告警码本已分离 | `onRecoveredFromBackup` 仅出现在 `.bak` 分支（`db.rs:1574/1592`、`db.go:1388/1408`、`CkvDb.kt:125/142`、`Db.cs:380/404`、`db.dart:129/151`）；残尾一律 `onTailTruncated`（`format.rs:282+`、`format.go:292+`、`Format.kt:190+`、`Db.cs:507+`） |

**结论口径**：`autoRecoverFromBackup` = 三语言有、Rust / Go 缺失（原文正确）；
`allowTailTruncation` = **五语言一律由 `engineMode` 推导，无任何语言可配**（原文两处均错，已更正）。

### 6.2 决策边界补充（3 条，已并入 §1.2 表）

**D1-7 作用域**。打开路径实有**三条**分支，D1 原文只描述了第二条：

| 分支 | 触发条件 | 实证 | D1 处理 |
| :-- | :-- | :-- | :-- |
| ① `.tmp` 晋升/清理 | 轮转中断 | `db.rs:1545-1556`、`Db.cs:325-345`、`CkvDb.kt:95-100`、`db.dart:95-105` | 不受 D1 影响 |
| ② 主库损坏 → `.bak` | `load_file(主库)` 抛错 | `db.rs:1567-1583`、`db.go:1382-1398`、`Db.cs:369-393`、`CkvDb.kt:116-132`、`db.dart:128-137` | **D1-1/D1-3 门控此处** |
| ③ 主库缺失 → `.bak` 晋升 | 主库不存在且 `.bak` 存在 | `db.rs:1587-1599`、`db.go:1402-1419`、`Db.cs:398-411`、`CkvDb.kt:134-146`、`db.dart:147-160` | 按 D1-8 处理 |

**关于分支 ③ 的语义（实读结论）**：`commit_flush` 的顺序是"写 `.tmp` → `sync_all` → 关句柄 → 轮转 `.db → .bak` → `.tmp → .db`"（`db.rs:1903-1913`），
因此正常轮转崩溃必然由分支 ① 兜底（`.tmp` 已完整落盘），**分支 ③ 无法由轮转中断触发**；
它的实际作用是"主库被外部删除/替换时静默用上一代补齐"。而"主库不存在 + `.bak` 存在"**不可能是全新库**，
静默补齐即静默降级，与 §0 判据 1 冲突 —— 故须单独规定，不能留空。

**D1-8 主库缺失且 `.bak` 损坏**。五语言现状一律静默落到"全新空库"：

| 语言 | 实证 | 形态 |
| :-- | :-- | :-- |
| Rust | `db.rs:1588-1599` | `if let Ok(...)` 不成立即穿透 → `current_max_tx_id = 0` |
| Go | `db.go:1403-1419` | `if loadErr == nil` 不成立即穿透 → `// New database` |
| Kotlin | `CkvDb.kt:134-146` | `catch (_: Exception) {}` 吞掉且无 `return` |
| Dart | `db.dart:147-160` | `catch (_) {}` |
| .NET | `Db.cs:406-410` | `catch (CorruptedError) { // 按新库处理，保留现场不清空 }`（.NET 注释为设计意图，其余四语言无注释） |

这是**第 5 处静默数据丢失**（前四处为 C1–C4）：现场同时存在损坏 `.bak` 与"打开成功"的空库，
调用方首次写入即生成全新 `.db`，"数据丢了"被伪装成"打开成功"。D1-8 取**抛 `CorruptedError`**（与判据 1 一致）。

**D1-9 `readOnly` 组合**。回退实现是 `fs::copy` / `File.Copy` / `Files.copy`，且 Rust `db.rs:210` **无条件**调用
`init_and_recover()`，只有残尾截断排除了 `read_only`（`db.rs:1608-1609`）—— 即只读打开损坏库会改写磁盘文件。
§1.4 已把该现象列为"变更影响"，但未规定组合语义，故补为 D1-9。
**口径精确化（`REVIEW-20261016-plan-verification.md` §8.4）**：禁止的是修改 `.db` / `.bak` **数据文件**；
**锁文件可写**——LMDB 官方文档明确"There is normally no pure read-only mode, since readers need write access
to locks and lock file"，即主流只读模式同样需要写锁文件。实现时不要把锁文件一并禁掉，否则跨进程互斥失效。

### 6.4 复杂度的取舍尺度（对齐主流）

§0 判据 4 已明确"不超出主流实现"的尺度，逐项对照见 `REVIEW-20261016-plan-verification.md` §8。结论摘要：

| 处置 | 项 | 结论 |
| :-- | :-- | :-- |
| 强对齐、按原决策实施 | D1（默认拒开）、D2（压实失败不使写失败） | SQLite 的"检查点未完成不报失败"、Badger 的"默认需显式 `Truncate`"、bbolt/PG 的"损坏即报错不自动修复"均为同构先例 |
| **应削减（反超主流）** | C9 的 PID 活性探测、C15 的组提交、C14 的死比例触发 | 分别对齐 bbolt/LMDB 的"内核锁 + 超时"、嵌入式库无组提交、bbolt/SQLite 只有大小触发 |
| **应补（低于主流）** | `needs_compaction` 的可观测性（本轮 V9） | 主流都有正式通道（SQLite WAL 大小 / bbolt `Stats()` / RocksDB severity） |

### 6.3 未采纳的意见（保留原文）

- D1-2（不做 Snapshot / WAL 区分）：本轮复核未找到推翻它的新证据；附带影响是 Snapshot 模式下
  "主库损坏即上次完整快照丢失"的可用性代价，属策略取舍，维持原决策；
- D1-6（`allowTailTruncation` 不收紧）：结论维持，但理由按 §6.1 改写（不是因为"可配"，而是因为它由 `engineMode` 推导）。
