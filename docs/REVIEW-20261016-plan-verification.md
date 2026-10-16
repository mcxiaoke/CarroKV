# CKV WAL 修复计划 · 决策落地复核（第三轮）

> **复核时间**: 2026-10-16 17:38:10
> **复核基线**: `dev` @ `3219d9c`（`git status` 仅文档改动，**源码零改动**）
> **复核对象**: `DECISION-20261016-wal-recovery-and-compaction.md`（D1 / D2）、
>   `FIX-PLAN-20261016-wal-core-issues.md`（§0–§7）
> **复核方式**: 逐条回到源码实读（Rust / Go / .NET / Dart / Kotlin 五语言），每条论断定位到具体 `file:line`
> **复核结论**: **P0 四条（C1–C4）的根因、定位、修复方向全部属实，阶段划分与验收方向可执行**；
>   决策侧的边界有 **3 处未闭合**、事实台账有 **4 处错误**，须在动手改代码前修订。
> **本文档边界**: 只做复核与修订建议，**未改动任何生产代码**，未执行 `git commit` / `git push`。
> **证据有效期**: 全部行号基于 `3219d9c`；其后若有代码提交需重新核对。

---

## 0. 结论速览

| 编号 | 问题 | 类别 | 严重度 | 影响 |
| :-- | :-- | :-- | :-: | :-- |
| V1 | `allowTailTruncation` 的可配性：Dart **无此字段**、Kotlin **声明但从未被读取** | 事实台账 | 中 | DECISION §1.1 表格两行、FIX-PLAN §7.8 结论均不成立；照表格实施会改错 |
| V2 | D1-4 要求"独立告警码，不再与残尾截断共用语义" | 事实台账 | 低 | 五语言**早已分离**，该条是空操作 |
| V3 | FIX-PLAN §4 Fix C4 / M2 伪码写"抛 Corrupted（**触发 .bak 恢复**）" | 事实台账 | 中 | 与 D1 冲突：恢复已被默认关闭，闭环表述失效 |
| V4 | S3 称 `fs::copy` 兜底"本身不留空窗" | 事实台账 | 低 | 语句局部成立，但兜底整体仍有两代窗口，且会留半份 `dst` |
| V5 | D1 未限定开关**作用域**，而打开路径实有三条分支 | 决策缺口 | **高** | 若套在整个回退段 → 第三条分支被一并关闭 |
| V6 | 主库缺失 + `.bak` 损坏 → 五语言**静默按空库打开** | 决策缺口 | **高** | 与 D1 原则 1 冲突，是第 5 处静默数据丢失，两份文档均未覆盖 |
| V7 | `readOnly=true` × `autoRecoverFromBackup=true` 组合未规定 | 决策缺口 | 中 | 该组合下回退会写盘（`fs::copy` / `File.Copy` / `Files.copy`） |
| V8 | D2-3"下一次写时重试"与 D2-4"跳过 2^n 次"互相矛盾 | 实施性 | 中 | 计数语义与首次重试点未定义 |
| V9 | 默认配置下压实失败只有 warning + 内存标志 | 实施性 | 中 | `needs_compaction` 无可观测通道，"文件无界膨胀"仍不可见 |
| V10 | Kotlin 生成 `.bak` 用 `Files.copy(REPLACE_EXISTING)` | 缺陷补漏 | 中 | 崩溃留下**半份 `.bak`**（丢失上一代），D1 把 `.bak` 定为人工恢复源后风险升高 |

> 阶段 0 的测试门槛需相应扩到 **T1–T17**（见 §6.3）。

---

## 1. 复核通过清单（抽样实读）

