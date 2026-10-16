# CKV WAL 代码审查与文档数据真实性核对报告

> **编制日期**: 2026-10-16 15:35:13
> **审查分支**: `dev` (`3219d9c`)
> **审查范围**: Rust / Go / .NET / Dart / Kotlin 五语言 WAL (`AppendOnlyLog`) 与 `SyncMode` 实现，
> 以及 `docs/` 下 2026-10-16 的三份相关文档的数据与结论
> **审查方式**: 源码逐行核对 + 临时复现测试实测（测试文件已删除，工作区未留痕迹，未做任何代码修改）

---

## 摘要

本轮 WAL 实现存在 **3 个致命缺陷**（1 个静默数据丢失、1 个每次写阻塞 10 秒、1 个内存/磁盘状态分叉），
均为实测复现，非推测。其中最严重的一个在**五语言全部测试（Rust 66 / Go 49 / .NET 67 全绿）中零覆盖**。

同期三份文档的性能数据**部分属实、部分归因错误、部分不实**，并据此给出了选型建议。
在致命缺陷修复前，该选型建议不安全。

| 判定 | 条目数 | 说明 |
| :--- | ---: | :--- |
| 致命缺陷（实测复现） | 3 | Rust 丢数据 / Go·.NET·Dart 压实永不执行 / .NET 状态分叉 |
| 其余缺陷 | 7 | Kotlin 无界增长、Kotlin 冲突检测弱、锁文件 fsync 等 |
| 文档数据属实 | 4 | 测试数、吞吐对比量级、压实体积、坏尾截断 |
| 文档数据归因错误 | 8 | 见 §4.2 |
| 文档数据不实 | 4 | 见 §4.3 |

---

## 一、致命缺陷

### 缺陷 1【致命·静默数据丢失】Rust：自动压实会丢掉"触发它的那次写"

### 位置

- `src/rust/src/db.rs:1729-1731`（`apply_mutation` 的 WAL 分支）
- `src/rust/src/db.rs:1709-1714`（`append_entries` 尾部的自动压实触发）

### 机理

```
apply_mutation:                          append_entries 尾部:
  1730  append_entries(entries)?  ← 磁盘     1709  if new_file_size >= threshold {
  1731  apply_mem(self);          ← 内存            1713      commit_flush(...)?;
```

`commit_flush` 序列化的是 **`apply_mem` 之前的 `self.data`**。本次刚追加进 `.db` 的那个帧
被压实结果整体替换掉，该变更只存在于内存中。

`Immediate` 模式下 `is_dirty` 已被置为 `false`，因此 `close()` 与析构都不会补救。

### 实测证据

`compaction_threshold_bytes = 200`，连续 12 次 `put_string`：

| put 序号 | .db 文件大小 | 内存键数 | 备注 |
| ---: | ---: | ---: | :--- |
| 0 | 79 | 1 | |
| 2 | 173 | 3 | |
| 3 | **117** | 4 | **压实在此触发（173 → 117）** |
| 11 | 269 | 12 | |

```
AFTER REOPEN: 11 keys -> [key000 ... key010]      // key011 永久丢失，无任何报错
```

### 触发条件与影响

- 条件：关闭/析构前**最后一次写恰好越过阈值**。
- 默认阈值 4 MB，单条约 150 B → 约第 2.8 万次写开始进入危险区；命中一次丢一条。
- **无异常、无警告、无返回值变化**，调用方完全无感。

### 测试覆盖情况

`grep -r "compaction_threshold\|CompactionThreshold\|compactionThreshold"` 命中
**5 种语言的测试文件 0 个**。这套默认开启（4 MB）的阈值机制从未被测试触达，
因此 66/49/67 全绿与该 bug 并不矛盾。

---

### 缺陷 2【致命·每次写阻塞约 10 秒】Go / .NET / Dart：自动压实永远执行不了

### 位置

| 语言 | 持锁点（未释放） | 内部再次取锁点 |
| :--- | :--- | :--- |
| Go | `src/go/db.go:1694` `defer lock.Release()` | `src/go/db.go:1766` `db.beginFlush()` |
| .NET | `src/dotnet/src/Ckv/Db.cs:1184` `finally { lockHandle.Dispose(); }` | `src/dotnet/src/Ckv/Db.cs:1180` `BeginFlush(...)` |
| Dart | `src/dart/lib/src/db.dart:420` `lock.release()` | `src/dart/lib/src/db.dart:410` `_beginFlush()` |

