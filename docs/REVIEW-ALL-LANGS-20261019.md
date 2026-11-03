# CKV 五语言全库深度审查报告

- 审查时间：2026-10-19 14:50 (UTC+8)
- 审查范围：`src/rust`、`src/go`、`src/kotlin` 全量源码；`src/dotnet`、`src/dart` 复核
- 审查重点：数据正确性 / 跨语言一致性 / 并发与错误处理 / 性能 / 测试与 CI 覆盖
- 审查方式：全量通读源码 + 可复现探针（`temp/review-probe/`，未改动任何库代码）+ 实跑测试与静态检查
- 基线：Rust 全量测试通过（0 失败）、Go 全量测试通过（0 失败）、Rust clippy 零警告；`docs/REVIEW-DOTNET-DART-20261018.md` 的全部发现经复核**均未修复**

---

## 0. 结论摘要

| 级别 | 数量 | 概要 |
| :--- | ---: | :--- |
| **P0** | 2（新增 1） | Dart `batch(delete(超长 key))` 毁库（上轮已报、未修复）；**新增：Rust/Go 的 `batch.delete(超长 key)` 同样会把 AOL 库写成不可读**——上轮"仅在 Dart"的结论低估了影响面 |
| **P1** | 8（新增 4） | 上轮 P1-1/P1-2/P1-3/P1-4/P1-5/P1-6/P1-7 全部未修复且**蔓延到更多语言**；新增 Rust/Go delete 入口校验缺失、前缀扫描全语言 O(n)、后台刷盘静默全语言、Kotlin open 自动建目录分叉 |
| **P2** | 10（新增 3） | 上轮 P2 大部分未修复；新增 Go 冗余调用、快照写放大蔓延 Go/Kotlin、bool 解码实为 3:2 分叉、`incrementInt64` 溢出语义未规范 |

**总体评价**：磁盘格式与崩溃恢复的设计质量极高，五语言在帧格式、CRC、TxID 单调性、torn-tail 分类、两代原子轮转、`.bak` 语义上高度对齐；Rust 的 Arc 快照与锁错误归因、Go 的 zero-tail 识别、Kotlin 的只读共享锁与 reentrant-open 冲突检查都值得肯定。**主要问题集中在两类**：① "batch 内 delete 缺输入校验"这条跨语言缺陷链——五语言只有 .NET 与 Kotlin 做对了；② 上轮报告的 .NET/Dart 问题没有任何一项被修复，且其中多数（写放大、整库读入、前缀扫描、后台刷盘静默）是**全语言共有**，不是 .NET/Dart 专属。

---

## 1. P0 —— 严重问题

### P0-1（Dart，未修复）`batch(delete(超长 key))` 把数据库写成永久不可读

与上轮报告完全一致，复核确认**仍存在**：

- 位置：`src/dart/lib/src/storage.dart:410-416`（`BatchWriter.delete`）—— 五个写入口中唯一没有 `validateLengths` 的。
- 后果：`RawOpEntry.encodeTo` 对 `key.length > 65535` 静默截断 `KeyLen`，条目实际字节数与声明不一致 → 载荷 CRC 能对上、条目解码时 `pIdx != pEnd` → `CorruptedError`。AOL 模式无 `.bak`，损坏帧在文件中间，**整库报废**。
- 上轮实测（temp/review-probe）：`batch(delete(hugeKey))` 调用正常返回 → `reopen: FAILED -> CorruptedError: Malformed entry payload`。

**修复方向（与上轮一致）**：`delete` 补 `validateLengths(keyBytes, Uint8List(0))`；编码层对 `key.length > 65535` 加兜底断言（纵深防御）。

---

### P0-2（新增：Rust / Go）`batch.delete(超长 key)` 同样能把 AOL 库写坏——跨语言缺陷链