| 论断 | 实读证据 | 判定 |
| :-- | :-- | :-- |
| C1 顺序倒置 | `db.rs:1709-1714` 压实内嵌于 `append_entries` 尾部；`apply_mutation`（`db.rs:1729-1733`）先 `append_entries` 后 `apply_mem`；`commit_flush`（`db.rs:1854-1900`）序列化的是**当前** `self.data` 全量快照并覆盖 `.db` → 本次帧被覆盖 | 属实 |
| C2 Go 嵌套锁 + 静默吞 | `db.go:1688` `defer lock.Release()`（外层持锁覆盖整个函数）；`:1765-1770` 嵌套 `beginFlush`；`if err == nil { _ = db.commitFlush(...) }` | 属实 |
| C2 .NET / Dart | `Db.cs:1083` 外层持锁 → `:1178-1182` 嵌套 `BeginFlush`；`db.dart:358` → `:407-413` | 属实 |
| C3 批内清空未记墓碑 | `db.rs:1016-1028`、`db.go:1236-1247`、`Db.cs:1928-1936`、`db.dart:955-970` 四处同构；Kotlin 无 `clearNamespace` | 属实 |
| C4 中间坏帧即截断 | `format.rs:275-420` 各坏帧分支 `set_len` 后 `break`；`format.go:275-395` 同构 | 属实 |
| C5 Rust 先删后 rename | `rename_with_retry`（`db.rs:1957-1987`）每次尝试前 `fs::remove_file(dst)`；copy 兜底前**再次**删除（`db.rs:1979-1981`） | 属实 |
| G1 `.bak` 也会被截断 | `.bak` 走同一 `load_file`；`allow_tail_truncation = engine_mode == AppendOnlyLog && !read_only`（`db.rs:1608-1609`），回退分支 `db.rs:1570` 加载后 `fs::copy` 覆盖主库；Go `db.go:1384-1386` 同构 | 属实 |
| G1 反面（.NET 不会） | `Db.cs:376` 用两参 `ParseDatabaseBytes(bakBytes)`，默认不截断 | 属实 |
| M1 Rust / Dart / Kotlin 未对齐 | Rust `db.rs:1960-1962`；Dart `db.dart:322-336` `deleteSync()` → `renameSync`；Kotlin `CkvDb.kt:203-208` 用 `Files.copy`（非 rename）、`:214-216` 兜底 `delete()` → `renameTo` | 属实（Kotlin 形态见 V10） |
| M1 Go / .NET 已正确 | Go `os.Rename` 无预删；.NET `AtomicReplace`（`Db.cs:976-998`）用 `File.Move(overwrite:true)` / `File.Replace` | 属实 |
| C19 默认值矩阵 | Rust `options.rs:69/73/75` = 10000 / 100 / 4MB；Go `options.go` 同值；Dart `options.dart` = 5000 / 50 / 0；Kotlin `Options.kt:16/20` = 5000 / 100，**无阈值字段** | 属实 |
| C19 `syncOnWrite` 更正 | `Options.cs:33` 确实存在且被 `Db.cs:948,1301` 使用 | 属实 |
| C8 / C14 | `Options.kt:14-23` 无 `compactionThresholdBytes`；`compaction_dead_ratio` 仅声明无读取 | 属实 |
| D2 前提（`increment` 非幂等） | `db.rs:543-558` 先从内存读当前值再 `wrapping_add`，属 read-modify-write | 属实 |
| §7.2 测试基线 | 未在本轮复测（沿用 §7.2 实测），基线一致故有效 | 未复测 |

---

## 2. 事实台账更正（4 条）

### V1 `allowTailTruncation` 没有"可配"语言，五语言一律硬推

DECISION §1.1 表格最后一行与 FIX-PLAN §7.8 结论段均把它记为"Dart / Kotlin 可配"，**两处都不成立**：

| 语言 | 实际形态 | 证据 |
| :-- | :-- | :-- |
| Dart | **`DbOptions` 里没有该字段** | `options.dart` 构造参数全集只有 `readOnly / autoRecoverFromBackup / syncMode / syncIntervalMs / engineMode / compactionThresholdBytes / lockTimeoutMs / syncOnWrite / onWarning`；`db.dart:109-110` 由 `engineMode == appendOnlyLog && !readOnly` 推导 |
| Kotlin | **声明但从未被读取（死字段）** | `Options.kt:18` 声明 `allowTailTruncation = true`；`CkvDb.kt:104` 用的是**本地推导值**，`rg allowTailTruncation src/kotlin/` 除声明与 `Format.kt:174` 的形参外**无读取点** → 与 C14（`compaction_dead_ratio`）同类的第 2 个死选项 |
| Rust / Go / .NET | 无字段，由 `engineMode && !readOnly` 推导 | `db.rs:1608-1609`、`db.go:1428`、`Db.cs:355` |

**准确表述**：`allowTailTruncation` 在五语言中**一律由 `engineMode == AppendOnlyLog && !readOnly` 推导**，没有任何语言可配置；Kotlin 的声明是无效字段，Dart 连声明都没有。D1-6"不在本次收紧范围"的结论可保留，但理由须改写。

### V2 D1-4 的"独立告警码"已是现状，属空操作

`onRecoveredFromBackup` 只出现在 `.bak` 回退分支（Rust `db.rs:1574,1592`、Go `db.go:1388,1408`、Kotlin `CkvDb.kt:125,142`、.NET `Db.cs:380,404`、Dart `db.dart:129,151`）；残尾截断一律使用 `onTailTruncated`（`format.rs:282/300/328/344…`、`format.go:292/306/328/341/355/369/394`、`Format.kt:190/199/209/233/242`、`Db.cs:507/531/550/561/624`）。

→ 两者**早已分离**，D1-4 后半句"不再与残尾截断共用语义"无需实施，建议改写为"沿用既有的 `onRecoveredFromBackup` / `onTailTruncated` 分离语义"。§1.5 第三条（告警级别是否提升）仍可讨论。

### V3 C4 的"抛错 → 触发 .bak 恢复"表述已失效

FIX-PLAN §4 Fix C4 伪码末行与 M2 修正版（§7.3）都写"中间损坏 → 抛 `Corrupted`（**触发 .bak 恢复**，禁止截断）"。而 D1 已把自动回退默认关闭，该括号在默认配置下不再成立 —— 抛错之后**没有任何自动后续动作**。