### 机理

`appendEntries` 通过 `defer`/`finally` 持有 `.db.lock` 直到函数结束，
然后**在函数内部**再次调用 `beginFlush()` → `FileLock.acquire` →
对同一个 `.db.lock` 执行 `create_new` → 返回 `AlreadyExists` →
进入退避重试直到 `lock_timeout_ms` 耗尽 → 返回 `Err(Locked)`。

Go 侧 `if err == nil { commitFlush(...) }` 静默跳过；.NET 侧直接向上抛异常。

### Go 实测（`LockTimeoutMs = 1000`）

```
put 0:    3.3 ms   size=77
put 1:    3.1 ms   size=122
put 2:    2.6 ms   size=167
put 3: 1073.9 ms   size=212      ← 开始每次写阻塞一个 LockTimeoutMs
put 4: 1086.7 ms   size=257
put 7: 1071.8 ms   size=392
=> 文件从未缩小，压实一次都没成功
```

### .NET 实测（同参数）——更糟，每次写抛异常

```
put 0:    8.4 ms   size=77    err=<none>
put 2:    3.4 ms   size=167   err=<none>
put 3: 1181.0 ms   size=212   err=UnauthorizedAccessException: Access to the path is denied.
put 7: 1181.6 ms   size=392   err=UnauthorizedAccessException: Access to the path is denied.
=> 文件从未缩小
```

### 影响

- 默认 `LockTimeoutMs = 10000`（Rust/Go/.NET 三语言均为 10 s）
- 默认 `CompactionThresholdBytes = 4 MB`
- **即：WAL 文件涨过 4 MB 之后，每一次写静默阻塞约 10 秒，且文件永远不缩小。**
- 复现用的 Go 测试总耗时 90 秒，即 9 次写 × 10 秒，与推算完全吻合。
- 该路径完全落在同步写路径上，用户只会看到"程序突然变慢 10000 倍"，无任何诊断线索。

---

### 缺陷 3【严重·内存/磁盘状态分叉】.NET

### 位置

`src/dotnet/src/Ckv/Db.cs:1195-1199`（`ApplyMutation` 的 WAL 分支）

### 机理

`ApplyMutation` 的设计前提是「`AppendEntries` 抛错 ⇒ 等于没写」，以此维持磁盘/内存一致：

```csharp
if (_options.EngineMode == StorageEngineMode.AppendOnlyLog)
{
    AppendEntries(entries);
    applyMem();
}
```

但缺陷 2 证明 `AppendEntries` **内部的 `BeginFlush` 会抛**。于是：

1. 帧已追加进 `.db`（磁盘有）
2. `BeginFlush` 抛 `UnauthorizedAccessException`（缺陷 2 的超时）
3. `applyMem()` **不执行**（内存没有）
4. 异常向上传播给调用方

**磁盘与内存永久分叉，且异常类型错误**——抛的是 `UnauthorizedAccessException`，
而非 API v1.0 契约规定的 `LockedError`。

这正是 `ApplyMutation` 架构收敛（见 `docs/CHANGES-20261016.md` 第 2 节
"写路径拆为先校验后落盘两段"）要杜绝的不变量，.NET 破坏了该不变量。

---

## 二、其余缺陷