**这是本轮最重要的新发现**：上轮把"batch 超长 delete"定位为 Dart 独有缺陷，实际上 Rust 与 Go 的 `Batch::delete` / `BatchWriter.Delete` **同样没有 key 长度校验**，且实测能把库写坏。五语言只有 .NET（`Db.cs:3563-3571`，`Limits.ValidateKeyAndValueLengths`）与 Kotlin（`Storage.kt:324-330`，`validateKey`）做对了。

| 语言 | Batch.delete 入口校验 | 单条目超长 delete | 多条目 batch（delete+put） |
| :--- | :---: | :--- | :--- |
| .NET | ✅ 抛 `KeyTooLargeError` | 安全 | 安全 |
| Kotlin | ✅ 抛 `KeyTooLongError` | 安全 | 安全 |
| Dart | ❌ 无 | **毁库（CorruptedError）** | 毁库 |
| Rust | ❌ 无 | **静默截断键**（删错键） | **毁库（CorruptedError）** |
| Go | ❌ 无 | **静默截断键**（删错键） | **毁库（CorruptedError）** |

**位置**：
- Rust：`src/rust/src/db.rs:2052-2059`（`Batch::delete`，无 `validate_kv_lengths`）；直接入口 `Db::delete`（`db.rs:1212`）同样无校验。
- Go：`src/go/db.go:1473-1484`（`BatchWriter.Delete`，无 `validateLengths`）；`sharedDB.Delete`（`db.go:3115`）同样无校验。

**实测（本轮探针 `temp/review-probe/rust-delete-probe`、`go-delete-probe`）**：

```
=== Rust, AppendOnlyLog, batch[delete(70000B key), put(k2)] ===
  write_batch: Ok
  reopen FAILED: CorruptedError("Failed to decode entry in payload at offset 74")   <-- 库报废
=== Rust, 单条目 batch[delete(70000B key)] ===
  write_batch: Ok；reopen OK —— 但 key 被静默截断成前 4464 字节（删除的是另一个键）  <-- 静默数据错乱
=== Go, 同一探针 ===
  [multi] Batch(delete huge + put k2): <nil>；reopen FAILED: CorruptedError: Failed to decode entry in payload at offset 74
  [direct] Delete(huge): <nil>；reopen OK（键不存在 → 无条目 → 安全，但入口无校验）
```

**为什么单条目不毁库、多条目毁库**：Rust/Go 的 `DecodeRawOpEntry` 循环解码后**不校验 `payloadOff == payload.len()`**（`format.go:262-269`、`format.rs` 同），单条目时截断键被静默接受；多条目时 `KeyLen` 截断导致 offset 错位，后续条目在错误位置解码 → 报 `CorruptedError`。Dart 的解码端检查 `pIdx != pEnd`，所以单条目即毁库——**这是解码端严格度的又一处跨语言分叉**。

**危害等级与 Dart 相同**：静默（调用返回 Ok）+ 不可自愈（AOL 无 .bak，损坏帧在中间）+ 全量丢失 + 跨语言 3:2 行为分叉。

**修复方向**：Rust `Batch::delete` 与 `Db::delete` 补 `validate_kv_lengths(key_bytes.len(), 0)`；Go 的 `BatchWriter.Delete` / `sharedDB.Delete` 补 `validateLengths(keyBytes, []byte{})`；并在 `RawOpEntry::Encode`（Rust/Go/Dart）与 `encodeTo`（Kotlin）对 `key.len()/ns.len()` 超限做兜底断言，把"上游漏校验"从"毁库"降级为"抛异常"。

---

## 2. P1 —— 重要问题

### P1-1（全语言）每次写整块克隆命名空间 → AOL"与规模无关"承诺不成立