→ 这是 §7.6 把 C13 并入阶段 1 时漏掉的回写：原设计中"抛错 → 回退"的闭环被 D1 拆断。须改为"抛 `Corrupted`（默认不自动回退，见 `DECISION` D1；`.bak` 仅作人工恢复源）"。

### V4 S3 的 copy 兜底表述不完整

S3 称"现有 `fs::copy(src, dst)`（`db.rs:1982`）本身不留空窗"。单句成立，但该路径**前面**仍有 `if dst.exists() { let _ = fs::remove_file(dst); }`（`db.rs:1979-1981`），因此兜底整体依旧存在"两代备份同时不存在"的窗口；且 `fs::copy` 失败会留下**半份 `dst`**。S3 需补这两点，并明确"失败时保留哪一代"。

---

## 3. 决策边界未闭合（3 条）

### V5 D1 未限定作用域：打开路径实有三条分支

| 分支 | 触发条件 | 证据（五语言） | D1 是否覆盖 |
| :-- | :-- | :-- | :-: |
| ① `.tmp` 晋升 / 清理 | 轮转中断（`.db` 不存在且 `.tmp` 校验通过 → 晋升；否则删除） | Rust `db.rs:1545-1556`、Go 等价段、.NET `Db.cs:325-345`、Kotlin `CkvDb.kt:95-100`、Dart `db.dart:95-105` | ❌ 未提及 |
| ② 主库损坏 → `.bak` | `load_file(主库)` 抛错 | `db.rs:1567-1583`、`db.go:1382-1398`、`Db.cs:369-393`、`CkvDb.kt:116-132`、`db.dart:128-137` | ✅ D1-1/D1-3 |
| ③ 主库缺失 → `.bak` 晋升 | 主库不存在且 `.bak` 存在 | `db.rs:1587-1599`、`db.go:1402-1419`、`Db.cs:398-411`、`CkvDb.kt:134-146`、`db.dart:147-160` | ❌ 未提及 |

**关于分支 ③ 的真实语义（实读结论）**：`commit_flush` 的顺序是"写 `.tmp` → `sync_all` → 关句柄 → 轮转 `.db → .bak` → `.tmp → .db`"（`db.rs:1903-1913`）。因此若崩在轮转中途，`.tmp` 必然已完整落盘，会先被分支 ① 晋升 —— **分支 ③ 无法由正常轮转崩溃触发**，它的实际作用是"主库被外部删除/替换时，静默用上一代 `.bak` 补齐"。

这与 D1 原则 1"能明确判定为损坏的状态一律抛错，不做自动降级"直接冲突：主库不存在 + `.bak` 存在，**不可能**是"全新库"，静默补齐即静默降级。

→ **需在 D1 增加一条**：开关作用域仅门控分支 ②；分支 ③ 单独规定（建议与 ② 同口径：默认拒开并提示 `.bak` 位置与显式恢复方式，因正常崩溃已由分支 ① 覆盖）。

### V6 分支 ③ 的"损坏子情形"静默开空库（第 5 处静默数据丢失）

主库缺失且 `.bak` **损坏**时，五语言全部静默落到"全新空库"：

| 语言 | 证据 | 形态 |
| :-- | :-- | :-- |
| Rust | `db.rs:1588-1599` | `if let Ok((...)) = self.load_file(&bak_path)` 不成立即穿透 → `current_max_tx_id = 0` |
| Go | `db.go:1403-1419` | `if loadErr == nil` 不成立即穿透 → `// New database` |
| Kotlin | `CkvDb.kt:134-146` | `catch (_: Exception) {}` 吞掉后无 `return` |
| Dart | `db.dart:147-160` | `catch (_) {}` |
| .NET | `Db.cs:406-410` | `catch (CorruptedError) { // 备份同样损坏且主库本就不存在：按新库处理，保留现场不清空 }` |

.NET 有注释说明这是**明确的设计意图**（其余四语言无注释），但该意图从未写入 `KV-DB-DESIGN-SPEC.md` 或任何决策记录。

→ 后果：现场同时存在损坏的 `.bak` 与"打开成功"的空库，调用方首次写入即生成全新 `.db`，**"数据丢了"被伪装成"打开成功"**（正是 D1 §0 判据 1 要禁的模式）。需显式决策：抛 `CorruptedError`（推荐，与 D1 一致），或保留现状但补 `onWarning` 告警码并写入规范。

### V7 `readOnly=true` × `autoRecoverFromBackup=true` 的组合未规定

- Rust `db.rs:210` **无条件**调用 `init_and_recover()`，回退分支用 `fs::copy` 写盘；只有残尾截断排除了 `read_only`（`db.rs:1608-1609`）。
- 即只读打开一个损坏库，会在用户明确声明"只读"的前提下改写磁盘文件。

DECISION §1.4 已把这个现象列为"变更影响"，但 D1-1…D1-6 未规定组合语义。

→ 建议明确：`readOnly=true` 时 `autoRecoverFromBackup` 不生效（忽略或直接拒绝该组合），且回退路径不得写盘。

---

## 4. D2 实施性疑问（2 条）

### V8 重试语义自相矛盾，"下一次写"与"跳过 2^n 次"冲突

