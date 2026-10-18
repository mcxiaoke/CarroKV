# 双审查报告核对与合并修复方案

> 生成时间：2026-10-18 14:28:24（本机时间）
> 核对对象：`docs/REVIEW-20261018-135612-full-code-review.md`（下称 **R1**）、`docs/REVIEW-20261018-141948-stp.md`（下称 **R2**）
> 核对基线：`git HEAD = 678ddfe`（工作树仅有上述两份未跟踪报告，其余干净）
> 核对方式：对两份报告中**互相矛盾、分级不一致、单方提及**的 38 条主张，逐条定位到当前 HEAD 的 `文件:行` 亲自复核；未逐条复核者沿用原报告结论并标注来源。

---

## 〇、一句话结论

两份报告**没有一条 P0 被推翻**（.NET 增量丢失、Dart flushAsync、Rust compact 三条全部成立），但**分级与覆盖面各有明显偏差**：R1 在 Rust 侧把 AOL 代际守卫误判为"已全链修复"，在 Kotlin Phase2 把可丢写的 P1 降级为"不可达"，并整体低估了规范硬性条款（HeaderFlags / `.corrupt`）的合规分量；R2 漏掉了 Rust `compact()` 关闭态防线（P0/R2 未列）、低估了 Go AOL 最终刷盘失效、并把 Go 的 AOL 代际守卫误记为已具备。合并去重后，**阻断项 26 条（P0×3 + P1×23）**，另有约 40 条 P2、50 条 P3 可分批清账。

---

## 一、核对方法与基线校正

### 1.1 基线差异（这是两份报告结论打架的根因之一）

| 项目 | R1（full-code-review） | R2（stp） |
| :--- | :--- | :--- |
| 报告锚定提交 | `678ddfe`（= 当前 HEAD） | `80bf68a`（= HEAD 的**父提交**） |
| 是否覆盖 678ddfe 的三项改动 | 覆盖 | **未覆盖** |

`80bf68a..HEAD` 只有 1 个提交 `678ddfe`，内容为：① Rust 后台线程 `join()` 自连接死锁修复；② Go `NewIoError`/`IsIoError` 补齐；③ Kotlin `DbError`/`KeyTooLargeError`/`ConcurrentModificationError` 三个 typealias + `normalizeNamespace` 收口。

**影响**：R2 关于"Kotlin 错误体系不完整""Go 缺 IoError 构造器"的判断在 `678ddfe` 上已部分过时，但**并不改变其余结论**——因为 678ddfe 未触碰任何写管线代码。R1 的"Kotlin 错误体系仍未达基线（CROSS-3）"在 HEAD 上复核成立（详见 §2.5）。

### 1.2 核对结果统计

| 结果 | 数量 | 说明 |
| :--- | :--- | :--- |
| ✅ 确认（两报告一致或单方主张经代码证实） | 30 | 见 §3 清单 |
| ⚠️ 分级需修正（问题成立、轻重判错） | 5 | HeaderFlags/.corrupt 各 1、Go AOL 刷盘、Kotlin Phase2、Go 轮转删 `.tmp` |
| ❌ 推翻（结论与 HEAD 代码不符） | 1 | R1 §10「Rust 后台刷盘代际守卫 ✅ FIX-01 全链」 |
| ➕ R1 漏报（R2 有、经复核成立） | 5 | Rust `compact()` 关闭态（P0）、Dart CoW 回归、Dart strictCompaction 回滚、.NET 关闭态时序、Kotlin AOL 写失败账本 |
| ➕ R2 漏报（R1 有或本次核对新发现） | 3 | R1 的发布链路断档、Rust 注册表锁内回调、**Go AOL 代际守卫缺失**（两报告均错判） |

---

## 二、六处关键裁决（冲突点逐条定案）

### C-1｜Rust AOL 后台刷盘代际守卫：R1 判"已修"，错

- R1 §10 对账表：「P1-27 后台刷盘代际守卫 → Rust ✅（FIX-01 全链）」。
- 代码实况（`src/rust/src/db.rs`）：
  - `:528-535` 计划阶段 AOL 分支返回 `(AppendOnlyLog, path, None, 0, 0)`——`generation` **硬编码 0**，从未采样 `dirty_generation`；
  - `:558-569` fsync 后 `if inner.options.engine_mode == AOL { inner.is_dirty = false; }`，**无任何 generation 比对**；
  - 对照快照分支 `:586-597` 才有 `inner.dirty_generation != generation` 守卫。
- **裁决：R2（N-X2）正确，R1 的"全链 ✅"只对 SnapshotAtomic 路径成立。** 后果：AOL + Deferred 下后台线程 fsync 与写者 append 交错，最后一段帧永不被 fsync 覆盖而脏标记已被清除，`close()` 也会因 `is_dirty=false` 跳过——掉电丢失"已确认成功"的写。

### C-2｜Go AOL 同款缺口：**两份报告都判错**

- R2 跨语言表把 Go 记为 ✅（引 `db.go:424`）。该行确实有 `if db.options.ReadOnly || !db.isDirty || db.dirtyGeneration != generation` —— 但那是 **Snapshot 分支**。
- AOL 分支在 `src/go/db.go:382-391`：`os.OpenFile(...).Sync()` 之后直接 `if engineMode == AOL { db.isDirty = false }`，同样**不比对 generation**。
- **裁决：AOL 代际守卫缺口是 Rust / Go / Kotlin 三语言，不是两语言。** R1 只报了 Kotlin（P1-10），R2 只报了 Rust+Kotlin，均漏 Go。

### C-3｜Kotlin Phase2 序列化实时 map：R1 判"不可达"，错

- R1（KOTLIN-5）定 P2，理由"现行锁序下不可达"；R2（N-X4）定 P1 可丢写。
- 代码实况（`src/kotlin/.../CkvDb.kt`）：
  - `:856-857` `val targetNamespaces = nsMap ?: namespaces`——但 `targetNamespaces` **只用于取 key 列表**（`:872` 排序、`:888` 遍历）；
  - `:890` 取值仍是 `namespaces[ns]!!`（活体 map），Phase1 提取的 `snapMap`（`:1182`）在取值处被弃用。