- .NET `Db.cs:2162-2173 / 2275-2279`、Dart `db.dart:1110-1136`：**未修复**（上轮实测：15 万条目下 .NET 吞吐腰斩、每次写 ~1.2 MB 垃圾；Dart 慢 8 倍）。
- **新增确认：Go 同样深克隆**（`db.go:1776 / 1781` 的 `store.clone()`）、**Kotlin 同样深克隆**（`CkvDb.kt:396-416` 的 `existing.clone()` + `Storage.kt` 的 clone 还要重算 `liveBytes`）。
- **唯一例外是 Rust**：`db.rs` 用 `Arc<NamespaceMap>` 共享内层、`SnapshotAtomic`/`Immediate` 只克隆外层 map（O(命名空间数)），快照与回滚都是 O(1) —— 这是正确做法，其余四门语言应向它看齐。

**修复方向（全语言统一）**：回滚点从"整块克隆"改为 **undo-log**（按 key 记录旧值/旧存在性，失败逐条反向回放），或像 Rust 一样改用共享不可变结构 + CoW。

### P1-2（.NET，未修复）`Utf8StringComparer` 的 `Compare` 与 `Equals` 自相矛盾 → 命名空间串号

`BinaryHelper.cs:141-170`：`Compare` 用 `UTF8.GetBytes`（孤立代理项 → U+FFFD），`Equals` 用 Ordinal。两个不同字符串 `"\uD800"` / `"\uD801"` 编码成同一字节序列，`SortedDictionary` 以 `Compare` 判定同键 → **两个命名空间塌缩成一个，写入 A 的值被 B 静默覆盖且重开依旧**。上轮实测复现，复核确认未修复。

**修复方向**：`Equals(x,y) => Compare(x,y) == 0`，并对孤立代理项在 `NormalizeNamespace` / key 入口显式拒绝（抛 `InvalidNamespaceError` / `KeyTooLargeError`），禁止静默写 U+FFFD。

### P1-3（.NET / Dart，未修复）`open()` 错误归因：目录缺失 → 空转超时后报"被占用"

`.NET LockFile.cs:59-83` / `Dart lock.dart:40-63` 把所有打开失败（含 `DirectoryNotFoundException` / `PathAccessException`）都当锁冲突重试到超时。上轮实测：默认配置 10 秒无响应 + 误导性 `LockedError`。**注意**：Rust（`lock.rs` 区分 `is_contention`，父目录缺失立即 `IoError`）、Go（`lock.go` 同样归因）、Kotlin 不受此问题影响——只有 .NET/Dart 需要修。

### P1-4（.NET / Dart，未修复）`open()` 不校验路径类型：目录路径打开出"幽灵库"

目录路径被当成全新空库打开成功，首次写才报 rename errno，且 `.stage` 兜底路径留下垃圾文件。上轮实测复现，复核确认未修复。Rust/Go/Kotlin 无此问题（Kotlin 见 P1-8 的反向分叉）。

### P1-5（全语言）打开时整库读入内存，峰值约为文件 2.8 倍

- .NET `Db.cs:904` `File.ReadAllBytes`、Dart `db.dart:509` `readAsBytesSync`：**未修复**（上轮实测 12.65 MB 文件 → +34.8 MB 托管堆）。
- **新增确认**：Rust `format.rs` `load_all`/`load_with_recovery` 逐帧 `read_exact` 但把全部条目物化成 `Vec<RawOpEntry>`；Go `LoadAllFrames` 同样全量收集 `[]*RawOpEntry`；Kotlin `loadAllWithRecovery` 读整份 `ByteArray` + 物化 `LoadOp` 列表。**五语言全部是全量物化**，100 MB 级库在移动端会 OOM。

**修复方向**：改为流式解析（逐帧读入可复用缓冲、直接 replay 进 store，不物化全量中间列表）——改动量最大但收益最大。

### P1-6（全语言）`keys(prefix)` / `iterPrefix(prefix)` 是全命名空间 O(n) 扫描