- D2-3：**下一次写时重试** + `close()` 兜底；
- D2-4：失败后**跳过的写次数**按 `2^n` 退避（起始 64，上限 4096）。

到底是"紧接着的下一次写"还是"第 65 次写"？计数起点、复位条件（成功后归零？阈值变更后归零？）均未定义。

另：D2-3 的第二条兜底依赖调用方显式 `close()`，而 FIX-PLAN C16 自述"调用方常不 `close()`"。低写入频率场景下重试可能**永不发生**。

→ 建议补：① 计数语义与复位条件；② 至少一个不依赖调用方的触发口（时间下限，或显式 `compact()` 强制重试）。

### V9 默认配置下压实失败没有程序化可见通道

D2-1 的默认行为是 `onWarning("onCompactionFailed")` + 置 `needs_compaction`。但 `needs_compaction` 是实例内部字段，未定义对外查询接口；调用方若不注册 `onWarning`，**"文件无界膨胀"在默认路径上完全不可观测**。

→ 建议：把 `needs_compaction` / 上次失败原因 / 已跳过写次数暴露为可查询状态（如 `stats()` 或 `CompactStatus`），或强制要求告警携带"当前文件大小 / 阈值 / 失败原因 / 跳过年数"。

---

## 5. Kotlin `.bak` 的撕裂风险（V10，两份文档均未记录）

`CkvDb.kt:203-208` 生成 `.bak` 用的是：

```kotlin
Files.copy(dbFile.toPath(), bakFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
```

与 Rust 的"先删后 rename"（M1 记录的"两代窗口消失"）是**不同失效形态**：`REPLACE_EXISTING` 会先截断目标再写入，中途崩溃 → `.bak` 变成**半份文件**，上一代完整备份被覆盖丢失。

在 D1 把 `.bak` 重新定位为"人工恢复用的离线副本"之后，这处风险上升：损坏的 `.bak` 会让人工恢复同样失败，且无告警。

→ 建议与 M1 同批修：改为"写 `bak.tmp` → fsync → rename 覆盖"，与 .NET `AtomicReplace`（`Db.cs:988-998`）同构。M1 只记录了兜底路径的 `delete → renameTo` 窗口，需补此条。

---

## 6. 建议的修订动作

### 6.1 对 `DECISION-20261016-wal-recovery-and-compaction.md`

| # | 位置 | 动作 | 依据 |
| :-: | :-- | :-- | :-- |
| 1 | §1.1 表格 Dart / Kotlin 两行 | 改为"❌ 无此字段 / ⚠️ 已声明但从未被读取" | V1 |
| 2 | §1.1 表格下的说明或 §1.3 | 结论改为"五语言一律由 `engineMode == AppendOnlyLog && !readOnly` 推导" | V1 |
| 3 | §1.2 D1-4 | 删去"不再与残尾截断共用语义"，改为"沿用既有的两类告警码分离" | V2 |
| 4 | §1.2 新增 D1-7 | 明确开关作用域：**仅门控"主库损坏"分支**；分支 ③ 另行规定 | V5 |
| 5 | §1.2 新增 D1-8 | 主库缺失 + `.bak` 损坏：默认抛 `CorruptedError`，禁止静默开空库 | V6 |
| 6 | §1.2 新增 D1-9 | `readOnly=true` 时该开关不生效（回退不得写盘） | V7 |
| 7 | §2.4 | 补 D2 的计数语义、复位条件与"不依赖 `close()` 的触发口" | V8 |
| 8 | §2.4 | 补 `needs_compaction` 的可观测要求 | V9 |

### 6.2 对 `FIX-PLAN-20261016-wal-core-issues.md`

| # | 位置 | 动作 | 依据 |
| :-: | :-- | :-- | :-- |
| 1 | §4 Fix C4 伪码、§7.3 M2 | "触发 `.bak` 恢复"改为"默认不自动回退（见 D1）" | V3 |
| 2 | §4 阶段 1 伪码末行 | `// 错误必须上抛` 标记为已作废（D2-1 改为告警 + 重试，写返回成功） | D2-1 |
| 3 | §4 阶段 0 T6、§5 验收门槛 | 按 D2 改写 T6；门槛由 T1–T8 扩为 T1–T17 | D2-1 |
| 4 | §7.8 表下结论段 | 更正 `allowTailTruncation` 的"仅 Dart / Kotlin 可配" | V1 |
| 5 | §4 阶段 2 第 1 条、§7.5 S3 | 补 `db.rs:1979-1981` 的预删事实与"半份 `dst`"风险 | V4 |
| 6 | §4 阶段 1 / §7.6 | 新增 Kotlin `.bak` 撕裂修复，与 M1 同批 | V10 |
| 7 | §4 阶段 1 | 新增分支 ③ 与"主库缺失 + `.bak` 损坏"的处理（与 D1-7/D1-8 同批） | V5 / V6 |

### 6.3 阶段 0 测试门槛补充