- R1 的"不可达"前提成立：`DEFERRED` 写路径（`:1143-1159`）虽然只取 `rwLock.write`、不取 `flushGate`，但**同一个实例只有一个 `syncMode`**——处于 `IMMEDIATE` 模式时不存在并发的 `DEFERRED` 写，其余写路径（含 `flush()`/`compact()`/`backup()`，见 `:1695/1720/1793`）也都会取 `flushGate`。
- **裁决（修订）：R1 正确，R2 的 N-X4"可丢已确认写"结论不成立**；本方案最初的"R2 正确"判断有误，已在实施时纠正。`namespaces[ns]!!` 仍然是错误的取值来源（`targetNamespaces` 只用于取 key 列表），但它当前没有可观测后果，属于防御性修复，降级为 **P3**（成本 1 行，仍然建议改，避免未来放宽锁序时踩雷）。

### C-4｜Dart `flushAsync`：两份报告各说对一半

- R1（P0-2）：await 窗口内的 `put()` 推进 `_currentMaxTxId`，尾部 `_currentMaxTxId = newMaxTxId` 把它覆写回旧值 + `_isDirty=false` → 丢写，且 TxID 复用可致重开 `CorruptedError`。
- R2（N-X6）：陈旧快照写入 `.tmp` 后 `rename` 覆盖 `.db` → 覆盖掉已被同步写提交的新一代；两个并发 `flushAsync` 还会交替写同一 `.tmp`。
- 代码实况（`src/dart/lib/src/db.dart:772-806`）：无任何 gate、无序号/代际变量；尾部 `_currentMaxTxId = newMaxTxId; _isDirty = false;` 无条件执行；`backupAsync`（`:1165-1171`）同构。
- **裁决：两条都是真问题，同一根因（既无串行化、也无代际栅栏）。** 修复必须同时满足两点，只做其中一项仍会复现另一种丢失。

### C-5｜HeaderFlags / `.corrupt.<ts>`：R2 分级正确，R1 低估

- 规范原文（`docs/KV-DB-DESIGN-SPEC.md`）：`:79`「`HeaderFlags` … 若读取端遇到未知非零必选位则拒绝打开」；`:167`「写者备份至 `.corrupt.<unix_ms>` 并截断主文件」。
- 代码实况：Rust `format.rs:23-44`、Go `format.go:112-135`、.NET `Db.cs:1001-1020` **均只校验 magic / readVer / CRC**，`12..16` 字节从未读取；Dart/Kotlin 同构。`.corrupt.` 字符串在 `src/` 全量 grep **零命中**（只出现在 `src/rust/tests/fault_injection_test.rs` 与 `src/dotnet/tests/FaultInjectionTest.cs`），即规范要求的取证备份**五语言均未实现**。
- **裁决：R2 的 P1 定级成立**（硬性条款 + 五语言集体缺位）。但需明确定性：这是**合规缺口与排障能力缺口**，不构成新增数据丢失源，排期上应归"合规批次"而非"阻断批次"。R1 把 .NET 的 HeaderFlags 记为 P2、把 `.corrupt` 放进 §7"设计空白"未定级，属低估。

### C-6｜Go AOL 最终刷盘失效：R1 定 P1 正确

- 代码实况（`src/go/db.go:503-507`）：`os.Open(db.path)`（O_RDONLY）+ `_ = f.Sync()` + 无条件 `db.isDirty = false`。Windows 上 `FlushFileBuffers` 需要写句柄，该调用失败且错误被丢弃。
- **裁决：R1（GO-2）正确，P1 保留。** R2 把它归入既往 P2-22/23，低估了"`Close()` 最终落盘静默变空操作"的契约破坏。

### C-7｜补充裁决：Rust `close()` 全局生效（R2 P1-5）

- 代码实况（`src/rust/src/db.rs:623-692`）：`close()` 直接置共享 `inner.closed = true`、`take()` 锁文件、注销注册表。`Db` 为 `Arc` 克隆语义，任一句柄 `close()` 对全部句柄生效。
- **裁决：R2 发现成立，属与冻结 API-SPEC §3.1「句柄级所有权」的冲突。** 但它是 `FIX-02(c)` 的有意设计（注释明确论证了"close 必须释放跨进程锁"），**不应作为 bug 直接改**——必须走决策（见 §6 D1）。

---

## 三、合并清单（按模块分类）

> 编号规则：`M<模块>-<序号>`。`来源` 列标 R1/R2 原编号；`级别` 为本次核对后**统一分级**；`工作量` 为单人估算（不含评审）。

### M1 格式层 / 加载与恢复

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| M1-01 | `HeaderFlags`(12..16) 非零不拒绝打开 | 五语言 | P1 | R2 P1-31 | 0.5d | ✅ 代码+规范双证 |
| M1-02 | 尾部截断前不写 `.corrupt.<unix_ms>` 取证备份 | 五语言 | P1 | R2 P1-32 | 1d | ✅ src 零命中 |
| M1-03 | `scan_max_tx_id` 不校验帧头 CRC/单调性/`payload_len` 越界 → 伪 TxID 后每写误报并发修改 | Rust、Dart、Kotlin | P1 | R1 RUST-5 / R2 P1-8 | 0.5d | ✅ `format.rs:512-541` |
| M1-04 | 未知 OpCode 静默忽略/丢弃（`when` 无 else），随后 flush 物理抹除 | Dart、Kotlin、Go | P2 | R2 N-D2 / P2-40 | 0.5d | ✅ |
| M1-05 | 加载期不校验 `valLen ≤ 16MB` | .NET、Go | P2 | R1 DOTNET-10 | 0.2d | 沿用 |
| M1-06 | 外来文件物理 `"default"` 分区不归一化 → 孤子分区读不到删不掉 | .NET、Go、Dart | P2 | R1 DOTNET-12 / R2 | 0.5d | 沿用 |
| M1-07 | `readVer` 校验排在 CRC 之前，位翻转报 `UnsupportedVersionError`（应 `CorruptedError`） | .NET、Rust | P3 | R1 DOTNET-16 | 0.2d | ✅ 同源 |
| M1-08 | `clearNamespace` 后空命名空间元数据重开即失（格式层无载体） | 五语言 | P3 | R2 KOTLIN-22 | 需规范 | 沿用 |