- .NET `Db.cs:3073-3100 / 2489-2516`、Dart `db.dart:1399-1440`：**未修复**。
- **新增确认**：Rust `db.rs` `keys`/`iter_prefix`/`scan_range` 全表遍历（**没有用 `BTreeMap::range`**，这是 Rust 最不该犯的）；Go `db.go:1298-1304`（`Keys` 全表）；Kotlin `CkvDb.kt:1624-1638`（全表 + 每个 key 一次 `isValidUtf8`）。底层 `NamespaceStore` 全部有序、`findIndex` 已在用二分，唯独前缀扫描不用二分起点。只有 .NET/Dart 的 `scanRange` 做对了。

**修复方向**：前缀起点用二分定位（有序数组：`FindIndex(prefix)` 或 BTreeMap `range(prefix..prefix+\u{FF})`），命中 k 时 O(log n + k)。

### P1-7（全语言）后台刷盘完全静默吞异常

- .NET `Db.cs:538-541`、Dart `db.dart:625-630`：**未修复**（裸 `catch {}` / `catch (_) {}`，无 `onWarning`）。
- **新增确认**：Rust `flush_if_dirty` 的序列化/写盘失败直接 `return`（无告警）；Go `flushIfDirty` 各 `err != nil { return }`（无告警）；Kotlin `flushIfDirtyLocked` `catch (_: Exception) { null }`（无告警）。**五语言一致静默**。`Deferred` 模式下后台刷盘是唯一自动落盘路径，磁盘满/权限变更时用户拿不到任何信号。

**修复方向**：统一 `onBackgroundFlushFailed` 告警 + 失败计数/退避，避免每 tick 刷屏。

### P1-8（新增：Kotlin）`open()` 自动创建父目录 → open 语义跨语言分叉

`LockFile.kt:38` 的 `lockFile.parentFile?.mkdirs()` 会**静默创建缺失的目录树**。其他四语言（含 Kotlin 之外的恢复逻辑）都不会这么做。后果：
- 拼错路径（多打一层目录）时 Kotlin 会"帮你建好目录然后打开成功"，其他语言报错——排障行为不一致；
- 目录路径上有权限问题时 `mkdirs` 失败 → 异常被 `catch (_: Exception)` 吞掉当锁冲突重试 → 仍然空转超时（P1-3 的残余面）。

**修复方向**：与五语言对齐——open 前显式校验父目录存在且为目录，不存在直接抛 `IoError`；移除 `mkdirs` 或仅在校验后显式创建（若产品有意支持自动建目录，需写进规范并让五语言一致）。

---

## 3. P2 —— 建议改进