| 编号 | 测试 | 覆盖 |
| :-- | :-- | :-- |
| T15 | 主库不存在 + `.bak` **损坏** → 断言不静默开空库（按 D1-8 取 `Corrupted` 或带告警） | V6 |
| T16 | `readOnly=true` + `autoRecoverFromBackup=true` + 损坏主库 → 断言**不写盘**、文件 mtime 不变 | V7 |
| T17 | 注入轮转中断 → 断言 `.bak` 要么完整、要么不存在，**不存在半份可解析失败态** | V10 |

---

## 7. 复核边界

- 本文档只做复核与文档修订建议，**未改动任何生产代码**，未执行 `git commit` / `git push`；
- 全部 `file:line` 基于 `dev` @ `3219d9c` 实读，与 §7.2 的测试基线一致；
- 本文档不替代 `CODE-REVIEW-20261016{a,b,c}.md`，只针对**两份计划/决策文档自身**的准确性与可实施性；
- 原两份文档的修订以**追加章节**形式落地（`DECISION` §6 / `FIX-PLAN` §8），原正文结论未被删除或改写，仅在过期处加一行作废指引；修订前原文已备份至
  `temp/backups/DECISION-20261016-wal-recovery-and-compaction.md.20261016-173810.bak` 与
  `temp/backups/FIX-PLAN-20261016-wal-core-issues.md.20261016-173810.bak`。

---

## 8. 与主流实现的对照校准（复杂度基准，2026-10-16 17:46:56）

> **校准判据（业主口径，2026-10-16 17:45）**：**不为"更安全"引入超出主流成熟实现的复杂度；
> 以对齐 SQLite / RocksDB / bbolt / LMDB / Badger / PostgreSQL 的既有实践为准绳。**
> **方法**: 逐项把 D1/D2 与 P0/P1/P2 的处置方式，对照主流实现的可查证行为，判定"对齐 / 超出 / 不足"。
> **重要前提**: CKV 的 WAL + 全量快照压实模型，与 LMDB（COW，无日志）/ RocksDB（LSM）不同，
> 属 SQLite / Badger 那一类"日志会增长"的引擎。**日志增长是该类引擎的固有属性，不是缺陷**——
> LMDB 文档对此有明确表述：WAL 与 append-only 引擎"otherwise they grow without bound"。
> 因此正确应对是**监控 + 显式压实 + 上限**，而不是设计更复杂的自动恢复。

### 8.1 逐项对照

| 缺陷 / 决策 | 主流实现的做法（可查证） | 本计划的做法 | 判定 |
| :-- | :-- | :-- | :-- |
| C1 压实时序 | 写路径只负责 WAL + 内存；刷盘/压实是独立阶段（RocksDB：memtable → immutable → background flush） | 压实移到 `apply_mem` 之后、独立锁周期 | 对齐 |
| C2 嵌套锁 | 主流不在用户写路径内做"必然失败的重入取锁"；压实不持用户锁 | 同样移至独立锁周期 | 对齐 |
| C3 批内清空语义 | RocksDB `WriteBatch` 按序原子应用，`DeleteRange` 的范围墓碑按序号覆盖同批 `Put` | 方案 A（批内影子状态补删除条目） | 对齐（方案 B 不必做） |
| C4 中间坏帧 | PostgreSQL：报 PANIC **拒绝启动**，需显式 `pg_resetwal -f` 才能继续；Badger：`ErrTruncateNeeded`，**默认不截断**；bbolt：返回 `ErrInvalid` / `ErrChecksum`，只报错不修复；KeyDB：格式错误无条件 `exit(1)`；**RocksDB 例外**：`wal_recovery_mode` 默认 `kPointInTimeRecovery`＝在损坏点停止并截断（`options.h:1503`），只有 `kTolerateCorruptedTailRecords` / `kAbsoluteConsistency` 才拒开 | 抛 `Corrupted` 拒绝打开 | 对齐**多数**；**严于 RocksDB 默认**（属有意从严，见 §8.7.1） |
| C4 / T5 残尾截断 | SQLite WAL 只认到最后一个合法提交帧；Badger 截到最后一个合法 entry | 截断到坏帧偏移 + 上报字节数 | 对齐 |
| D1 默认不自动回退 `.bak` | Badger 需显式 `Options.Truncate = true`；PostgreSQL 需 `pg_resetwal -f`；bbolt 无自动恢复 | 默认 `false` + 显式开关 | **强对齐** |
| D2 压实失败不使写失败 | **SQLite**：`if (rc == SQLITE_BUSY) { rc = SQLITE_OK; }` —— 明确"不因检查点未完成而报失败"，WAL 继续增长，靠重试；RocksDB：`Status::Severity::kSoftError` = "不阻止写入，但处于降级模式，后台压实可能不及时" | 告警 + 退避重试 + `close()` 兜底，本次写返回成功 | **强对齐** |
| V9 膨胀的可观测性 | SQLite：监控 `-wal` 文件大小 + `wal_checkpoint` 返回 `(busy, log, checkpointed)` 三元组；bbolt：`db.Stats()` 看 free pages；RocksDB：`Status::severity()` + `EventListener::OnBackgroundError` | 只有 `onWarning` | **不足，须补** |
| C12 恢复期结构校验 | 主流恢复都做校验（bbolt meta page checksum、Badger CRC32C、SQLite 页校验） | 校验 `value_type` 与定长长度 | 对齐 |
| C9 锁协议 | **bbolt**：`flock(db, !readOnly, options.Timeout)` 在 `Open` 时获取、**持有整个连接生命周期**，非阻塞尝试 + 50ms 重试至超时；**LMDB**：fcntl advisory lock，进程退出自动释放，stale reader 用 `mdb_reader_check` 清理 | 32B 内容 + 时间戳 + UUID，计划再加"steal 前 PID 活性探测" | **超出，应简化** |
| C14 死比例触发 | bbolt / SQLite 只有大小、页数触发；只有 Badger 有 discard 比例触发（因其 KV 分离、GC 成本模型不同） | 死代码，计划"要么实现要么删" | 建议删（见 8.2） |
| C15 常驻句柄 / 组提交 | 嵌入式库（SQLite / LMDB / bbolt）**不做组提交**；组提交是服务端（PostgreSQL / MySQL）概念 | 计划"评估常驻句柄 + 组提交" | **超出，可砍一半** |
| C16 后台线程持写锁 | 主流后台压缩不持用户可见写锁（作用于不可变快照） | 阶段 3 改为快照视图 + 短锁落盘 | 对齐 |
| C18 轮转后父目录 fsync | POSIX 上主流都做（rename 后 fsync 目录）；Windows 可跳过 | 阶段 3 补（仅 Unix） | 对齐 |
| C13 / `.bak` 语义 | 主流**没有**"自动回退上一代"机制，只有显式备份与显式恢复 | D1 重新定位为"人工恢复用离线副本" | 对齐 |