### M2 写管线与持久性（核心风险区）

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **M2-01** | `IncrementInt64` RMW 拆成"读锁读→无锁算→写锁存预计算值"，并发丢增量（实测丢 86%） | .NET | **P0** | R1 P0-1 / R2 N-X1 | 0.3d | ✅ `Db.cs:2802-2850` |
| **M2-02** | AOL 后台刷盘 fsync 后无代际比对即清 `is_dirty` → 掉电丢已确认写 | Rust、Go、Kotlin | P1 | R2 N-X2 / R1 P1-10 | 0.5d | ✅ 三语言确认 |
| M2-03 | AOL 追加部分写失败不回缩文件，撕裂尾帧永久化 → 同连接每写误报并发修改 | Rust、Go、Dart、Kotlin | P1 | R1 RUST-10/GO-3/DART-4、R2 N-K1 | 0.8d | ✅ 四语言同构 |
| M2-04 | `syncMode=MANUAL` 被当作 IMMEDIATE（唯一分叉） | Kotlin | P1 | R1 KOTLIN-1 / R2 N-X3 | 0.1d | ✅ `CkvDb.kt:1143` |
| M2-05 | Phase2 序列化读活体 `namespaces` 而非 Phase1 快照（当前锁序下不可达，防御性修复） | Kotlin | P3 | R2 N-X4 | 0.1d | ✅ 已修订见 C-3 |
| M2-06 | 关闭检查位于 `_flushGate` 之前，Dispose 可插入 → 关闭后落盘、脱离跨进程锁双写 | .NET | P1 | R2 N-X9 | 0.5d | ✅ `Db.cs:2087/2099` |
| M2-07 | 轮转失败 `os.Remove(tmpPath)`，与同文件 `commitFlush` 自相矛盾且可毁唯一新代 | Go | P1 | R2 N-X5 | 0.2d | ✅ `db.go:2545` |
| M2-08 | AOL 最终刷盘用 O_RDONLY → Windows 上 `Sync` 失败且静默清脏 | Go | P1 | R1 GO-2 | 0.3d | ✅ `db.go:503-507` |
| **M2-09** | `flushAsync`/`backupAsync` 无串行化、无栅栏 → 陈旧快照覆写 + TxID 回退 | Dart | **P0** | R1 P0-2 / R2 N-X6 | 0.6d | ✅ `db.dart:772-806` |
| M2-10 | `strictCompaction` 失败 rethrow 后被 `_rollback`，回滚已持久化的帧 | Dart | P1 | R2 N-X8 | 0.3d | ✅ `db.dart:1030-1045` |
| M2-11 | `_getOrCreateStoreForWrite` 无条件 CoW 克隆（性能债；**恢复 `_snapshotCount` 守卫会破坏回滚**，需改 undo-log） | Dart | P2 | R2 N-X7 | 1.5d | ✅ 已实证，见 §8 注 |
| M2-12 | 两阶段提交 precheck 同尺寸短路，等长外部替换永不检出 | Rust、.NET | P1 | R1 RUST-5/DOTNET-5 | 0.4d | ✅ `db.rs:2739` |
| M2-13 | `netstandard2.0` 轮转仍用 `File.Replace`（Unix 抛 `PlatformNotSupportedException`） | .NET | P1 | R1 P1-2 / R2 P1-16 | 0.3d | ✅ `Db.cs:1587-1601` |
| M2-14 | rename 退避只 `catch (IOException)`，WinError 5 直接落 `.stage` 兜底 | .NET | P1 | R1 P1-3 / R2 P1-14 | 0.2d | ✅ `Db.cs:1689` |
| M2-15 | 后台刷盘裸 `catch {}` 吞掉全部预检/IO 错误，零告警 | .NET、Dart、Kotlin、Go | P2 | R1 P1-4/DOTNET-4、R2 P1-35 | 0.5d | ✅ `Db.cs:538-541` |
| M2-16 | readOnly 打开仍执行 `.tmp` 晋升/删除（只读动盘） | .NET、Dart、Kotlin、Rust | P2 | R1 DOTNET-6/DART-7/KOTLIN-7/RUST-3 | 0.5d | ✅ Dart 已确认 |
| M2-17 | 两阶段提交 Phase3 部分失败状态分叉（`FileInfo.Length` 抛错即假死；失败路径删掉唯一新代） | .NET | P2 | R2 N-N1 | 0.4d | 沿用 |
| M2-18 | 空操作 batch 仍推进 TxID 并触发全库重写 | Rust、Kotlin | P2 | R2 N-R4/KOTLIN-17 | 0.3d | 沿用 |
| M2-19 | AOL 大批量 batch 按 1M ops/64MB 切多帧逐帧提交，破坏 all-or-nothing | Go、Kotlin | P2 | R2 N-G2/P2-38 | 0.8d | 沿用 |
| M2-20 | `DbOptions{}` 零值 `EngineMode=0` 绕开 `flushMu` 写 `.tmp` | Go | P2 | R1 GO-4 / R2 N-G3 | 0.2d | 沿用 |
| M2-21 | 两阶段提交 precheck 与轮转之间隔着整个锁外序列化，外部改写不复检 | Rust | P2 | R2 N-R1 | 0.3d | 沿用 |

### M3 单例 / 生命周期 / 关闭

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **M3-01** | `compact()` 无 closed/可写防线，close 后仍原子重写磁盘 | Rust | **P0** | R1 P0-3 | 0.1d | ✅ `db.rs:1723-1734` |
| M3-02 | 同进程并发首次 Open 双双新建，一方等锁超时抛 `LockedError`（单例契约破产） | .NET | P1 | R2 N-X10 | 0.5d | ✅ `Db.cs:662-712` |
| M3-03 | 注册表锁内同步调用 `onOptionsConflict` 用户回调 → 回调内 open 自我死锁 | Rust | P1 | R1 P1-1 | 0.3d | ✅ `db.rs:402-408` |
| M3-04 | 强类型读取两次独立取读锁（中间释放），并发删除即 `IndexOutOfBoundsException`/NPE | Kotlin | P1 | R1 P1-9 / R2 P1-28 | 0.4d | ✅ `CkvDb.kt:1482-1549` |
| M3-05 | `close()` 全局生效，无句柄级引用计数 | Rust | P1(决策) | R2 P1-5 | 1.5d | ✅ 见 D1 |
| M3-06 | `Snapshot` dispose 后读抛裸异常，违反 §3.5.3「温和返回空」 | Dart、Kotlin | P1(决策) | R1 P1-5 / R2 N-D3 | 0.3d | ✅ 规范 :153-154 |
| M3-07 | `.stage` 兜底残留无启动清理，可无界堆积 | Rust、.NET | P2 | R1 RUST-7 | 0.3d | ✅ `db.rs:2952` 创建无清理 |
| M3-08 | `batch(f)` 先执行用户闭包再报 `LockedError` | Rust | P2 | R1 RUST-8 | 0.1d | 沿用 |
| M3-09 | finalizer 路径 `TryEnterWriteLock` 失败后仍无锁刷盘 + 执行用户回调 | .NET | P2 | R1 DOTNET-8 / R2 N-N4 | 0.4d | 沿用（与 M2-06 相关） |
| M3-10 | `Snapshot.Close()` 无锁置 `namespaces = nil` 与并发读构成 race | Go | P2 | R1 GO-5 / R2 P2-29 | 0.2d | 沿用 |
| M3-11 | `Backup` 无 `dest==self` 防御（与主写路径争同一 `.tmp`） | Rust、.NET、Go、Dart | P2 | R1 RUST-6/DOTNET-9/GO-12、R2 N-G1 | 0.5d | ✅ Go 已确认 |