| # | 严重度 | 问题 | 位置 |
| ---: | :--- | :--- | :--- |
| 4 | 严重 | **Kotlin WAL 无界增长**：`DbOptions` 根本没有 `compactionThresholdBytes` 字段，`appendEntries` 也无自动压实。4 MB 阈值这层保护在 Kotlin 上不存在，WAL 只增不减 | `Options.kt`；`CkvDb.kt:283-353` |
| 5 | 严重 | **Kotlin 跨进程冲突检测弱于其它语言**：仅判断 `diskMaxTxId > currentMaxTxId`；Rust/Go/.NET 比对 `(maxTxId, fileSize)` 元组。当外部进程追加导致磁盘 TxID **低于**本进程内存 TxID 时，Kotlin 静默继续写入 → 两进程交错写同一文件 → 静默损坏。其它三种语言会报 `ConcurrentModification` | `CkvDb.kt:299-303` |
| 6 | 中等 | **每次写都对锁文件 fsync**：`FileLock::acquire` 内含 `file.sync_all()`。实测该锁文件操作（create_new + write 32B + fsync + unlink）单独耗时 **1285 µs/次**；而 `WAL + Deferred` 实测 **1981 µs/次** → **约 65% 的写延迟来自锁文件的 fsync**，与数据库 fsync 无关 | `lock.rs:63` 及各语言等价实现 |
| 7 | 中等 | **每次写开关一次 `.db` 文件句柄**：`append_entries` 每次 `OpenOptions::open` + `seek(End)` + `metadata`，用完即弃，未复用常驻句柄 | `db.rs:1653` |
| 8 | 中等 | **`compactionDeadRatio` 是幽灵 API**：Rust/Go/.NET 均声明该选项、默认值 `0.30`，但**没有任何引擎代码读取它**。Dart/Kotlin 连声明都没有 | `options.rs:62,76`；`options.go:14,27`；`Options.cs:30,50` |
| 9 | 次要 | **后台 flush 线程持写锁**：`Deferred` 模式后台线程持 `write()` 锁执行整库序列化 + fsync，阻塞所有读者；`close()` 需 `join` 线程，最长等待一整个 interval（实测将 interval 设为 100 s，两个 drop 各等 100 s） | `db.rs:240-253` |
| 10 | 次要 | **Kotlin 写路径未收敛**：`incrementI64` / `dropNamespace` 自行内联了 `engineMode` 分派，绕过 `applyMutation`，与"统一收敛写路径"的设计目的相悖 | `CkvDb.kt:484,538` |

> 说明：`MAX_PAYLOAD_LEN`（64 MB）> `MAX_VAL_LEN`（16 MB），因此"单条 entry 超过
> `MAX_PAYLOAD_LEN` 导致写出 0-op 空帧"的边界在合法输入下**不可达**，不构成缺陷。

---

## 三、是否是最优方案

WAL 作为"单文件、免多文件生命周期维护"的取舍方向本身是合理的，但当前实现距离最优有结构性差距：

### 1. 完全没有组提交（Group Commit）

成熟 WAL 引擎会把一个同步窗口内的多个写聚合成**一次** fsync。
当前实现每个写自己一把锁、一次 fsync，把 fsync 成本**全额**转嫁给每次写。

实测对比（同一台机器、同一负载）：

| 模式 | 实测吞吐 | 实测延迟 |
| :--- | ---: | ---: |
| `Snapshot + Deferred` | **483,481 ops/s** | 2 µs/op |
| `WAL + Deferred` | **505 ops/s** | 1981 µs/op |

WAL 路径比内存快照路径**慢 957 倍**，而文档将其表述为 WAL 的特性优势。

### 2. 锁粒度错配

跨进程文件锁是 **flush 级**的（`docs/CHANGES-20261016.md` 第 195-205 行自己也承认
"锁为每次 flush 级"），却被放在 O(1) 的增量写路径上。

嵌入式单进程场景的正确做法是：**常驻追加句柄 + 进程内 mutex**，
仅在 `open` / `compact` / `backup` 时取跨进程锁。

### 3. 内存仍为全量

WAL + 全量内存数据集 = 内存占用一点没降低。WAL 的核心意义（数据大于内存）
在本实现中并未兑现。

### 4. 压实没有后台化

压实是 O(N) 操作，却被放在触发它的那次写的**同步路径**内执行。
这正是缺陷 1 与缺陷 2 的共同根因。

### 5. WAL 模式下的恢复语义未重新审视

WAL 模式自动压实会把膨胀日志轮转成 `.db.bak`。此后若主库损坏，
`init_and_recover` 会 fallback 到 `.bak`——那是**压实前**的旧状态，
会把压实后写入的所有数据回滚掉，且仅发出一条 `onRecoveredFromBackup` 警告。