### 8.2 反超主流、应当砍掉或降级的 3 项

1. **C9 的第四点（steal 前加 PID 活性探测）——不要加这第三个启发式。**
   bbolt 与 LMDB 的做法是**内核锁 + 超时**：锁在 `Open` 时获取、持有到连接关闭，进程退出由内核自动释放，
   **根本不需要判活与窃取协议**。若采纳 C9-② 的 `flock` / `fcntl` 方案，则时间戳 / UUID / PID 判活与
   `steal` 逻辑应**整体删除**，而不是再叠一层。注：Kotlin 与 Dart 已经是内核文件锁（C19 锁协议行），
   五语言统一到内核锁后，代码量反而下降。
2. **C15 的组提交——砍掉。** 嵌入式单进程场景组提交没有收益，收益场景需要服务端式的多客户端队列。
   至多保留"常驻追加句柄"这一半（也可维持现状，优先级最低）。
3. **C14 `compaction_dead_ratio`——直接删字段。** v1.0 未发布，删除属发布前修订；
   bbolt / SQLite 都没有死比例触发。真需要时按 Badger 的做法再补（需维护 discard 统计），现在不必。

### 8.3 低于主流、必须补的 1 项

**V9（`needs_compaction` 可观测性）**：主流对"后台维护任务失败/滞后"都给正式通道
（SQLite 的 WAL 大小监控与 checkpoint 三元组返回、bbolt 的 `Stats()`、RocksDB 的 severity + 事件回调）。
本计划只有一条 warning 回调，调用方不注册即完全不可见。补一个查询接口（复用既有 stats 亦可）属于
**对齐主流的最低要求，不是额外复杂度**。

### 8.4 一处口径修正：`readOnly` 的精确含义（修正 D1-9 的措辞）

LMDB 文档明确：**"There is normally no pure read-only mode, since readers need write access to locks and lock file."**
即主流只读模式**允许写锁文件**，只是不写数据文件。

→ D1-9 应表述为：`readOnly=true` 时**不得修改 `.db` / `.bak` 数据文件**；触碰锁文件属正常行为。
这比上一轮"回退路径不得写盘"的措辞更准确，可避免实现者把锁文件一并禁掉而破坏跨进程互斥。

### 8.5 结论：可以照做，但按上表做 3 处削减

- **阶段 0 / 阶段 1（C1 + C2 + C3 + C4 + G1 + C10 + D1）**：修复方向与主流一致，**可以实施**。
  **C4 的判别算法建议按 §8.7.2 简化为类别判据**（净减少代码，替代 M2 的扫描算法）；
  动手前只需定 4 个"一句话级"口径：① 开关作用域（已写为 D1-7 / D1-8 / D1-9）；
  ② D2 退避的计数语义；③ `needs_compaction` 是否对外可见（建议可见）；④ 分支 ③ 是否并入阶段 1（建议并入）。
- **阶段 2**：C5 / C6 / C7 / C8 / C11 照做；**C9 改为"内核锁 + 连接生命周期持有"的重写，
  并删除判活 / steal 启发式**（独立批次，配 T10）。