### M4 迭代 / 编码语义

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| M4-01 | `iterPrefix`/`scanRange` 不过滤非法 UTF-8 键（同文件 `keys()` 已过滤，自相矛盾） | Rust、.NET、Go、Dart、Kotlin | P1 | R1/DOTNET-15/DART-3/KOTLIN-10、R2 P1-36 | 0.6d | ✅ 口径分裂确认 |
| M4-02 | 写侧不拒绝孤立代理项，`U+D800` 与 `U+FFFD` 字节塌缩 | .NET、Dart、Rust | P2 | R1 DOTNET-11、R2 DART-13 | 0.5d | 沿用 |
| M4-03 | Kotlin 拒绝空键，其余四语言允许 → 同一 `.db` 跨语言行为不一致 | Kotlin | P2(决策) | R2 N-K5 | 0.1d | ✅ `CkvDb.kt:1784` |
| M4-04 | `NS("default").Namespace()` 返回未归一化的 `"default"` 而非 `""` | 五语言 | P3 | R2 N-C1 | 0.3d | 沿用 |
| M4-05 | `keys()` 的非法键策略未在规范定义 | 五语言 | P3(规范) | R1 RUST-9 | 需规范 | ✅ |

### M5 错误体系

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| M5-01 | value 超限抛 `ValueTooLongError`，逃出 `KeyTooLargeError` 体系（typealias 只桥了 Key 向） | Kotlin | P1 | R1 P1-11/P1-14 | 0.2d | ✅ `CkvDb.kt:1794-1796` |
| M5-02 | 根类为 `CkvException`，`DbError` 仅 typealias；规范外死类 `KeyNotFoundError`/`InvalidKeyError`/`PayloadTooLargeError` 无人引用 | Kotlin | P2 | R1 CROSS-3 | 0.3d | ✅ `Errors.kt` 全读 |
| M5-03 | `flush`/`compact`/`backup` 抛裸 `IOException`，绕过错误体系 | Kotlin、Dart、.NET | P2 | R2 N-K2、R1 P1-22 残留 | 0.5d | 沿用 |
| M5-04 | 仅 4/10 标准错误有 `Is*` sentinel | Go | P2 | R1 GO-8 残留 | 0.3d | 沿用 |
| M5-05 | `LockFile` 把一切 `UnauthorizedAccessException` 误诊为锁争用 | .NET、Dart | P2 | R1 DOTNET-13/DART-5 | 0.2d | 沿用 |
| M5-06 | `backup` 兜底抛裸 `StateError` | Dart | P2 | R1 DART-10 | 0.1d | 沿用 |

### M6 备份 / 压实

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| M6-01 | `Backup()` 在 `RUnlock` 之后读 `currentMaxTxID`（race detector 实证 exit 66） | Go | P1 | R1 P1-6 / R2 N-G1 | 0.2d | ✅ `db.go:1068→1081` |
| M6-02 | `backupAsync` 无 gate，与同步写交错覆写 `.tmp` | Dart | P2 | R2 N-X6 附带 | 0.3d | ✅ `db.dart:1165` |
| M6-03 | `strictCompaction` 失败路径丢弃已收集的 watcher 事件 | Rust | P2 | R2 N-R2 | 0.2d | 沿用 |

### M7 跨语言语义 / 规范

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| M7-01 | API-SPEC §3.1 四处脱节：`sync_mode` 写 `Always/Periodic`（实现 `Immediate/Deferred/Manual`，默认 `Immediate`）、`sync_interval_ms` 写 50（实现 100）、`auto_compaction_threshold`/`dead_ratio_compaction_threshold` 均已不存在 | 文档 | P1 | R1 CROSS-2 | 0.5d |
| M7-02 | 规范版本演进未执行（头写 v1.0 Frozen 但含 v1.1 内容；FREEZE 派生错误计数 8→10 过期） | 文档 | P2 | R1 CROSS-6 | 0.3d |
| M7-03 | 事件回调异常隔离语义五语言不一致且未文档化 | 规范 | P2 | R2 | 0.2d |
| M7-04 | Deferred 逐写 TxID++ 与快照批内连续的口径未声明（`stats().currentMaxTxId` 对账歧义） | 规范 | P3 | R1/R2 DART-14 | 0.2d |

### M8 CI / 发布 / 仓库