在 Snapshot 模式下这是预期语义（`.bak` 本就是上一代快照），
但在 WAL 模式下 `.bak` 的语义变得危险。
`docs/SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016.md` §2.3 称之为
"完整的两代容灾回滚能力"，在 WAL 语境下是误导。

---

## 四、文档数据与结论真实性核对

### 4.1 ✅ 属实（已复现验证）

| 文档说法 | 核对结果 |
| :--- | :--- |
| "Rust 66 tests, Go 49 tests, .NET 67 tests 全绿" | **完全属实**。逐个执行验证：Rust 66 / Go 49 / .NET 67，数字精确一致 |
| `WAL+Immediate` 439~464 Ops/s vs `Snapshot+Immediate` 253~315 Ops/s | 量级属实 |
| 压实 110 KB → 142 字节 | 与 bench 中 1000 次覆写 `hot_key` 的规模吻合 |
| Tail Truncation 物理截断自愈 | **是真的**。`format.rs` 含 7 处 `set_len` + `onTailTruncated` 回调，确实物理截断并返回 `effective_size` |

### 4.2 ⚠️ 数字对，但解读错误

| 文档说法 | 实情 |
| :--- | :--- |
| "Deferred/Manual **完全绕过磁盘**，仅在内存红黑树/有序表中修改"；"WAL + Deferred/Manual 维持 500~600 Ops/s，其原因是 WAL 保证了任何写入都立即同步追加进操作系统的 Page Cache" | **归因错误**。每次写都对 `.db.lock` 做了一次 `sync_all()`（缺陷 6）。该 fsync 单独 1285 µs，占实测 1981 µs 的 65%。慢的原因是**锁文件 fsync**，不是 Page Cache |
| "后台周期线程（**默认 50ms**）定时刷盘" | **默认值错误**。Rust/Go/.NET/Kotlin 的 `sync_interval_ms` 默认均为 **100**；仅 Dart 为 50。基准代码 `sync_wal_matrix_bench.rs:32` 写死了 `.sync_interval_ms(50)`，报告把**基准配置误当成了默认值** |
| "WAF 从 **54.04x 暴增到 107.75x**，因为写入次数从 100 次增加到 200 次" | **因果推断错误**。54.04x 是 **Rust @ 100 ops**，107.75x 是 **Go @ 200 ops 与 .NET @ 200 ops**。这是"换语言 + 换次数"两个变量同时变动，无法得出"随次数线性恶化"的结论。此外 Go 与 .NET 在 200 ops 下给出**完全相同**的 107.75x，本身也值得存疑 |
| "采样指标：写放大倍数 (WAF, **物理写入字节数** / 逻辑数据字节数)" | **方法错误**。`sync_wal_matrix_bench.rs:68-81` 显示 WAF 是**公式推算**的，不是测量的。且 `Snapshot+Deferred/Manual` 的 "1.07x" 实为"最终文件大小 / 逻辑字节"= **空间利用率**——该模式下根本未发生写放大，属范畴错误 |
| "Compaction 压实后重放验证读取最终版本值 `value_generation_999` 完全正确" | **不存在**。bench 代码中没有该回读验证步骤，系凭空添加 |
| "P50 / P99 延迟 = 0.00 μs" | **测量分辨率假象**。Rust 使用 `as_micros() as f64`（整数微秒截断），亚微秒值全部归零。此数据不可用于任何对比 |
| "实现 `CompactionThresholdBytes` 阈值自动压实" | **四语言不可用**：Rust 会丢数据（缺陷 1），Go/.NET/Dart 永不执行（缺陷 2），Kotlin 未实现（缺陷 4） |
| "WAL 在 Deferred/Manual 下进程崩溃 100% 零丢失" | 结论成立但**归因错误**：数据在 `appendEntries` 中已 `write()` 进内核，与 SyncMode 无关；`Manual` 模式同样成立 |

### 4.3 ❌ 不实

#### 五语言"完全同构"不成立

字节级互操作部分**属实验证**（Dart 与 Kotlin 均读取 `testdata/*.db` 做
"解析外部生成文件 + 本地重生成字节比对"）。但 API / 语义同构存在实质分叉：