| # | 语言 | 问题 | 位置 / 说明 |
| :-- | :-- | :-- | :-- |
| P2-1 | Dart | 同步 API 阻塞事件循环；`replaceFileAtomicallyWithRetry` 退避用 `sleep()`，单次轮转最多阻塞 isolate 230 ms（Flutter UI 线程不可用）。 | `db.dart:1911-1938`，未修复 |
| P2-2 | Dart | 跨 isolate / 跨进程不可用：连接锁持有整个生命周期，`compute()` / `Isolate.run` 直接 `LockedError`。 | `db.dart:98-123`，未修复 |
| P2-3 | **五语言** | **bool 解码分叉实为 3:2**：.NET `buf[0]!=0`、Rust `bytes[0]!=0`、Go `buf[0]!=0`（宽松，0x02 静默当 true）；Dart / Kotlin 对非 0/1 抛 `TypeMismatchError`（严格）。加载期只校验 `ValLen==1` 不校验字节取值。定长类型长度不符时报错类型也分叉（.NET/Rust/Go `Corrupted` vs Dart/Kotlin `TypeMismatch`）。 | 上轮只报了 .NET/Dart 两端，实际是三对二 |
| P2-4 | Dart | `getString()` 对同一值最多完整解码 3 遍（`isValidUtf8` 一遍 + 返回解码一遍 + `getBytes` 重复 `findIndex`）。 | `db.dart:1326-1342`，未修复 |
| P2-5 | .NET / Rust | `Stats()` / `fileSize()` 文档写"不触盘"但实现调 `FileSizeInternal` / `fs::metadata`（一次 stat）。 | `Db.cs:2381-2412`、`db.rs stats()`，未修复 |
| P2-6 | .NET / Dart | 锁退避固定间隔（10 ms / 50 ms），规范要求 10–100 ms 指数退避。Rust/Go/Kotlin 已用指数退避（5→100 ms），只 .NET/Dart 落后。 | `LockFile.cs:70`、`lock.dart:62`，未修复 |
| P2-7 | .NET | `Utf8StringComparer.GetHashCode`/`Compare` 每次比较都 `UTF8.GetBytes` 新分配数组，挂在每次写都访问的 `_namespaces` 上。 | `BinaryHelper.cs:145-168`，未修复 |
| P2-8 | .NET | `~CkvDb()` 终结器线程同步落盘 + 2 s 超时释放内核锁，终结器延迟时 `Open` 拿到"看着活着、实际已死"的实例。 | `Db.cs:3302-3374`，未修复 |
| P2-9 | .NET / Dart | 只读打开仍先把 `.bak` 读进内存再决定拒绝恢复。 | `db.dart:587-607`、`Db.cs:986-1008`，未修复 |
| P2-10 | **Go（新增）** | `LoadAllFramesWithRecovery` 中 `evidence.ensure(file)` 在 5 处被**重复调用两次**（`format.go:399-400, 414-415, 441-442, 458-459, 473-474`）。幂等无功能影响，属复制粘贴冗余，应删。 | `src/go/format.go` |
| P2-11 | **五语言（新增）** | `incrementInt64` 溢出语义未写入规范：Rust 显式 `wrapping_add`，Go/Dart/Kotlin/.NET 默认回绕——目前行为恰好一致，但规范未定义，未来任何一门改用 checked/saturating 都会分叉。建议在 API-SPEC 里明确"回绕（two's complement）"。 | `db.rs:1160` 等 |

---

## 4. 上轮（2026-10-18）问题修复状态核验

| 上轮编号 | 问题 | 状态 | 复核位置 |
| :-- | :-- | :-- | :-- |
| P0-1 | Dart batch(delete 超长 key) 毁库 | **未修复** | `storage.dart:410-416`（仍无 validateLengths） |
| P0-2 | .NET/Dart clearNamespace 竞态（键复活） | **未修复** | `Db.cs:3030-3065`（仍在读锁内采集、锁外清内存）；Dart 单 isolate 恰好无并发 |
| P1-1 | 每次写 O(n) 克隆 | **未修复**（且确认 Go/Kotlin 同病） | `Db.cs:2162-2279`、`db.dart:1110-1136`、`db.go:1776`、`CkvDb.kt:396-416` |
| P1-2 | .NET 命名空间比较器自相矛盾 | **未修复** | `BinaryHelper.cs:141-170` |
| P1-3 | open 错误归因（目录缺失空转） | **未修复**（仅 .NET/Dart） | `LockFile.cs:59-83`、`lock.dart:40-63` |
| P1-4 | 目录路径幽灵库 | **未修复**（仅 .NET/Dart） | `Db.cs` open 路径无类型校验 |
| P1-5 | 整库读入内存 | **未修复**（且确认 Rust/Go/Kotlin 同病） | 见 P1-5 新条目 |
| P1-6 | Keys(prefix) 全表扫描 | **未修复**（且确认 Rust/Go/Kotlin 同病） | 见 P1-6 新条目 |
| P1-7 | 后台刷盘静默吞异常 | **未修复**（且确认 Rust/Go/Kotlin 同病） | 见 P1-7 新条目 |
| P2-3 | bool 解码分叉 | **未修复**（实为 3:2） | 见 P2-3 新条目 |
| P2-4/5/6/7/8/9 | 各项小改进 | **均未修复** | 见 P2 表 |