| 编号 | 问题 | 语言 | 级别 | 来源 | 工作量 | 核对 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| M8-01 | v0.2.1 有版本号、**无 tag、无 registry 产物**（`git tag` 仅 v0.1.0/v0.2.0，crates.io/NuGet newest 均 0.2.0），README 安装坐标不可解析 | 发布 | P1 | R1 CROSS-1 | 0.2d | ✅ tag 实测 |
| M8-02 | `extended` job 仍 `schedule/dispatch`-only，Go/Kotlin/Dart 无 PR 门禁，与 CHANGES 声明不符 | CI | P2 | R1 CROSS-4 / R2 §6 | 0.3d | ✅ `ci.yml:156` |
| M8-03 | Dependabot 移除后仅 Cargo 有 audit，Go/Gradle/Dart/Actions 无 CVE 扫描 | CI | P2 | R1 CROSS-5 | 0.3d |
| M8-04 | CHANGES 两处失实（P1-16"彻底修复"、extended"晋升 PR 门禁"） | 文档 | P2 | R1/R2 | 0.1d |
| M8-05 | workflow 无 `timeout-minutes`、crates.yml 缺顶层 `permissions`/`concurrency`、action 未 SHA-pin | CI | P3 | R1 CROSS-8 | 0.3d |
| M8-06 | `.gitignore` 缺 `build/`/`.gradle/`/`.dart_tool/` | 仓库 | P3 | R1 CROSS-7 | 0.1d |
| M8-07 | README 新旧 `sync_mode` 名称混用；Dart README 示例无法编译 | 文档 | P3 | R1 CROSS-11 / R2 | 0.3d |

---

## 四、修复方案（关键技术要点）

### 4.1 M2-01｜.NET `IncrementInt64` 原子化（P0）

**改法**：把"读当前值 + 类型校验 + 计算 + Put"整体搬进 `ExecuteMutation` 的内存改写回调，用回调内返回值取代外部预算值。

```csharp
// 删除 2807-2834 的读锁读值 + 外部预算
// 改为：
byte[] nsBytes = Encoding.UTF8.GetBytes(normNs);
byte[] valBytes = Array.Empty<byte>();      // 由回调填充
long newVal = 0;
return ExecuteMutation(
    // WAL 条目需要延迟构造 → 给 ExecuteMutation 增加一个 Func<T, IReadOnlyList<WalOpEntry>> 重载，
    // 或让 entries 参数接受 Func<IReadOnlyList<WalOpEntry>>
    entriesFactory: () => new[] { new WalOpEntry(OpPut, ValTypeInt64, nsBytes, keyBytes, valBytes) },
    affected: new[] { normNs },
    applyMem: clonedInTx => {
        var store = GetOrCreateStoreForWrite(normNs, clonedInTx);
        long cur = 0;
        int idx = store.FindIndex(keyBytes);
        if (idx >= 0) {
            if (store.Items[idx].ValueType != Constants.ValTypeInt64)
                throw new TypeMismatchError(...);
            cur = BinaryHelper.DecodeInt64(store.Items[idx].Value);
        }
        newVal = unchecked(cur + delta);
        valBytes = BinaryHelper.EncodeInt64(newVal);
        store.Put(keyBytes, ValTypeInt64, valBytes);
        return newVal;
    },
    makeEvents: _ => new List<KeyChangeEvent> { ... });
```

**要点/坑**：
- WAL 条目依赖计算结果，必须在内存改写之后构造。建议给 `ExecuteMutation` 增加 `Func<T, IReadOnlyList<WalOpEntry>>` 形式的重载，AOL/Deferred 分支在 `applyMem` 完成后再取条目；若不想动签名，`valBytes` 用闭包捕获的引用类型（如上）亦可，但**必须确保 entries 在落盘端消费时已填充**（`ExecuteMutation` 是同步方法，可行）。
- 失败回滚后 `newVal` 已被覆盖，不能用于返回值——返回 `result`（applyMem 返回值）即可。
- 类型校验必须在写锁内做，否则与"读时是 int64、写时被改成 string"的竞态仍存在。

**回归测试**（新增 `ConcurrencyTest.cs`）：8 线程 × 2000 次 `IncrementInt64(k,1)` → 断言 `16000`；再断言重开后仍 `16000`；再加一条"并发 increment + 类型篡改"断言抛 `TypeMismatchError` 而非静默覆盖。

### 4.2 M2-02｜AOL 后台刷盘代际守卫（Rust / Go / Kotlin）

统一模式（照 Snapshot 分支已有守卫改写）：

1. 计划阶段**两种引擎都采样** `generation = dirty_generation`（Rust `db.rs:528-535` 的 AOL 分支把占位 `0` 换成 `inner.dirty_generation`）；
2. fsync 成功后取写锁，比对 `dirty_generation == generation` 才清 `is_dirty`；
3. fsync **失败或文件打开失败时不清脏**，并按 M2-15 上报一次告警。

**关键语义**：不一致时放弃清脏（数据已 fsync 到"采样时刻"为止，新写入的帧留给下一轮），**不要重做 fsync**（下一 tick 自然收敛）。

**Rust 现状**：`:561` `if let Ok(f) = ... { let _ = f.sync_all(); }` —— 打开失败与 fsync 失败都被吞，需改为记录 `synced_ok` 再决定是否清脏。
**Go 现状**：`db.go:382-391` 需同样处理（O_WRONLY 打开失败不清脏）。
**Kotlin 现状**：`CkvDb.kt:689-703` 的 `generation` 变量在 AOL 分支从未赋值，需在 `rwLock.read` 计划段补 `generation = dirtyGeneration`。

**回归测试**：AOL + Deferred，注入可控 fsync 探针（Rust 用 `#[cfg(test)]` 钩子，Go 用 `var syncHook func() error` 变量，Kotlin 用可注入 `FsyncHook`），构造"sync 完成 → 写者 append → 置脏"序列，断言 `isDirty` 仍为 true 且 `close()` 会再刷。

### 4.3 M2-09｜Dart 异步提交栅栏（P0）

两件事必须一起做：

```dart
Future<void>? _flushGate;          // 串行化 flushAsync / backupAsync / 同步提交路径的异步段
int _flushSerial = 0;              // 每次内存变更自增，等价于其他语言的 dirtyGeneration

Future<void> _commitFlushAsync(int diskMaxTxId) async {
  final serialAtStart = _flushSerial;
  final txAtStart = _currentMaxTxId;
  // ...序列化、open、writeFrom、flush、close、rename（均为 await 点）
  if (serialAtStart != _flushSerial || txAtStart != _currentMaxTxId) {
    _isDirty = true;               // 有并发写，保持脏标记让下一轮收敛
    return;
  }
  _currentMaxTxId = newMaxTxId;    // 仅在没有并发写时才允许回写
  _isDirty = false;
}
```

- `flushAsync()` 改为 `while (_isDirty) { await _flushOnce(); }`，避免"一次 return 就丢脏"；
- 同步写路径（`_commitFlush` / `_appendEntries`）在每个提交点 `_flushSerial++`；
- 异步段整体用 `_flushGate` 串行（`Completer` 链即可，勿用锁阻塞事件循环）。