| 项 | 分叉情况 |
| :--- | :--- |
| `syncIntervalMs` | Dart **50** vs 其余四语言 **100** |
| `compactionThresholdBytes` | Dart 默认 **0（即禁用）** / Rust·Go·.NET 默认 **4 MB** / Kotlin **无此选项** |
| `compactionDeadRatio` | Rust·Go·.NET 有但是死代码 / Dart·Kotlin 无 |
| `syncOnWrite` | **Dart 独有**，其余四语言无 |
| `allowTailTruncation` / `autoRecoverFromBackup` | **Kotlin 独有可配**；其余语言由 `engineMode` 推导，不可单独关闭 |
| 跨进程冲突检测语义 | Kotlin 与其它四语言不同（缺陷 5） |
| `lockTimeoutMs` 默认值 | Dart/Kotlin **5000** vs Rust/Go/.NET **10000** |

#### "进程崩溃 100% 零丢失是 SyncMode/WAL 的功劳"

见 §4.2 末行，归因错误。

---

## 五、建议修复顺序

### P0 —— 正确性

1. **把自动压实移出写路径**
   `append_entries` 只做追加 + 置 `needs_compaction` 标志，
   由后台线程或下一次显式 `compact()` / `flush()` 执行。
   → 缺陷 1、2、3 一次性消失。

2. **压实前保证内存状态已应用**
   先 `apply_mem`，或把本次 `entries` 一并传给压实，确保落盘快照 ≥ 内存快照。

3. **补测试**
   跨阈值连续写入后重开校验全量键，5 种语言各一个。
   当前该机制零覆盖。

### P1 —— 性能与一致性

4. **`FileLock::acquire` 去掉 `sync_all()`**
   `create_new` 本身即原子创建，锁文件内容只需原子性，无需 fsync。
   预期可把 WAL 吞吐从 500 Ops/s 提升至数万级。

5. **`append_entries` 复用常驻文件句柄**
   消除每写一次 open/close/seek/metadata 的系统调用开销。

6. **对齐五语言选项面**
   `compactionDeadRatio` 要么实现要么删除；Kotlin 补 `compactionThresholdBytes`；
   Dart 对齐 `syncIntervalMs`(100)、`compactionThresholdBytes`(4 MB)、`lockTimeoutMs`(10000)
   并处理独有的 `syncOnWrite`。

7. **统一 Kotlin 冲突检测语义**
   改为比对 `(maxTxId, fileSize)` 元组，与 Rust/Go/.NET 对齐。

### P2 —— 文档与基准

8. **基准报告的 WAF 改为真实测量**
   使用进程 IO 计数器或写前后的物理字节，删除公式推算。

9. **修正三处不实陈述**
   "默认 50ms"、"WAF 随次数线性增长"、"Compaction 回读验证"。

10. **计时精度改用纳秒**
    去除 `as_micros()` 整数截断，使 P50/P99 可用于对比。

11. **补充 WAL 模式 `.bak` 的恢复语义文档**
    明确 `.bak` 在 WAL 模式下是"压实前状态"，回滚风险需显式告知。

### 关于当前文档的处置建议

`CHANGES-20261016.md`、`PHASE2-SYNCMODE-AND-WAL-SUMMARY-20261016.md`、
`SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016.md` 将未经验证的基准推算
表述为"实测数据"，并据此给出了选型建议
（性能报告 §4 的 Mermaid 决策树，推荐 "AppendOnlyLog + Immediate" 与
"AppendOnlyLog + Deferred"）。

**在缺陷 1 修复之前，该选型建议不安全。**

---

## 附录：审查方法说明

- 全部结论均基于源码逐行核对 + 临时复现测试的实测输出，无推测成分。
- 复现测试涉及 `src/rust/tests/`、`src/go/`、`src/dotnet/tests/Ckv.Tests/`
  三个目录下的 3 个临时测试文件，**已全部删除**，`git status` 工作区干净。
- 审查过程**未修改任何生产代码**，**未执行 git commit / push**。
- 未被实测证伪的疑点已在正文明确标注（如 §3.5 的 `.bak` 回滚语义、§4.2 中
  Go 与 .NET WAF 数值完全相同的可疑之处）。