**结论**：自 2026-10-18 审查至今，`CHANGES-20261018.md` 记录的是**更早**的修复批次（FIX-01…FIX-22、C1…C16 等），上轮报告的 18 项发现无一项落地。上轮 P0-2 与 P0-1 至今仍是真实风险。

---

## 5. 跨语言行为分叉总览

| 维度 | .NET | Rust | Go | Dart | Kotlin |
| :--- | :--- | :--- | :--- | :--- | :--- |
| batch.delete 长度校验 | ✅ 抛错 | ❌ 毁库/截断 | ❌ 毁库/截断 | ❌ 毁库 | ✅ 抛错 |
| 直接 delete 长度校验 | ✅ | ❌ | ❌ | ✅* | ✅ |
| bool=0x02 解码 | true（宽松） | true（宽松） | true（宽松） | 抛错（严格） | 抛错（严格） |
| 定长解码长度错误类型 | Corrupted | Corrupted | Corrupted | TypeMismatch | TypeMismatch |
| 锁退避 | 固定 10 ms | 指数 5→100 ms | 指数 | 固定 50 ms | 指数 5→100 ms |
| 锁错误归因（目录缺失） | ❌ 重试超时 | ✅ 立即 IoError | ✅ 立即 IoError | ❌ 重试超时 | ✅（但自动建目录） |
| 只读连接锁 | 排他 | 排他 | 排他 | 排他 | **共享锁**（bbolt 语义） |
| open 缺失父目录 | 报错 | 报错 | 报错 | 报错 | **自动创建** |
| 回滚/快照内存模型 | O(n) 克隆 | **O(1) Arc 共享** | O(n) 克隆 | O(n) 克隆 | O(n) 克隆 |
| 前缀扫描 | O(n) | O(n)（未用 range） | O(n) | O(n) | O(n) |
| 后台刷盘失败上报 | 静默 | 静默 | 静默 | 静默 | 静默 |

\* Dart 直接 `delete`（`db.dart:1595`）无显式校验，但"键不存在即返回 false、不产生条目"，超长键通常不存在于库中，实际风险低于 batch 路径；防御纵深仍建议补齐。

---

## 6. 值得保留的设计（防回归清单）

审查中确认这些实现是正确的、跨语言对齐的，后续改动不应破坏：

1. **帧格式与恢复**：`FileMagic UKVDB003`、HeaderFlags 固定 0 且读取端拒绝未知非零位、CRC 先验后判标志位、非单调 TxID 拒绝、`opCount/payloadLen/nsLen/keyLen/valLen` 全部上限校验、未知 opcode 拒绝打开（M1-04）——五语言一致。
2. **torn-tail 分类**：残尾截断只允许在尾部；完整帧但 CRC/编码错 = 介质损坏必须 `CorruptedError` 绝不静默截断；zero-filled tail 视为未写尾部（Go/Rust/Kotlin 的 `tailRegionIsAllZero`，Rust/Go 的 `.corrupt.<ts>` 证据复制）。
3. **连接锁协议**：锁文件只创建不删除、无时钟判活、无窃取；Kotlin 的只读共享锁（读-读不互斥）是优化方向，建议评估推广到其余语言。
4. **Rust 内存模型**：`Arc<NamespaceMap>` + 外层 map 克隆，快照/回滚 O(1)——四门语言回滚重构的参照。
5. **Rust/Go/Kotlin 的磁盘预检**：`precheckDiskState`/`beginFlush` 缓存 `(maxTxId, fileSize)`，文件大小未变不重扫；外部替换文件（含 TxID 变小）能检出并抛 `TransactionConflictError`。
6. **Kotlin 错误层次**：`KeyTooLongError` 为根、`ValueTooLongError` 为子类 + `typealias KeyTooLargeError`，既符合 API-SPEC 又兼容旧 catch。
7. **Kotlin reentrant-open 冲突检查**：`readOnly` 冲突抛 `ConflictingOptionsError` 防静默提权；engine/sync 冲突走 `onWarning`。
8. **Kotlin batch 内读己写**：staging 区从后向前扫描、墓碑优先，语义正确。
9. **CI 门禁**：golden vectors SHA256 校验（防向量被静默修改）、五语言 interop 链（每语言写夹具、其余四语言读）、扩展静态检查在每次 PR 全量跑（M8-02）、Rust clippy `-D warnings`。