**回归测试**：把 `temp/probes/dart/flush_async_race_probe.dart` 收敛成 10 轮最小用例进 `test/`；断言重开后注入写全部存在、`currentMaxTxId` 不回退；再补"两个并发 flushAsync 不产生交错 `.tmp`"。

### 4.4 M2-03｜AOL 部分写失败回缩（Rust / Go / Dart / Kotlin）

统一：`append_entries` 写前记录 `startPos`，任何一步失败 `truncate(startPos)` 后再抛 `IoError`。

- Rust `db.rs:2645-2663`、Go `db.go:2340-2398`、Dart `db.dart:939-999`、Kotlin `CkvDb.kt:970-983`（Kotlin 当前 `finally {}` 为空，且失败后 `currentMaxTxId`/`lastKnownDiskFileSize` 不推进 → 后续 precheck 必然误报"外部修改"）。
- 仅在持连接写锁时执行（当前四语言该函数均在写锁内，安全）。
- 若 truncate 本身失败（文件被占用），退化为"标记 `_needsRecovery` + 抛 `CorruptedError` 提示重开"，不要留撕裂尾帧给下一次 precheck 反复误报。

### 4.5 M2-04 / M2-05｜Kotlin 两处一行级修复

- `CkvDb.kt:1143`：`if (options.syncMode == SyncMode.DEFERRED)` → `if (options.syncMode != SyncMode.IMMEDIATE)`；同时 `:462` 的后台线程启动条件补 `MANUAL`（否则 MANUAL 永不后台刷盘）。
- `CkvDb.kt:890`：`val store = namespaces[ns]!!` → `val store = targetNamespaces[ns] ?: continue`。

### 4.6 M2-06｜.NET 关闭态复检

在 `ExecuteMutation` 取到 `_flushGate` 之后、Phase1 写锁内**复检** `_root._isClosed`（不要只靠入口的 `ThrowIfDisposed`）；AOL 追加分支（`Db.cs:2208`）与 Deferred/Manual 分支（`:2226-2240`）在各自写锁内同样复检。

**锁序验证**：`CloseRoot`（`:3219-3260`）是先 `_flushGate` 再 `_rwLock`，与 `ExecuteMutation` 同序，因此"持 gate 后复检"不会产生逆序；但 `CloseRoot` 在 `Wait(2000)` 超时后仍会继续（M3-09），所以 M2-06 必须与 M3-09 同批修，否则 finalizer 路径仍有窗口。

### 4.7 M3-01｜Rust `compact()` 一行防线

```rust
let mut inner = self.inner.write();
inner.check_writable()?;                 // 新增：closed / read_only 统一拦截
let disk_max_tx_id = inner.precheck_disk_state()?;
```

同时把 `commit_flush` 里也补一层 closed 断言（`compact` 之外还有内部调用点），并补 `close → compact → 磁盘 mtime/字节不变` 回归测试。

### 4.8 M1-01 / M1-02｜五语言规范合规（HeaderFlags + `.corrupt`）

- HeaderFlags：在五语言 header 校验函数中，于 CRC 校验**之前**读 `12..16`，非零即 `UnsupportedVersion`（规范措辞是"拒绝打开"，语义上归版本/兼容类更准；若规范无明文错误类型，建议统一为 `Corrupted` 并补规范）。
- `.corrupt`：在"确诊断电残尾 → 截断"分支，截断前 `File.Copy(path, path + ".corrupt." + unixMs)`；仅在阶段二（enableRecovery）路径执行，只读模式跳过。五语言同构，建议先补 `testdata` 侧一条 golden 复核，避免动到格式层。
- **注意**：这两项会改变"打开损坏文件"的可观察行为，必须同步补 API-SPEC/DESIGN-SPEC 的错误类型说明，否则又是一处规范-实现脱节。

### 4.9 M3-02｜.NET 并发首次 Open

在 `LockFile.Acquire` **成功之后**、`InitAndRecover` **之前**复检注册表：命中活跃实例则释放本轮锁、`TryAddRef` 后返回门面句柄。这样两个线程中先拿到锁的完成初始化并注册，后拿到锁的直接复用，不再等 10 秒超时。

### 4.10 M6-01 / M2-07 / M2-08｜Go 三处

- `db.go:1081`：`finalMaxTxID := db.currentMaxTxID` 移入 `RLock` 内（与 entries 采集同一临界区）；
- `db.go:2545`：`rotErr != nil` 分支删掉 `_ = os.Remove(tmpPath)`，改为保留 `.tmp` + `isDirty = true` + 返回 `IoError`（与 `commitFlush:2143-2153` 对齐）；
- `db.go:503-507`：改 `os.OpenFile(path, os.O_RDWR, 0)`，`Sync` 失败或打开失败时**不清脏**并 `OnWarning("onFinalFlushFailed")`。

### 4.11 M1-03｜`scan_max_tx_id` 加固（三语言）

- 校验 `header_28` 的 HeaderCRC（覆盖 8..28）；
- `payload_len` 越界（`offset + 28 + payload_len > file_size`）→ `Corrupted`（区分"撕裂尾帧"与"中间坏块"的既有分类，不要一律按残尾截断）；
- 单调性：`tx_id < last_tx_id` → `Corrupted`。

**注意**：Rust `db.rs:2645-2663` 的 AOL 运行期也用该函数做 precheck，加固后若遇到运行期撕裂尾帧，行为从"伪 TxID 反复误报并发修改"变为"直接 Corrupted"——需与 M2-03 的 truncate 修复一起发布，否则会把"每次写报并发修改"变成"每次写报损坏"。

### 4.12 M4-01｜UTF-8 迭代口径统一

统一为"**跳过**非法键"（`keys()` 现行策略），并同步补 API-SPEC §3.3.3 至 `scanRange`/`keys`；Rust 的 lossy 保留必须改成跳过（否则 `iterPrefix` 会返回 U+FFFD 伪键指向错误数据）。Kotlin 因加载期强校验实际无害，可仅补测试固化。

---

## 五、实施计划

### 5.1 批次与排期