- **阶段 3**：C12 / C16 / C17 / C18 / C20 / C21 照做；**C14 删字段**；**C15 只做常驻句柄、砍组提交**。
- **净效果**：比原计划**少 3 项改动、多 1 个查询接口**，整体复杂度低于原计划，
  且与 SQLite / bbolt / Badger / PostgreSQL 的既有实践一致；未引入任何主流没有的机制。

### 8.6 本节边界

- 主流行为均来自公开文档与官方 wiki（RocksDB Background Error Handling wiki、Badger Data Integrity、
  SQLite `wal_checkpoint_v2` 与 WAL 文档、bbolt `db.go` 与 errors 包、LMDB 官方文档、PostgreSQL `pg_resetwal` 文档）；
- **未在本机跑任何对照实验**，本节只用于复杂度取舍，不作为兼容性承诺；
- 本节结论不改变 §1–§7 的任何判定，只补充"该做到什么程度"的尺度；
- **§8.7 追加了本地源码实证复核**，其中**更正了本节 C4 一行对 RocksDB 的描述**（详见 §8.7.1）。

---

## 8.7 本地源码实证复核（`temp/github`，2026-10-16 17:53:30）

> **方法**: 对本地已下载的开源项目（FASTER / KeyDB / Lightning.NET / fjall / garnet / hive_ce / redb /
> rocksdb / rust-rocksdb）就"崩溃恢复 / 损坏判定 / 压实与截断"三类路径做源码实读，用于**校正 §8.1 的文档级结论**。
> **结论**: §8.1 主体判断成立，但**有 1 处必须更正**（RocksDB 的 WAL 重放默认行为）；
> 另发现**1 处可显著简化 C4/M2 的现成做法**（fjall），以及 4 处支持 D1 的独立先例。

### 8.7.1 必须更正：RocksDB 的 WAL 重放默认不是"拒开"

`rocksdb/include/rocksdb/options.h:412-450`（枚举定义）与 `:1503-1504`（默认值），本地源码：

| 模式 | 语义（原文要点） |
| :-- | :-- |
| `kTolerateCorruptedTailRecords` (0x00，原 levelDB 行为) | 容忍最后一条记录不完整、容忍预分配的零字节；**"in case a corruption is detected during recovery, this mode will refuse to open the DB"** |
| `kAbsoluteConsistency` (0x01) | 期望无任何损坏（单测与极少场景） |
| **`kPointInTimeRecovery` (0x02，默认)** | **"We stop the WAL playback on discovering WAL inconsistency"** —— 在损坏点停下，即截断 |
| `kSkipAnyCorruptedRecords` (0x03) | 无条件跳过损坏，尽力抢救（last ditch） |

→ **更正 §8.1**: 该行原先写"RocksDB：`paranoid_checks` 默认 `true`，置 `bg_error_`"，
把两套机制混为一谈——`paranoid_checks` / `bg_error_` 管的是**运行时后台错误**（运行期转只读），
与**开库时的 WAL 重放**无关；WAL 重放由 `wal_recovery_mode` 控制，**默认是"在损坏点停止并截断"**，
即 CKV 的**现状**，而非 C4 的目标行为。

**含义**：C4/T4（中间坏帧 → 拒开）**严于 RocksDB 的默认**，与 `kTolerateCorruptedTailRecords` /
`kAbsoluteConsistency` 同级。这不否定 C4（`DECISION` §0 判据 1 已明确"能判定损坏就抛错"），
但文档必须如实标注：**C4 是有意的从严选择，而非"对齐默认值"**。

### 8.7.2 可显著简化 C4/M2 的现成做法：fjall 的"类别判据"

`fjall/src/journal/reader.rs:53-78` 与 `journal/batch_reader.rs:64-73,107-129,174-177,93-121`（实读）：

| 失败类别 | fjall 的处置 | 位置 |
| :-- | :-- | :-- |
| 文件在条目中间结束（EOF / `UnexpectedEof`） | **截断到 `last_valid_pos` 并停止**（容忍） | `reader.rs:65-67`；`batch_reader.rs:64-73`（注释："missing terminator, but last batch, so probably incomplete, discarding to keep atomicity"） |
| 批次**校验和不匹配** | **返回 `ChecksumMismatch` 报错**（不忍耐） | `batch_reader.rs:127-129` |
| 批次长度不足（声明条目数未满足） | **返回 `InsufficientLength` 报错** | `batch_reader.rs:107-113` |
| 批次条目过多 | **返回 `TooManyItems` 报错** | `batch_reader.rs:174-177` |
| 结构异常（End/Item 无 Start、Start 嵌套） | 截断并停止（容忍） | `batch_reader.rs:93-121,165-171,195-202` |

即 fjall 的判据是 **"字节缺失 = 断电残尾（容忍）；字节在但校验不符 = 损坏（报错）"**，
而不是"向后扫描找合法帧"。据此对 M2 的建议：