---

## 7. 测试 / CI 覆盖评估

- **实测结果**：Rust `cargo test --release` 全绿（0 失败）、Go `go test ./...` 全绿、Rust clippy `-D warnings` 零警告。CI 配置（`.github/workflows/ci.yml`）完整：mainline（Rust+.NET 双 OS + golden vectors）、interop（五语言互操作链）、extended（Go/Kotlin/Dart + macOS Rust，每次 PR 全量静态检查）。
- **重大盲区（本轮确认）**：五语言测试套件**均无"batch 内 delete 超长 key"用例**（grep `KeyTooLarge.*delete|delete.*huge|huge_key` 仅 Kotlin 命中一个无关的 Value 超限用例）。这正是 P0-1 / P0-2 至今未被测试捕获的直接原因。
- 上轮报告第 5 节建议的 9 类测试（长度校验矩阵、clear 竞态分叉、非法 UTF-16、路径前置校验、峰值内存、规模不变性、前缀耗时上界、刷盘告警、跨语言解码向量）**全部仍缺失**。

---

## 8. 修复优先级建议

| 顺序 | 项 | 工作量 | 收益 |
| :--: | :-- | :-- | :-- |
| 1 | **Rust/Go batch+直接 delete 补长度校验**（P0-2），编码层加兜底断言 | 极小 | 消除 3 门语言"一次调用毁全库/静默删错键" |
| 2 | Dart `BatchWriter.delete` 补 `validateLengths`（P0-1） | 极小 | 消除最后一门毁库路径 |
| 3 | 三语言补 `batch delete 超长 key` 回归测试（两种引擎模式） | 小 | 防止 P0 复发 |
| 4 | P0-2（上轮）clear 墓碑采集与内存清空合并进同一写锁临界区（.NET 主改、Dart 结构对齐） | 中 | 消除键复活 |
| 5 | P1-1 回滚点改 undo-log（先 Rust 样板 → Go/Kotlin/.NET → Dart） | 中大 | 兑现 AOL 性能承诺 |
| 6 | P1-6 前缀扫描二分起点（.NET/Dart/Rust/Go/Kotlin） | 小 | 前缀查询回到 O(log n + k) |
| 7 | P1-7 后台刷盘失败统一 `onBackgroundFlushFailed` 告警 | 极小 | 消除静默丢数据 |
| 8 | P1-2 .NET 比较器自洽 + 拒绝孤立代理项 | 小 | 消除数据串号 |
| 9 | P1-3/4/8 open 路径前置校验与错误归因（.NET/Dart 主改；Kotlin 移除静默建目录并与其他语言对齐） | 小 | 消除 10 秒挂死与幽灵库 |
| 10 | P1-5 流式解析（五语言） | 大 | 决定 100 MB 级场景可用性 |

---

## 9. 附注

- 本报告仅做审查与验证，未修改任何库代码；探针位于 `temp/review-probe/rust-delete-probe/` 与 `go-delete-probe/`（Rust 探针 Cargo.toml 使用 `path` 依赖指向 `src/rust`，Go 探针 go.mod 使用 `replace` 指向 `src/go`），可随时删除。
- 上轮探针（`temp/review-probe/dotnet-probe`、`dart-probe`）仍可复现 .NET/Dart 侧全部问题。
- 修复批次记录建议：完成任一 P0 后，在 `docs/CHANGES-YYYYMMDD.md` 以既有批次格式登记，并将第 7 节盲区测试并入该批次的回归清单。