| 批次 | 内容 | 编号 | 估算 | 前置 |
| :--- | :--- | :--- | :--- | :--- |
| **A 阻断** | .NET 增量原子化、Dart 异步栅栏、Rust compact 防线 | M2-01、M2-09、M3-01 | 1.0d | 无 |
| **B 数据正确性** | AOL 代际守卫（3 语言）、AOL 部分写回缩（4 语言）、Kotlin MANUAL、Kotlin Phase2（P3 防御）、.NET 关闭态 + 并发首开、Go 三处、Dart 压实回滚 | M2-02/03/04/05/06/07/08/10、M3-02、M6-01 | 3.0d | A |
| **C 健壮性 + 合规** | HeaderFlags、`.corrupt`、`scan_max_tx_id`、同尺寸三元组、netstandard2.0、rename 退避、readOnly 动盘、错误体系 | M1-01/02/03、M2-12/13/14/16、M5-01 | 3.0d | B（M1-03 必须与 M2-03 同发布） |
| **D P2 清账** | 静默 catch/告警通道、未知 OpCode、多帧原子性、零值选项、备份自路径、.stage 清理、UTF-8 口径、M5/M6 其余 | M1-04~08、M2-15/17~21、M3-07~11、M4-01/02、M5-02~06、M6-02/03 | 3.5d | C |
| **E 收口** | v0.2.1 tag + 发布、CI 门禁、依赖扫描、规范 §3.1 重写与版本演进、README/CHANGES 勘误 | M7-01/02/03、M8-01~07 | 1.5d | D（发布必须在全绿后） |
| **合计** | | | **约 12 人日** | |

> 建议按"每批一次提交 + 一次 `docs/CHANGES-YYYYMMDD.md` 摘要"推进；不要跨批合并提交，否则回归定位成本会高于修复成本。

### 5.2 每批验收门（缺一不可）

1. 五语言格式化：`cargo fmt` / `dotnet format` / `gofmt -w .` / `dart format .`；
2. 静态分析零告警：`cargo clippy --all-targets -- -D warnings` / `dotnet format --verify-no-changes` / `go vet ./...` / `dart analyze` / `gradlew check`；
3. 全量测试：`cargo test --release`、`dotnet test`、`go test ./...`（含 `-race` 子集）、`dart test`、`gradlew test`；
4. 黄金向量：`testdata/MANIFEST.txt` 4/4 SHA256 不变（M1-01/02/03 动格式层时必须单独确认）；
5. 本批新增回归测试全部先在**未修复代码**上跑红，再修复跑绿（否则等于没测）。

### 5.3 批次内的推荐顺序（同文件冲突规避）

- 批次 B 中 **同一个文件只做一次提交**：`dart/db.dart`（M2-09/10/11/03）、`kotlin/CkvDb.kt`（M2-02/03/04/05）、`go/db.go`（M2-02/03/07/08/M6-01）、`rust/db.rs`（M2-02/03）、`dotnet/Db.cs`（M2-01/06）。
- M2-06（关闭态）与 M3-09（finalizer 无锁刷盘）建议同批，否则 gate 超时路径仍开窗。
- M1-03 与 M2-03 必须同批发布（见 4.11）。

---

## 六、需要拍板的决策项（不决策就无法动手）

| 编号 | 议题 | 选项 | 影响面 |
| :--- | :--- | :--- | :--- |
| **D1** | Rust `close()` 语义（M3-05） | A. 保持现状 + rustdoc 明示"全局副作用"；B. 改引用计数对齐 ADR（约 1.5d，触及全部 Rust 生命周期测试） | 冻结 API-SPEC §3.1；四条 P1 测试 |
| **D2** | Snapshot dispose 后读（M3-06） | A. Dart/Kotlin 改"温和返回空"（0.3d）；B. 修订规范承认抛错 | 跨语言一致性；用户迁移成本 |
| **D3** | 空键（M4-03） | A. Kotlin 放开（对齐其余四语言，1 行）；B. 五语言统一拒绝（改 4 语言 + 规范） | 同一文件跨语言写行为 |
| **D4** | netstandard2.0 TFM（M2-13） | A. `#else` 改"Delete+Move"退避；B. 砍掉 TFM | 发布矩阵、Unity/MAUI 用户 |
| **D5** | Dart `flushAsync` 语义（M2-09） | A. 完全串行化（推荐）；B. 允许并发但保证"最后一个写赢" | 异步 API 契约 |
| **D6** | M1-01/02 是否本期落地 | A. 本期（+1.5d，含规范同步）；B. 延后到 v0.3 | 合规缺口保留时间 |

### 6.1 建议裁决（本方案提出，待维护者确认）

| 编号 | 建议 | 采纳后代价 | 理由（含实证） |
| :--- | :--- | :--- | :--- |
| D1 | **A′：本期只做文档化 + 测试固化，引用计数排 v0.3** | 0.3d（原估 1.5d → 省 1.2d） | `Db` 为 `#[derive(Clone)]`（`db.rs:320`）共享 `Arc`，`close()` 全局生效；而 `FIX-02(c)` 注释已论证"close 必须立即释放跨进程锁"，改引用计数会把锁释放推迟到最后一个句柄，与 ADR 裁决正面冲突。**正确顺序是先裁定 API-SPEC §3.1 承诺的是句柄级还是共享级**，再决定改代码还是改文档。本期动作：rustdoc 明示全局副作用 + 新增 `close_shared_semantics_test` 固化现状。属契约问题、无数据后果，不应占用批次 B 预算。 |
| D2 | **A：Dart/Kotlin 改"温和返回空"** | 0.3d | 规范 `API-SPEC.zh.md:153-154` 明文"不抛出崩溃性异常"；改规范等于允许库在用户 dispose 后崩溃，是把实现偏差升级为设计缺陷。已核实两边**均无测试断言该抛错行为**，零回归风险。 |
| D3 | **A：Kotlin 放开空键** | 0.1d | 规范对空键**零规定**（`grep 空键/key_len/空字符串` 无命中）；Rust `industrial_suite_test.rs:59` 已实测 `put_string("", "", ...)` 成功，格式层 `key_len=0` 可表达。Kotlin 拒绝将导致**无法读取其他四语言写入的含空键库**，属互通破损。同步补规范一句"空键合法"。 |
| D4 | **A：保留 TFM，`#else` 改两段 rename + 补编译门禁** | 0.5d（原估 0.3d +0.2d 门禁） | `Ckv.csproj:4` 为 `netstandard2.0;net8.0`，双 TFM 包已发布，砍 TFM 属 breaking。修复面仅 `AtomicReplace` 的 `#else`（`Db.cs:1587-1601`）：`dst→.bak` + `src→dst` 两段 rename，与 Go/Rust 轮转同构且 `.bak` 天然可回退。**必须补门禁**（net48 或 netstandard2.0 消费方冒烟），否则仍是纸面修复；加不上则在文档标注"该 TFM 仅编译验证"。 |
| D5 | **A：完全串行化 + 链内代际比对** | 0.6d | Dart 单线程事件循环下并发 flush 无吞吐收益，只会在 await 点交错写同一 `.tmp`。"最后写赢"需版本向量与 rename 顺序处理，复杂度翻倍而收益为零。串行化 + 代际比对可同时消灭陈旧快照覆盖与 TxID 回退两个后果（见 §4.3）。 |
| D6 | **拆分：HeaderFlags 本期做；`.corrupt` 本期仅 AOL 截断路径** | 0.5d + 0.5d（原估 1.5d） | HeaderFlags 为读侧防护，现有写头 `flags` 恒 0（Go `format.go:112` `PutUint32(buf[12:16], 0)`），加校验**不可能误伤存量文件**，五语言各约 8 行 + 一条"flags=1 拒绝打开"测试即可。`.corrupt` 为写侧取证：新增磁盘文件、牵连只读模式（只读不应写盘）与阶段二恢复流程，1d 中半数为文档与测试，故先覆盖 Rust `format.rs` 五处中占四处的 AOL 截断路径，Snapshot 恢复路径延后 v0.3。 |