> **建议（净减少代码）**：C4/M2 的算法可用 O(1) 类别判据替代——
> ① 读取本帧所需字节之前即到达文件末尾（short read）→ **残尾 → 截断到帧边界**；
> ② 剩余字节全为 0（预分配未写区；RocksDB 默认模式亦显式容忍零填充尾数据）→ **残尾 → 截断**（防御性，见下）；
> ③ 帧头/载荷字节齐全，但 magic / 帧头 CRC / payload CRC / entry 解码失败 → **损坏 → 抛 `Corrupted`**；
> ④ 全部校验通过但 `tx_id` 非单调 → 内部不变量被破坏 → 抛 `Corrupted`。
>
> 该判据不需要 magic 逐字节扫描、不需要候选数量上限；`tx_id` 单调性从"防误判手段"回归为"必查项"。
> M2 的"向后扫描"降级为**可选诊断**（仅在报错信息里说明"损坏点之后可能仍有可抢救数据"），不参与控制流。

**关于 ② 的必要性**：已 grep 确认 CKV 五语言写路径**没有任何预分配**
（`set_len` / `SetLength` 仅出现在恢复期截断：`format.rs:279/297/325/341/358/373/403`、`CkvDb.kt:113`、`Db.cs:363`），
故"尾部全零"在 CKV 自身写入下不会出现；② 仅作防御（个别文件系统崩溃后可能留下未写零区），成本一行。
注：hive_ce 与 Badger 都遇到过**平台相关的"伪损坏"**（Windows 预分配 / mmap 超配），
CKV 无预分配，不涉及该问题。

### 8.7.3 支持 D1（默认拒开、`.bak` 不自动回退）的独立先例

| 项目 | 行为 | 证据 |
| :-- | :-- | :-- |
| **hive_ce**（Dart，与 CKV Dart 端口同语言同模型） | 默认**拒开**并抛 `HiveError('Wrong checksum in hive file. Box may be corrupted.')`；仅当 `crashRecovery` 为真才截断。注释明写"A crash can only leave an incomplete frame at the end of the file"——**与 C4 的立论完全一致** | `hive/lib/src/backend/vm/storage_backend_vm.dart:138-161`；默认值 `hive_impl.dart:169,199` |
| **redb**（Rust） | 不能证明上次干净关闭时：warn `"Database ... not shutdown cleanly. Repair required"` 并 `return Err(DatabaseError::RepairAborted)` | `redb/src/db.rs:687-697` |
| **redb** 的显式修复入口 | `check_integrity()`：显式调用、可能很慢、"仅在怀疑被外部修改或引擎缺陷时使用"，返回 `Ok(true)` / `Ok(false, 已修复)` / `Err(Corrupted)` | `redb/src/db.rs:831-850` |
| **KeyDB**（C） | 格式错误（`fmterr`）→ **无条件 `exit(1)`**，即使 `aof-load-truncated` 为 yes；提示用 `keydb-check-aof --fix` | `KeyDB/src/aof.cpp:1058-1062` |
| **KeyDB** 的尾部容忍 | `uxeof`（意外 EOF）→ 默认 `aof-load-truncated = 1`（`config.cpp:2831`），截断到 `valid_up_to` 后继续 | `aof.cpp:1036-1053` |

三点推论：

1. **"显式修复入口"是主流普遍形态**（redb `check_integrity`、KeyDB `check-aof --fix`、SQLite `.recover`、
   PostgreSQL `pg_resetwal`——四例独立）。CKV 用 `autoRecoverFromBackup`（默认 `false`）实现同一意图，
   **形式更简、无需新增 API**；D1-3 的错误信息指引"人工用 `.bak` 覆盖"即等价于这些工具的语义。
2. **`allowTailTruncation` 默认开（D1-6 不收紧）符合主流**：KeyDB `aof-load-truncated` 默认 1、
   hive_ce `crashRecovery` 默认 `true`。→ D1-6 结论维持，且现有源码级依据。
3. **hive_ce 与 KeyDB 都把"EOF 可容忍 / 格式错误必致命"落在不同分支**（§8.7.2），
   即 C4 的判别方向在主流中**存在**，但实现方式是**类别判别**而非**向后扫描**。

### 8.7.4 顺带确认与排除

- **帧级原子性是对的，不必加复杂度**：KeyDB 遇到尾部不完整的 `MULTI/EXEC` 时**整体回退**到
  `valid_before_multi`（`aof.cpp:1004-1008`），即"尾部截断不得产生半个事务"。
  CKV 的 BatchFrame 天然满足（一帧 = 一次事务 + `tx_id` 单调），**无需改动**；
  只需保证 C4 的截断点落在**帧边界**（M2 与 §8.7.2 的判据都满足）。
- **FASTER 的 "truncate" 是日志头部 / GC 语义**（`cc/src/index/cold_index.h:616-634`），
  不是尾部恢复，与本题无关；`garnet`（服务端）、`Lightning.NET`（LMDB 绑定层）未展开。

### 8.7.5 本节边界

- 仅实读上表所列 `file:line`，**未在 CKV 上做对照实验**，也未编译或运行这些项目；
- 这些项目位于 `temp/github/`（临时区），版本以本地副本为准，未核对上游 commit；
- 本节**不改变 §8.1–§8.6 的取舍结论**，仅更正 §8.1 的 RocksDB 一行，并新增 §8.7.2 的简化建议。