**排期影响**：D2/D3/D5/D6 均落在已在排期内的条目（M3-06、M4-03、M2-09、M1-01/02），不改批次结构；D1 采纳后省 1.2d，D4 采纳后 +0.2d。**总估算由 12 人日降至约 10.5 人日**，且未延后任何数据正确性风险（D1 的契约风险以"文档 + 测试"方式当期闭环）。

---

## 七、测试补强清单（去重合并，按优先级）

**必加（对应 P0/P1，共 14 条）**：
1. .NET：8×2000 并发 `IncrementInt64`（M2-01）
2. .NET：并发 Dispose vs 写（M2-06）；同进程双线程首开（M3-02）
3. .NET：`AtomicReplaceWithRetry` 遇 `UnauthorizedAccessException` 仍走完 5 次退避（M2-14）
4. Dart：flushAsync 交错写（M2-09）；`strictCompaction` 后内存/磁盘一致（M2-10）；无快照时单写不做全表拷贝（M2-11）
5. Rust/Go/Kotlin：AOL + Deferred 后台刷盘竞态（M2-02，三语言各一条）
6. Rust/Go/Dart/Kotlin：AOL 追加中途失败 → 同连接继续写 → 重开可读（M2-03）
7. Kotlin：MANUAL 写后文件长度不变、flush 后落盘（M2-04）；IMMEDIATE Phase2 × DEFERRED 写交错（M2-05）；并发读 + 删的压力（M3-04）；value>16MB 错误类型（M5-01）
8. Go：`Backup` 并发写 `-race`（M6-01）；轮转第二步失败后 `.tmp` 仍在（M2-07）；AOL `Close` 在 Windows 上真正 fsync（M2-08）
9. Rust：`close → compact` 磁盘不变（M3-01）；`onOptionsConflict` 内嵌套 open 不死锁（M3-03）
10. 五语言：`HeaderFlags=1` 拒绝打开（M1-01）；截断前生成 `.corrupt.*`（M1-02）

**次加（合规/一致性）**：等长外部替换 `.db` 期望并发修改（M2-12）；netstandard2.0 冒烟 TFM；readOnly 打开零 syscall 副作用（M2-16）；非法 UTF-8 键在 `keys`/`iterPrefix`/`scanRange` 三处口径一致（M4-01）；未知 OpCode 拒开（M1-04）；`Backup(dest==src)`（M3-11）；空键矩阵（M4-03）。

---

## 八、与两份报告的分级差异说明

| 条目 | R1 | R2 | 本方案 | 依据 |
| :--- | :--- | :--- | :--- | :--- |
| Rust AOL 代际守卫 | 判"✅ 已修" | P1 | P1（R1 结论作废） | `db.rs:528-535`、`558-569` |
| Go AOL 代际守卫 | 未提 | 判"✅" | P1（新增） | `db.go:382-391` vs `:424` |
| Kotlin Phase2 快照 | P2「不可达」 | P1 | **P3（R1 正确，C-3 已修订）** | `CkvDb.kt:890` + 单实例单 `syncMode`，无并发 DEFERRED 写 |
| Dart CoW 无条件克隆 | 未提 | P1 | **P2 性能债（实施时实证 R2 的修法不安全）** | `_recordRollback` 记录的是克隆前的 store，回滚依赖 CoW；恢复守卫会让 P0-7 回归（150 而非 100） |
| HeaderFlags | .NET P2 | 五语言 P1 | P1（合规） | 规范 `:79` + 五语言零校验 |
| `.corrupt` 备份 | §7 未定级 | 五语言 P1 | P1（合规） | 规范 `:167` + src 零命中 |
| Go AOL 最终刷盘 | P1 | P2 | P1 | `db.go:503-507` O_RDONLY |
| Go 轮转删 `.tmp` | 未提 | P2 | P1 | `db.go:2545` + 与 `commitFlush` 矛盾 |
| Kotlin AOL 写失败账本 | 未提 | P1 | P1 | `CkvDb.kt:970-983` 空 `finally` |
| Rust `close()` 全局生效 | 视为设计 | P1 | P1（决策） | `db.rs:623-692` + API-SPEC §3.1 |

---

## 九、诚实度声明

- 本文档 §2 的 6 处裁决、§3 中标注「✅」的 30 条，均由本次会话直接读取当前 HEAD 源码确认，附 `文件:行`；标注「沿用」者为未再逐行复核、沿用原报告结论。
- 未执行任何构建/测试（本仓库测试套件耗时长，且本次任务为文档核对）；因此运行时竞态结论仍以代码路径推断为准，修复后必须由 §7 的回归用例实证。
- 未修改任何源码；工作树仅新增本文件与两份原报告。
