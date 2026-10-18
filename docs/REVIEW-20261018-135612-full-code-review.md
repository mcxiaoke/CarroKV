# CKV 全项目第二轮全面代码审查报告（Rust / .NET / Go / Kotlin / Dart + 横切面）

> 审查时间：2026-10-18 10:56 ~ 13:56（本轮时钟）
> 审查对象：`src/rust`、`src/dotnet`、`src/go`、`src/kotlin`、`src/dart` 全部生产源码（约 20,000 行）+ 测试面（约 12,000 行），以及 `.github/workflows`、`testdata`、`docs` 发布链路。
> 判定基准：`docs/KV-DB-DESIGN-SPEC.md` v3.2、`docs/API-SPEC.zh.md` / `docs/API-SPEC.md`（v1.1）、`docs/API-V1-FREEZE.md`、`docs/decisions/ADR-20261017-singleton-ownership-and-lifecycle.md`、`DECISION-20261016-wal-recovery-and-compaction.md`（D1/D2/C8/C9/FIX-21/C15/C19）。
> 审查方式：6 路并行专项逐行审查（五语言 + 横切面），代理工作树严格只读；**全部 P0 与全部 P1 主张由主会话逐条亲自复核到当前 HEAD 代码行**；P2/P3 为代理静态核实（标注"待确认"者需后续交叉验证）。
> HEAD：`678ddfe`（含审查窗口内维护者自行合入的修复，影响说明见 §1.3；历史 review 见 `docs/REVIEW-20261017-201309-full-code-review.md`）。
> 探针脚本留存于 `temp/probes/{rust,dotnet,dart,go,cross}/`（temp/ 已被 .gitignore 排除）。

---

## 目录

- [一、审查方法与实测基础](#s1)
  - [1.1 全量 lint / 测试套件实跑结果](#s11)
  - [1.2 探针实测汇总](#s12)
  - [1.3 审查窗口内的 git 变动（678ddfe）](#s13)
- [二、总体结论](#s2)
- [三、P0 致命缺陷（3 项）](#s3)
- [四、P1 严重缺陷（14 项）](#s4)
- [五、P2 一般缺陷（38 项）](#s5)
- [六、P3 建议（50 项）](#s6)
- [七、跨语言共性设计空白](#s7)
- [八、CI / 发布 / 版本 / 文档问题汇总](#s8)
- [九、测试缺口汇总](#s9)
- [十、历史 review（20261017）修复落地对账](#s10)
- [十一、修复优先级路线图（批次六建议）](#s11)
- [附录 A：验证通过项摘要（按语言）](#sa)
- [附录 B：探针脚本清单](#sb)

---

<a id="s1"></a>
## 一、审查方法与实测基础

<a id="s11"></a>
### 1.1 全量 lint / 测试套件实跑结果（2026-10-18，Windows 10 本机）

| 语言 | 命令 | 结果 |
| :--- | :--- | :--- |
| Rust | `cargo fmt --check` / `cargo clippy --all-targets -- -D warnings`（强制重检）/ `cargo test --release` | 三者 EXIT=0；clippy 零警告；数百用例全绿（含 golden/interop/压实/Deferred 竞态/fault-injection） |
| Rust 黄金向量 | SHA256 对照 `testdata/MANIFEST.txt` | 4/4 一致 |
| .NET | `dotnet build -c Release` / `dotnet test tests/Ckv.Tests -c Release` / `dotnet format --verify-no-changes --no-restore` | 0 警告 0 错误；130/130 通过；格式 0 差异 |
| Go | `gofmt -l .` / `go vet ./...` / `go test ./...` / `go test -race`（子集，CGO_ENABLED=1 + MSYS64 gcc） | 均干净/通过；race 子集通过 |
| Dart | `dart format --output=none --set-exit-if-changed lib test` / `dart analyze` / `dart test` | 0 changed；0 issues；**82/82 通过** |
| Kotlin | `.\gradlew.bat test --no-daemon --rerun-tasks`（强制重跑） | **93/93 通过**，0 失败 0 跳过（按 678ddfe 复核） |
| 黄金向量（横切面） | AGENTS.md 脚本全量 MANIFEST SHA256 校验 + `Format-Hex` 对照 spec §5.3.1 + 沙箱复跑 `tools/gen_testdata.py` | 4/4 一致；十六进制镜像逐字节吻合；**独立复跑再生成的四文件 SHA256 与仓库现行文件完全一致**（确定性成立） |
| 版本 stamp | 12 处版本点逐项比对 | 全部 = 0.2.1，无残留 0.2.0 |
| 仓库卫生 | UTF-8/BOM/CRLF + `git ls-files` 253 文件扫描 | 全 UTF-8 无 BOM；无构建产物入库 |

> 同上轮 review 的提醒再次成立：**五语言测试全绿，但本轮全部 P0 与绝大多数 P1 缺陷现有测试都未覆盖**（各缺陷与测试缺口逐一对应见 §9）。

<a id="s12"></a>
### 1.2 探针实测汇总（12 组关键探针）

| # | 探针 | 结果 |
| :--- | :--- | :--- |
| P1 | .NET：8 线程并发 `IncrementInt64` ×2000（Snapshot+Immediate） | `expected=16000 in_memory=2150 on_reopen=2150 lost=13850`——**丢失 86%**，重开磁盘与内存一致（丢失发生在落盘前）|
| P2 | .NET：只读打开 + 主库缺失 + `.tmp` 完好 | `read_only_promoted_tmp=True`——只读打开执行了 `File.Move` 晋升 |
| P3 | Rust：close() 后调用 `compact()` | `Ok(())` 且 `.db` mtime/字节均改变——**磁盘被真实重写** |
| P4 | Rust：close() 后 `batch(f)` | 闭包体执行完毕后才报 `LockedError`（staging 结果被正确丢弃，但用户副作用已发生）|
| P5 | Dart：`flushAsync()` 窗口内 Timer 注入 40 写/轮 ×30 轮 | `races_detected=30/30`——重开后注入写**全部丢失**，`_currentMaxTxId` 回退（如 287 vs 320）|
| P6 | Go：`os.Open`(O_RDONLY) 后 `f.Sync()`（Windows） | **`Access is denied`**——Windows `FlushFileBuffers` 需要写句柄 |
| P7 | Go：race detector，SyncModeDeferred + 3 写者 + 4000 次 Backup | **`WARNING: DATA RACE`**：`Backup db.go:1081 读 currentMaxTxID` ↔ `executeMutation db.go:2563 写`，exit 66 |
| P8 | Go：`crc32.ChecksumIEEE("123456789")` | `0xCBF43926`，与表法等价 |
| P9 | 横切面：`tools/gen_testdata.py` 沙箱复跑 | 产物与 testdata/ 与 MANIFEST.txt 完全一致（未触碰受管文件）|
| P10 | 横切面：CI yml 静态解析（分支/jobs/权限/timeout/pin/cron） | ci.yml push `[main, master]` ✓；权限最小化 ✓；4 个 yml 均无 `timeout-minutes`；无 Dependabot 残留 |
| P11 | 横切面：`git tag -l` + `git ls-remote --tags origin` | 本地/远端均只有 `v0.1.0`、`v0.2.0`（另含 `src/go/*` 坐标 tag）；**无 `v0.2.1`** |
| P12 | 横切面：crates.io / NuGet registry 实测 | 两者 newest 均为 **0.2.0**（0.2.1 未发布）|

<a id="s13"></a>
### 1.3 审查窗口内的 git 变动（678ddfe）与本报告的口径

审查进行中，维护者于 13:41:49 自行合入 `678ddfe`（"fix: resolve rust self-join deadlock, align kotlin error spec, and export go io errors"）。该提交：

1. **修复了一个本轮发现清单之外的新缺陷**：Rust 后台刷盘线程 `join()` 自连接死锁——`Drop for DbInner` 与 `close()` 中无条件 `handle.join()`，当 drain/close 由后台线程自身触发时 join 自己。修复方式为 join 前比对 `thread().id() != thread::current().id()`（`src/rust/src/db.rs` 两处，现位于 :274-279、:646-651）。
2. **部分落地 GO-8**：`src/go/errors.go` 新增 `NewIoError` 构造器与 `IsIoError` sentinel（其余 6 类 `IsXxx` 仍缺）。
3. **部分落地 Kotlin 错误体系对齐**：`Errors.kt` 新增 `typealias DbError = CkvException`、`KeyTooLargeError = KeyTooLongError`、`ConcurrentModificationError = TransactionConflictError`，并补 `normalizeNamespace` 统一 NsLen 拦截 + BatchOneDataSafetyTest 扩面（CROSS-3 尚存 value 超限逸出体系，见 KOTLIN-4）。

**口径**：本报告所有行号与结论均按 `678ddfe`（当前 HEAD）复核；Kotlin 专项审查即锚定在 678ddfe，其余四语言专项锚定在 80bf68a，涉及 678ddfe 触达的文件（rust db.rs 两处 +4 行偏移、go errors.go、kotlin 三文件）已逐条重核。

---

<a id="s2"></a>
## 二、总体结论

**本轮共 105 项发现：P0×3、P1×14、P2×38、P3×50。** 无一项 P0 由现有测试覆盖。

| 语言/面 | 健康度一句话 |
| :--- | :--- |
| **Rust** | 参考引擎质量最高：格式层/写管线/锁协议/压实全对齐；唯一 P0 是 `compact()` 缺 closed 防线（一行修复），历史账 P1-7/8/9/10 未清 |
| **.NET** | 单例所有权 ADR 重构落地扎实（现场崩溃类问题根除）；但 `IncrementInt64` 把"P0 原子累加器"契约**单方面打破**（实测丢 86%），netstandard2.0 轮转缺陷与 rename 退避缺陷原样未修 |
| **Go** | 结构与格式全对齐、race 干净；Backup 锁外读 TxID 被 race detector 实证，Windows AOL 最终刷盘是空操作且静默清脏；历史 Go P2 债务几乎整批未清 |
| **Kotlin** | 批次修复大面积落地、测试最全（93 用例）；但 MANUAL 模式语义反向、强类型读取双读锁 TOCTOU（历史 P1-28 未修）、AOL 后台刷盘缺代际守卫三个 P1 |
| **Dart** | 同步面安全；async 面有一个 P0（flushAsync 窗口吞写）——是五语言中唯一需要 async 栅栏语义的写入路径 |
| **横切面** | 黄金向量制度健全（4/4 + 沙箱复现一致）；**发布链路断档（0.2.1 无 tag/无产物）**、API-SPEC §3.1 与实现-spec 双脱节、Kotlin 错误体系未达统一命名基线 |

**与上轮 review 的落地率对账**：历史 P0×7 已全部修复（有测试固化）；历史 P1×40 中约七成已落地，未落地者集中在三类：① .NET 的 rename 退避/netstandard2.0 分支（P1-14/P1-16）；② Go 的 P2 技术债整批（P2-20/22/23/24/26/29/31/32/33/34）；③ 跨语言扫描语义族（P1-36 部分、onDirSyncUnavailable、HeaderFlags 校验、同尺寸短路）。详见 §10。

---

<a id="s3"></a>
## 三、P0 致命缺陷（3 项，均已实测/实锤复核）

### P0-1｜DOTNET-1｜`IncrementInt64` 并发丢失更新（实测丢失 86%）

- **位置**：`src/dotnet/src/Ckv/Db.cs:2808-2832`（读锁窗口读 `currentVal`）、`Db.cs:2831-2849`（锁外预算 `newVal` 后进写锁直接 `store.Put(预计算值)`）。
- **现象**：read-modify-write 拆在"读锁 → 无锁 → 写锁"三段，并发 increment 各读到同值、后写覆盖前写。
- **证据**：主会话亲读代码确认结构；代理探针 `expected=16000 in_memory=2150`（丢 13850）。Rust 参考实现（`db.rs` `increment` 读在 `execute_mutation` 写临界区内）正确。
- **影响**：spec §5.2.2 定义该 API 为"**P0 原子累加器**"；计数器/幂等令牌/配额静默丢增量，不告警不落盘不可恢复。
- **修复**：把"读当前值 + 类型拦截 + 计算 + Put"整体移入 `ExecuteMutation` 的写临界区回调内（约 15 行改动）；补 2 线程 ×1000 并发回归测试。
- **防线现状**：130 个测试全单线程，全绿掩盖该缺陷。

### P0-2｜DART-1｜`flushAsync()` 异步提交窗口吞掉并发写 + TxID 回退（30/30 复现）

- **位置**：`src/dart/lib/src/db.dart:772-805`（`_commitFlushAsync` 尾部无条件覆写状态）、触发面 `db.dart:296-299`（flushAsync）、`db.dart:604-609`（Timer 同款调用）。
- **现象**：快照序列化在首个 `await` 前同步完成，但之后有 ≥5 个挂起点（open/writeFrom/flush/close/exists/rename）；窗口内任何成功 `put()` 在尾部被 `_currentMaxTxId = newMaxTxId`（序列化时刻的旧值）覆写、`_isDirty=false`。重开即丢写；期间 sync `flush()` 因 `_isDirty==false` 跳过、`close()` 兜底刷盘同样跳过。若丢失前的写已推进 TxID，重开还会因后写复用已占用 TxID 触发 `CorruptedError`（库拒开）。
- **证据**：亲读代码确认（`db.dart:797-800` 无条件覆写 + 无任何并发写栅栏）；代理探针 30/30 轮稳定复现。
- **影响**：Deferred 模式下静默数据丢失 + TxID 空间紊乱可连锁导致库拒开。五语言中唯一需要 async 栅栏的路径（其他语言写路径在锁内完成状态提交）。
- **修复**（≤15 行）：提交状态前加代际/序号栅栏——写路径只推进 `_currentMaxTxId` 不动序号，`_commitFlushAsync` 尾部在 `serial != _flushSerial || _currentMaxTxId != txAtStart` 时直接 return（保持 `_isDirty=true` 让下一次 flush 收敛）；配套 `flushAsync` 改为 `while (dirty) flushOnce()` 循环。

### P0-3｜RUST-1｜`close()` 后调用 `compact()` 仍原子重写磁盘

- **位置**：`src/rust/src/db.rs:1723-1734`（`compact()` 本体，无 `check_writable()`）、`db.rs:2727-2732`（`precheck_disk_state` 只挡 `read_only`）、`commit_flush` 亦无 closed 防线。
- **现象**：close 后（跨进程锁已释放、注册表已注销、`closed=true`），旧 clone 调 `compact()` 返回 `Ok(())` 并真实轮转重写 `.db`。
- **证据**：亲读代码确认三条缺失防线；代理探针 mtime/字节双变。
- **影响**：A 进程 close → B 进程 open 并写入 → A 的残留线程调用 `compact()`，以 close 时刻的内存快照**全新轮转覆盖 B 的全部写入**——静默丢数据且违反 C9 锁释放前提。`flush()`/`execute_mutation`/`bg_flush_step` 均有防线，唯一漏网是 `compact()`。
- **修复**：`compact()` 取写锁后第一行加 `inner.check_writable()?;`（与 `flush()` 同构），补 close→compact→磁盘未变回归测试。

---

<a id="s4"></a>
## 四、P1 严重缺陷（14 项；全部经主会话亲读代码复核）

### P1-1｜RUST-2｜单例注册表锁内调用用户回调（死锁契约违反）

- **位置**：`src/rust/src/db.rs:402-408`（Step1）与 `:450-455`（Step3）：持 `SHARED_REGISTRY.lock()`（parking_lot Mutex，不可重入）期间经 `check_options_conflict`（:361-395 内 :383-395）**同步调用 `onOptionsConflict` 用户回调**。
- **影响**：回调内任何一次 `Db::open`（任意路径）即自我死锁。违反"绝不持锁调用户回调"死锁契约（open 路径没有 pending_warnings 锁外派发的等效机制）。
- **修复**：`check_options_conflict` 只做校验并返回告警消息，由调用方在锁外收集、注册后派发（照 `pending_warnings` 模式）。

### P1-2｜DOTNET-2｜历史 P1-16 未修：netstandard2.0 分支仍是 `File.Replace`

- **位置**：`src/dotnet/src/Ckv/Db.cs:1587-1601`（`#if NETCOREAPP3_0_OR_GREATER … #else File.Replace`）+ `Ckv.csproj` 双目标 `netstandard2.0;net8.0`。
- **影响**：netstandard2.0 产物在 Unix 运行时上 `File.Replace` 抛 `PlatformNotSupportedException` 且不被捕获 → 消费者（Unity/MAUI/netstandard）**第一次写轮转即崩**。netstandard2.0 TFM 零测试覆盖。
- **修复**：`#else` 分支改为"前置 `File.Delete(dst)` + `File.Move`"的退避序列，或砍掉 netstandard2.0 目标（需发布决策）。

### P1-3｜DOTNET-3｜历史 P1-14 未修：rename 退避只 `catch (IOException)`，WinError 5 逃逸

- **位置**：`src/dotnet/src/Ckv/Db.cs:1689`（`AtomicReplaceWithRetry`）。
- **影响**：`ERROR_ACCESS_DENIED(5)` 被映射为 `UnauthorizedAccessException`，绕过 `[5,15,30,60,120]` 全部退避，直落 `.stage` 非原子拷贝兜底（O(库大小) + 崩溃窗口留半成品 `.db`）。杀软/索引器 ACL 拦截是 Windows 桌面常态。
- **修复**：`catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)`。

### P1-4｜DOTNET-4｜历史 P1-35 未修：后台刷盘裸 `catch {}` 吞掉全部预检错误

- **位置**：`src/dotnet/src/Ckv/Db.cs:538-541`。
- **影响**：Deferred/Manual 下 `ConcurrentModificationError`/IO 错误全吞、无 `onWarning`；每次 tick 重试再吞，直到 close 才见 `onFinalFlushFailed`，期间零早期信号。
- **修复**：catch 内 `QueueWarning("onBackgroundFlushFailed", …)`（派发通道已存在）。

### P1-5｜DART-2｜Snapshot dispose 后抛裸 `DbError`，违反 API-SPEC §3.5.3 温和语义

- **位置**：`src/dart/lib/src/storage.dart:109-113`（`_checkDisposed` → `throw DbError('Snapshot is already disposed')`，全部快照读路径命中）。
- **证据**：spec 原文："快照关闭/销毁后，再次调用读操作温和地返回空（不存在），不抛出崩溃性异常"（API-SPEC.zh.md §3.5.3 :153-154）；.NET 已实现该语义，Dart 未对齐，且抛的不是 10 类标准错误任何一个。
- **修复**：读路径统一改 `_effective`（dispose 后空字典兜底，≤10 行）；或走决策修订 spec 并同步跨语言口径。

### P1-6｜GO-1｜`Backup()` 在锁外读 `currentMaxTxID`（race detector 实证）

- **位置**：`src/go/db.go:1053-1081`（:1068 RUnlock → :1081 锁外读）。竞争写点 `db.go:2563` 等。
- **证据**：亲读确认 + 探针 DATA RACE（读 Backup↔写 executeMutation，exit 66）。
- **影响**：Go 内存模型 UB；备份帧 TxID 可能超出 entries 实际状态。race 全绿只因无并发 Backup 用例。
- **修复**：`finalMaxTxID := db.currentMaxTxID` 移入 RLock 内；同时建议 Backup 取 `flushMu`（见 GO-12）。

### P1-7｜GO-2｜AOL 三处最终刷盘在 Windows 上失效且静默清 `isDirty`（探针证实）

- **位置**：`src/go/db.go:503-507`（close 兜底）、`:1134-1139`（Flush AOL 分支）——`os.Open`（O_RDONLY）+ `_ = f.Sync()` 后无条件 `isDirty=false`；`:382-391`（flushIfDirty AOL 分支）打开失败也照样清脏。
- **影响**：Windows 上 Deferred/Manual 的 `Close()`"最终落盘"是**空操作**，close 契约（§5.2.2）被静默破坏；掉电丢"已确认成功"的写。Snapshot 引擎不受影响。
- **修复**（≤15 行）：统一 `syncAppendLog()` 用 `O_RDWR` 打开（Windows 需要 GENERIC_WRITE）、Sync 失败或打开失败时**不清 `isDirty`** 并上报告警。

### P1-8｜KOTLIN-1｜`syncMode=MANUAL` 被当作 IMMEDIATE 处理（跨语言唯一分叉）

- **位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:1143`（仅 `== SyncMode.DEFERRED` 走内存积累路径；`MANUAL` 落入 else 立即两阶段落盘）；对照 `db.rs`/`db.go`/`Db.cs` 均为 `Deferred || Manual` 同路径。
- **影响**：语义 + 性能双重违规（MANUAL 本意高频写合并，实际每写全量序列化+fsync+双 rename，吞吐劣化一个量级）；数据安全反而更好（无丢失），无 P0 风险。
- **修复**：条件补 `|| options.syncMode == SyncMode.MANUAL`（1 行）+ 补"flush 前文件不落盘"断言。

### P1-9｜KOTLIN-2｜历史 P1-28 未修：强类型读取双读锁 TOCTOU，并发删除即 `IndexOutOfBoundsException`

- **位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:1482-1549`（getString/getInt64/getFloat64/getBool 四处同构）：先 `getBytes()`（一次读锁）释放锁，再第二次读锁 `findIndex` + `store.items[idx]`，**无 `idx < 0` 守卫**，且类型校验取 item、返回值取 `bytes`——两次锁定读取可能跨代。
- **影响**：并发"读 + 删/覆写"下未封装的系统性崩溃（`IndexOutOfBoundsException` 不是任何标准错误），或返回"新值的旧字节副本"。（Snapshot 侧同形状但私有深拷贝无并发写者，不可达。）
- **修复**：四方法改为锁内一次完成 find + idx<0 返回 null + 类型校验 + 解码（≤15 行/方法）。

### P1-10｜KOTLIN-3｜历史 P1-27 修复不完整：AOL+DEFERRED 后台刷盘无代际守卫

- **位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:689-703`（fdsync 后 `rwLock.write { isDirty = false }`，仅检查 engineMode，不比对 `dirtyGeneration`；SNAPSHOT 分支 :725 有守卫）。
- **影响**：写者 appendEntries 与后台 fsync 交叠的窗口内，最后一部分帧从未被 fsync 覆盖但脏标记被清——掉电丢"已确认成功"的写（持久性破绽，窗口窄）。
- **修复**（≤10 行）：read 阶段两种模式都记 `generation = dirtyGeneration`，AOL 分支清脏前比对 `dirtyGeneration == generation`。

### P1-11｜KOTLIN-4｜Value 超限抛 `ValueTooLongError`，逸出 `KeyTooLargeError` 体系

- **位置**：`CkvDb.kt:1792-1797`（`validateKeyAndVal`）与 `Storage.kt:425-430`（BatchWriter 同构）；`Errors.kt:35` `typealias KeyTooLargeError = KeyTooLongError` 只桥接了 Key 向。
- **证据**：678ddfe 之后复验仍在。API-SPEC §3.2.4：key/value 任一超限都应是 `KeyTooLargeError`（§4 映射表 .NET 侧确实双向抛）。
- **影响**：`catch (KeyTooLargeError)` 捕不到 value 场景，跨语言迁移错误分流静默失效。
- **修复**：value 超限改抛 `KeyTooLongError`（即 `KeyTooLargeError`），对 `ValueTooLongError` 加 Obsolete 兼容 typealias。

### P1-12｜CROSS-1｜发布链路断档：0.2.1 有版本号、无 tag、无 registry 产物

- **位置**：git tags（本地/远端均只有 v0.1.0、v0.2.0）；crates.io/NuGet newest 均为 0.2.0；README/5 语言 README 的 `v0.2.1` 安装坐标不可解析。
- **影响**：用户按 README 安装坐标（git/jitpack/go get/pip 式 ref）解析失败；crates.yml / publish-nuget.yml 的 `v*` tag 触发器不会触发。发布事故级。
- **修复**：全绿确认后 `git tag v0.2.1 && git push origin v0.2.1` 触发发布，随后复验 registry 与 jitpack 构建。

### P1-13｜CROSS-2｜API-SPEC §3.1 配置契约与实现 + 上位规范双脱节（中英同源）

- **位置**：`docs/API-SPEC.md:72-86` / `API-SPEC.zh.md:72-86`（主会话核对原文）。
- **明细**：① `sync_mode` 声明旧名 `Always/Periodic`（真实枚举 Immediate/Deferred/Manual）；② `sync_interval_ms` 写 50（实现/上位规范均 100）；③ `auto_compaction_threshold` 选项名与语义均过时（实际 `compactionThresholdBytes` + FIX-21 垃圾量公式）；④ `dead_ratio_compaction_threshold` 选项五语言均不存在（死键比例为内置行为）。
- **影响**：按规范配置直接编译失败/语义错误；两份规范同名口径分裂。
- **修复**：按 KV-DB-DESIGN-SPEC §5.1.1 全量重写 §3.1（en+zh 同时）。

### P1-14｜CROSS-3｜Kotlin 错误体系未达统一命名基线（错误名/类/继承三处）

- **位置**：`src/kotlin/src/main/kotlin/ckv/Errors.kt:1-38`、`CkvDb.kt` 多抛点。
- **明细**：根类是 `CkvException`（`DbError` 仅是 typealias，运行时类名/堆栈不一致）；Key 超限抛 `KeyTooLongError`、Value 超限抛 `ValueTooLongError`（无 `KeyTooLargeError` 实类承接，见 P1-11）；并发冲突抛 `TransactionConflictError`；规范外死类 `KeyNotFoundError`/`InvalidKeyError`/`PayloadTooLargeError`。其余 4 语言逐字合规。API-SPEC §4 反而宣称 Kotlin"bombom 逐字一致"——文档级失实。
- **修复**：Kotlin 补实类桥接（或 spec 修订例外并删除"逐字一致"表述，二选一走决策）；删除死类。

---

<a id="s5"></a>
## 五、P2 一般缺陷（38 项）

### 5.1 Rust（8 项）

| 编号 | 位置 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| RUST-3 | `db.rs:2300-2308` | read_only 打开仍执行 `.tmp` 晋升/删除（盘上修复），与自家注释/规范"只读不动盘"相抵 | readOnly 跳过晋升/清理或写 ADR 说明 |
| RUST-4 | `db.rs:2980-2992` | 目录 fsync 全静默（POSIX 失败无告警、Windows 无条件 no-op 也无告警）——规范 §1.2#7 要求一次性 `onDirSyncUnavailable` | 错误时 queue_warning 一次性派发 |
| RUST-5 | `format.rs:512-541` | `scan_max_tx_id` 不校验帧头 CRC/单调性/`payload_len` 越界——撕裂帧可致伪 TxID → 之后每次写误报 `ConcurrentModificationError` 直至重开 | 加越界→`Corrupted` 区分；CRC 查头 20B |
| RUST-6 | `db.rs:1389-1446` | `backup()` 无 dest==self 防御、不取 `flush_mutex`（与 flush 争 `.tmp`）、closed 后仍可备份、TxID 段与主库快照重复歧义 | same-file 拒绝 + 取 flush_mutex |
| RUST-7 | `db.rs:2948-2977 / 2296-2418` | `.db.stage` 兜底残留无启动清理，可无界堆积（`init_and_recover` 只清 `.tmp`） | 启动时清理陈旧 `.stage` + 一次性告警 |
| RUST-8 | `db.rs:1713-1719` | `batch(f)` 在执行用户闭包之前不检查 closed/writable（探针复现：闭包副作用已发生才报错） | 入口加 `check_writable` 早退 |
| RUST-9 | `db.rs` 四处迭代器 | 非 UTF-8 key 四种口径不一（`iter_prefix`/`scan_range` lossy 保留，`keys`/Snapshot 跳过） | 统一 skip + 写入口拒非 UTF-8 或文档明示 |
| RUST-10 | `db.rs:2645-2663 / 2736-2769` | AOL 运行中写失败留撕裂尾帧，precheck 每次 `scan_max_tx_id` 撞上它 → 每写误报并发修改，同连接永不自愈 | 失败时收缩文件到写前位置（或翻译为 Corrupted 提示重开） |

### 5.2 .NET（10 项）

| 编号 | 位置 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| DOTNET-5 | `Db.cs:1340-1343` | 历史 P1-7 同源：`ProbeDiskState` 同尺寸短路跳过 TxID 比对——等长外部替换 `.db` 永不触发 `ConcurrentModificationError` | 比对三元组（TxID,size,mtime），mtime 变化时补扫描 |
| DOTNET-6 | `Db.cs:815-872` | 历史 P1-25 .NET 面：readOnly 仍晋升 `.tmp`/写盘（探针复现）；晋升失败抛 `CorruptedError` 应为 `IoError` | 补 readOnly 门 + 错误类型归位 |
| DOTNET-7 | `Db.cs:826-850` | `.tmp` 读取**失败**（瞬时 IO）被当作"内容损坏"删除——主库缺失时可即毁唯一完好数据 | 读失败不删、抛 `IoError` 保留现场 |
| DOTNET-8 | `Db.cs:3266-3322` | finalizer 收敛路径：`TryEnterWriteLock(2000)` 失败后 `try` 块仍无条件做 `_isDirty` 检查与 `CommitFlush`（无锁刷盘）；与 ADR §5"终结器严禁盘上 IO/等待锁"字面冲突 | `if (!lockTaken) return;` + 更新 ADR 表述 |
| DOTNET-9 | `Db.cs:2349-2377` | `Backup(dest)` 不取 `_flushGate`、无 `dest==self` 校验（`dest+".tmp"` 与主写路径同文件互踩） | 自路径拒绝 + 入 flushGate |
| DOTNET-10 | `Db.cs:1164-1217` | 加载期不校验 `valLen ≤ 16MB`（u32 无上限条目直接入内存并再序列化传播） | 解码处 `valLen > MaxValLen` → Corrupted |
| DOTNET-11 | `BinaryHelper.cs:145-169` | `Utf8StringComparer` 孤立代理项塌缩（U+D800 与 U+FFFD 同 UTF-8 字节）+ `Compare` 每次分配 2 个数组 | 写侧拒绝孤立代理或预租 buffer 比较器 |
| DOTNET-12 | `Db.cs:1189-1198` | 加载路径不归一化物理 `"default"` 命名空间（外来文件出现数据读不到、删不掉的孤子分区；Go 同源，跨语言口径待统一） | `ParseDatabaseBytes` 内归一化 |
| DOTNET-13 | `LockFile.cs:72-75` | 历史 P2-13：一切 `UnauthorizedAccessException`（目录不可写等）被误诊为锁争用 → 误导性 `LockedError` | 区分错误源 |
| DOTNET-14 | `Db.cs:1014-1021` | 历史 P1-31：`HeaderFlags` 非零不拒绝（规范 §3.1"未知非零必选位"条款，五语言共同缺口） | 补 flags 校验 |
| DOTNET-15 | `Db.cs:2399-2414 / 3036-3046` | 历史 P1-36 部分：`IterPrefix`/`ScanRange` 不过滤非法 UTF-8 键（同文件 `KeysInternal` 有过滤，口径分裂） | 补 `Utf8Helper.IsValid` 过滤 |

### 5.3 Go（5 项）

| 编号 | 位置 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| GO-3 | `db.go:2340-2398` | `appendEntries` 部分写失败留半帧且不回填盘账，续写永久化"中间坏块"（重开即 Corrupted）；与 Rust/Dart 同构（见 §7） | 写前记 `startPos`，失败 `Truncate(startPos)` |
| GO-4 | `types.go:207-213 / db.go:368-371` | 历史 P2-26：`DbOptions{}` 零值 `EngineMode=0` 时后台刷盘绕开 `flushMu`，与 executeMutation 同写 `.tmp` 可交错 → rename 出交错库文件 | Open 入口零值归一化 |
| GO-5 | `db.go:2936-2940` | 历史 P2-29：`Snapshot.Close()` 无锁写 `namespaces = nil` 与并发读构成 data race | 原子 released 标志替代置 nil |
| GO-6 | `db.go:2026-2051` | 历史 P2-24：precheck 对 `os.Stat` 失败静默放行、`ScanMaxTxID` 错误吞掉——主库被删不报并发修改、介质损坏被折算成并发修改（与 Rust 行为偏离） | 区分 IsNotExist/其他错误并上抛 |
| GO-7 | `db.go:56-84 / 163 / 1713-1740` | `snapshotCount` 沦为死变量、注释与实现矛盾：无论有无快照每次 mutation 必克隆（Rust 是 refcount>1 才拷贝） | 以 `snapshotCount>0` 为克隆条件或修正注释归档权衡 |

### 5.4 Dart（5 项）

| 编号 | 位置 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| DART-3 | `db.dart:1336,1378`（同 storage.dart:242,285） | 历史 P1-36：iterPrefix/scanRange 对非法 UTF-8 key lossy 解码（同文件 `keys()` 已过滤，自相矛盾；返回 U+FFFD 伪键可触达错数据） | 补 `isValidUtf8` 过滤 |
| DART-4 | `db.dart:616-654 / 939-999 / format.dart:140-163` | 历史 P1-27 部分：AOL 写失败连接永久"中毒"——scanMaxTxId 把残帧当完整帧（伪 TxID）或当 Corrupted（与加载器损伤分类矛盾）→ 每写误报并发修改 | 损伤分类统一 / 写失败标 `_needsRecovery` |
| DART-5 | `lock.dart:34-64` | 历史 P2-51：lock acquire 一切异常空转 10 秒后误报 `LockedError`（目录不存在/权限不足被当成锁争用） | 先 stat 区分错误源 |
| DART-6 | `db.dart:604-609` | 历史 P2-57：Timer 刷盘 `catch (_) {}` 静默——Deferred 持续失败零信号（对照 close 路径有 onFinalFlushFailed） | catch → `onWarning('onDeferredFlushFailed')` 限频 |
| DART-7 | `db.dart:445-492` | 历史 P1-25 残留：readOnly 打开时 `.tmp` 晋升/删除无 readOnly 守卫（同函数 `.bak` 分支有门，内部不一致） | 补 readOnly 门 |

### 5.5 Kotlin（7 项）

| 编号 | 位置 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| KOTLIN-5 | `CkvDb.kt:890` | `buildSnapshotBytes` 忽略传入的快照 map、读活体 `namespaces`（现行锁序下不可达，但与"以快照为准"承诺相悖的防御缺口） | `targetNamespaces[ns] ?: continue` |
| KOTLIN-6 | `CkvDb.kt:288-293` | 静态 `backupFile` 强制 readOnly 重开：库已打开时必然 `ConflictingOptionsError`（.NET 是唯一按 spec 复用活跃实例实现方；Go 同病） | 有活跃实例则复用之（对齐 .NET） |
| KOTLIN-7 | `CkvDb.kt:516-560` | readOnly 打开时 `.tmp` 晋升/删除无readOnly 门（损坏分支有门 :583/:632，内部不一致） | 两处补 `!options.readOnly` |
| KOTLIN-8 | `CkvDb.kt:1274 / 1323 / 1422` | Watcher 事件 value 共享内部数组——回调改写 event.value 可污染内存并经后续刷盘/backup 写入磁盘 | 事件构造 `valBytes.copyOf()`（3 处或构造函数一处） |
| KOTLIN-9 | `Storage.kt:88-90` | Snapshot `checkClosed()` 抛 `CkvException`，与 §3.5.3 温和语义矛盾（与 DART-2 同签名、配合 RUST/.NET 已按温和实现的口径） | dispose 后读返回空/0/空列表 |
| KOTLIN-10 | `CkvDb.kt:1591-1607` | `scanRange` 不过滤非法 UTF-8（iterPrefix :1452-1468 已过滤——批次三只修了一半）；keys() 策略未定义 | 补过滤 + spec 补 keys() 策略 |
| KOTLIN-11 | `Format.kt:295-306 / 359-372` | 未知 opCode 静默忽略（`when` 无 else 兜底）、DROP/DELETE 的 KeyLen/ValLen 约束不校验 | 补 `else -> Corrupted` + 尺寸校验 |

### 5.6 横切面（3 项）

| 编号 | 位置 | 问题 |
| :--- | :--- | :--- |
| CROSS-4 | `ci.yml:154-156` + `CHANGES-20261018.md` | changelog 宣称 Go/Kotlin/Dart 已进每次 push/PR 门禁，实际 `extended` job 仍 schedule/dispatch-only——**这些语言的 PR 无预合并校验**（最长一个周期才被发现） |
| CROSS-5 | 依赖扫描 | Dependabot 移除后仅存 audit.yml（只扫 Cargo）；Go/Gradle/Dart/Actions 生态无任何自动更新或 CVE 扫描，决策无记录 |
| CROSS-6 | `API-SPEC.md:6` 等 | 规范版本演进未执行：头还写 v1.0 Frozen 但内容已含 §3.3.5 便利接口；FREEZE 派生错误计数 8→10 过期；KV-DESIGN 引用的"API v1.1 §3.18"在 API-SPEC 无对应编号 |

---

<a id="s6"></a>
## 六、P3 建议（50 项，紧凑清单）

**Rust（6）**：RUST-11 `execute_mutation` 失败分支丢弃已取出 pending warnings（:926-932、:940-949）；RUST-12 两处 `InvalidUtf8` 错误消息把 value 写成 key（:748、:2115-2121）；RUST-13 迭代器自称 lazy 实为持锁全量物化（注释误导 + 缺 limit 变体）；RUST-14 空 no-op 批在 Deferred 仍 +TxID / 快照模式触发整库轮转；RUST-15 并发首次 open 竞态窗口会互相阻塞至 `LockedError`（Step2 持锁不处理"他先注册"胜负）；RUST-16 死代码 `compare_utf8_bytes` / `LockFile::path`。

**.NET（9）**：DOTNET-16 readVer 检查排在 CRC 之前（位翻转报 `UnsupportedVersionError` 应为 `CorruptedError`）；DOTNET-17 netstandard2.0 目录 fsync 静默 no-op 无一次性告警（NET8 分支正确）；DOTNET-18 `Crc32(null)` 静默返回 0；DOTNET-19 死代码 `Utf8ByteComparer`/`LoadFromBytes`（已 grep 实证零调用）；DOTNET-20 Dispose 不解绑 `_watchers`；DOTNET-21 close 后 `_pendingWarnings` 有些入队项已无派发时机；DOTNET-22 对 OpDelete valLen / OpDropNamespace keyLen/valLen 不校验为 0（宽容解析）；DOTNET-23 AOL batch-clear 的 DELETE 键集合取自读锁快照，提交前他人 put 的键重开后复活（窗口极小，待跨语言定级）；DOTNET-24 Deferred 分支逐写 `currentMaxTxId++` 的 TxID 对账偏差（与 DART-14 同源）。

**Go（9；GO-8 部分已由 678ddfe 修复）**：GO-8 ~~缺 NewIoError/IsIoError~~ → 已补；仍缺 6 类 `IsXxx` sentinel（ConcurrentModification/InvalidUtf8/KeyTooLarge/InvalidNamespace/UnsupportedVersion/TypeMismatch）；GO-9 写侧无 UTF-8 校验（Go 是唯一可静默写入非法字节序列的语言）+ 加载不归一化 `"default"` + OpDelete/OpDrop ValueType 不校验 0x00；GO-10 `flushIfDirty` Snapshot 分支失败完全静默无 onWarning；GO-11 退避阶梯最后 120ms 空等 + `isLockContention` 不认 WinError 5；GO-12 Backup 不取 flushMu、两路并发 Backup 互踩 `dest+".tmp"`、自路径无防御；GO-13 `VerifyFileHeader` 未按规范拒绝非零 HeaderFlags/Reserved；GO-14 新门面 nil panic 面（`Snapshot()` closed 返回 nil → `s.NS().GetBytes` nil deref）；GO-15 AOL 压实/追加在写锁内做全量序列化+双 rename（跨语言同构，性能债）；GO-16 杂项（历史 P2-27 ClearNamespace 丢弃分配、P2-28 O(N) memmove、P2-31 锁文件只读 O_RDWR、P2-32 死注释、P2-33 Stats 注释矛盾、注册表键大小写、目录 fsync 无告警）。

**Dart（9）**：DART-8 缺 `onDirSyncUnsupported`（规范 §1.2#7 实锤要求，Dart 应在首次轮转一次性告警）；DART-9 加载器错误文案写死错误上限"1024"（真 65535）；DART-10 backup 兜底抛裸 `StateError` + dest 自冲突无守卫；DART-11 Watcher 回调异常溢出到触发写（AOL 已 append，复现"调用方重试→双累加"同款）；DART-12 相对路径 + 运行中 chdir 的写偏移（IO 不用 canonical 路径）；DART-13 孤立代理键塌缩（与 .NET 同族）；DART-14 deferred 逐写 TxID++ 对账偏差；DART-15 退避阻塞事件循环 ~230ms + `.stage` 失败残留/成功后 deleteSync 抛错误导；DART-16 三门面方法覆盖面不一致（便利性遗漏）。

**Kotlin（11）**：KOTLIN-12 flush 任务体 Error 逃逸静默停摆 + close 时 pendingWarnings 不派发；KOTLIN-13 `fileSize()` 是唯一漏 `checkDisposed` 的公开方法；KOTLIN-14 `.bak` 读取分支未包 try（裸 IOException）；KOTLIN-15 `.bak` 恢复后不 fsync（规范 §4.2.3 明示"复制恢复并 fsync"）；KOTLIN-16 死代码 `writeLocked`，其文档承诺与实际收口并存；KOTLIN-17 全无效 batch 恒 `modified=true`（白白落盘/耗 TxID）；KOTLIN-18 `WalRecoveryContractTest.kt:23` 过时注释（宣称无自动压实）；KOTLIN-19 jvmToolchain(17)（nio.file API 26+）与 README"适用于 Android"未决；KOTLIN-20 open 内两处直调 `onWarning`，回调抛异常炸 open；KOTLIN-21 `sharedInstance` 公开 getter 逃逸内部可变核心（ADR 受控面绕行）；KOTLIN-22 clearNamespace 保留的空命名空间重开后从 `namespaces()` 消失（格式层无表达，五语言共同 + 规范未明示）。

**横切面（6）**：CROSS-7 `.gitignore` 缺 `build/`/`.gradle/`/`.dart_tool/`；CROSS-8 workflow 工程化（无 timeout-minutes、crates.yml 缺顶层 permissions/concurrency、action 未 SHA-pin）；CROSS-9 CHANGES 与 git 史不可对账（11:12 批次五条目宣称的 PR 门禁与 deposit 行为无对应提交；dependabot.yml 在 git 史中从未存在）；CROSS-10 文档债集中（KV-DESIGN"三语言"残留、`Iterable<Entry>` 语义与五语言缓冲实现不符、LIB-PUBLISHING-GUIDE crate 名 `ckv` 旧写法、DEVELOPMENT-GUIDE crates 步骤停留在 token 时代、Kotlin 过时注释）；CROSS-11 README 新旧 sync-mode 名称混用（`Always` 与 `Immediate` 同文件两称）；CROSS-12 jitpack 版本注入靠 env fallback（未显式 `-Pversion`）。

---

<a id="s7"></a>
## 七、跨语言共性设计空白（五语言同构，建议规范级立项后统一修复）

1. **AOL 追加半写永久化**：RUST-10 / GO-3 / DART-4 三语言同构——`append_entries` 部分写失败不收缩文件，撕裂帧使同连接每写误报并发修改或重开即 Corrupted。方案：写前记录 `startPos`，失败 `Truncate(startPos)`（幂等，持连接锁内安全）。
2. **同尺寸短路削弱并发修改检测**：RUST-5 / DOTNET-5（Go 静默放行更弱 GO-6）——规范 §4.1.3 要求 `(MaxTxID, FileSize)` 二元组比对，等长外部替换全线漏检。方案：三元组（TxID,size,mtime）。
3. **`scan_max_tx_id` 弱校验**：历史 P1-8 家族残留在三语言（Rust/Dart/Kotlin 部分或全部未加 CRC/单调/越界校验）。
4. **`HeaderFlags` 非零不拒绝**：五语言全未实现（规范 §3.1 条款）+ `.corrupt.<unix_ms>` 取证备份缺失（规范 §3.4 阶段二条款）。
5. **孤立代理键塌缩**：.NET/Dart 明证，Rust 组合键有同样字节续接风险——建议写侧拒绝孤立代理项以对齐 Unicode 标量语义，并锁定跨语言。
6. **非法 UTF-8 键的迭代口径分裂**：同一语言内 keys/iterPrefix/scanRange 三套口径（.NET/Dart/Kotlin/Rust 均有部分缺口），spec §3.3.3 需扩到 scanRange/keys 并统一 skip。
7. **加载路径不归一化物理 `"default"` 分区**：Go/.NET/Dart 均观察到——外来文件孤子分区"读不到、删不掉"，需规范级统一（归一化或严格拒开）。
8. **clearNamespace 的空命名空间元数据**：重开即失（格式层无"空 ns"载体），需规范明示或格式扩展。
9. **Snapshot dispose 后语义**：Dart/Kotlin 仍抛裸异常，.NET 温和、Rust 部分未定义——跨语言收敛（温和返回空）。
10. **TxID 分配口径**（信息级）：Deferred 模式逐写分配 vs 快照批内连续，`stats().currentMaxTxId` 跨语言对账需口径声明。

---

<a id="s8"></a>
## 八、CI / 发布 / 版本 / 文档问题汇总

| 优先 | 事项 | 动作 |
| :--- | :--- | :--- |
| P1 | v0.2.1 无 tag/无产物（CROSS-1） | 打 tag 推送触发 crates.io/NuGet 发布；发布后复验 jitpack |
| P1 | API-SPEC §3.1 四处脱节（CROSS-2） | 按 KV-DESIGN §5.1.1 重写（en+zh） |
| P1 | Kotlin 错误体系（CROSS-3/P1-11） | 实类桥接 + 删除死类 + 修订 §4 失实表述 |
| P2 | PR 门禁与 changelog 失真（CROSS-4） | 恢复 Go/Kotlin/Dart PR 门槛（+10 分钟 runner）或修正 ci.yml 注释与 CHANGES 勘误 |
| P2 | 依赖扫描只剩 Cargo（CROSS-5） | 加 `actions/dependency-review@v4` 或最低限度周期审计 |
| P2 | 规范版本演进（CROSS-6） | API-SPEC 升 v1.1 + FREEZE 计数勘误 |
| P3 | workflow 加固（CROSS-8） | `timeout-minutes`、crates.yml permissions/concurrency、SHA-pin |
| P3 | 仓库卫生 | `.gitignore` 补 3 行（CROSS-7）；jitpack 显式 `-Pversion`（CROSS-12）；README 统一 `Immediate`（CROSS-11） |
| P3 | 文档债批次清理（CROSS-10） | 见 §6 横切部分清单 |

---

<a id="s9"></a>
## 九、测试缺口汇总（P0/P1 的直接防线缺失）

1. **.NET**：并发 `IncrementInt64` 用例（P0-1 凶手）；`AtomicReplaceWithRetry` 退避/回退路径（Windows-only：独占持有 dst 断言 elapsed≥200ms）；netstandard2.0 TFM 烟测；等长替换 `.db` 期望 `ConcurrentModificationError`；readOnly 打开不改文件系统；孤立代理项键序；外来畸形帧（valLen>16MB / OpCount=0 / OpDelete valLen≠0 / 物理 "default"）；`Backup(dest==self)`。
2. **Dart**：flushAsync 并发写回归（可把探针改 10 轮最小用例）；Snapshot dispose 读语义；scanMaxTxId 损伤分类与加载器一致性；open 不存在目录的 `IoError` 分类；Timer 失败告警；终结器路径。
3. **Go**：Backup 并发写 race 回归（GO-1）；Snapshot.Close 与并发读；`DbOptions{}` 零值归一化；append 部分写失败→续写→重开；AOL final flush fsync 失败传播（可注入 sync 探针）；外部删库期望并发修改；Int64 补码绕回。
4. **Kotlin**：MANUAL"flush 前不落盘"断言；并发读+删压力（P1-9）；AOL+DEFERRED fsync 窗口可控注入（P1-10）；Value>16MB 错误类型断言；watcher mutate event.value 后内存/磁盘未被污染；Snapshot 关闭后温和读；readOnly `.tmp` 无 syscall 效果断言；活跃单例下 `backupFile` 行为；未知 opCode 拒开。另：ktlint/detekt 缺位（死代码/过时注释无人清扫）。
5. **Rust**：close 后 compact/flush 契约；并发首次 open；`onOptionsConflict` 内再嵌套 open 的死锁；非 UTF-8 key 迭代口径一致性；AOL 同进程撕裂尾帧后的恢复语义；backup 自路径与 `.stage` 清理；`scan_max_tx_id` 恶意 payload_len 越界；readOnly 打开时 orphan `.tmp` 的"零盘上写"断言。

---

<a id="s10"></a>
## 十、历史 review（20261017）修复落地对账

历史 P0×7 **全部修复**（P0-1/P0-2 Dart、P0-3 Go、P0-4/P0-5/P0-6 .NET、P0-7 Dart——含关键的 ADR 单例所有权重构与两阶段提交，均有专项回归）。历史 P1×40 落地对账如下（✅ 落地 / ⚠️ 部分 / ❌ 未修 / — 不再适用）：

| 历史 | Rust | .NET | Go | Dart | Kotlin |
| :--- | :--- | :--- | :--- | :--- | :--- |
| P1-1 快照 O(N) 拷贝 | ⚠️ 批内去重已做，快照存活仍逐写 O(命名空间) | ✅（CoW 单次克隆） | ⚠️（`clonedInTx` 单批一次但无条件克隆，GO-7） | ✅ | ✅ |
| P1-2 迭代器物化 | — 按规范 §3.3.3 设计；改为补 limit 变体 | 同左 | 同左 | 同左 | 同左 |
| P1-3 NsLen≤255 加载校验 | ✅ | ✅ | ✅ | ✅ | ✅ |
| P1-12 关闭后读写拦截 | ✅ | ✅ | ✅ | ✅ | ✅ |
| P1-14 Windows rename 退避 | ✅（[5,15,30,60,120] 有） | ❌ **DOTNET-3**（漏 UAE） | ✅（仅共享冲突重试，WinError 5 不重试为刻意对齐） | ✅ | ✅ |
| P1-15 二次 open 配置比较 | ✅ | ✅（ConflictingOptionsError） | ✅ | ✅ | ✅ |
| P1-16 netstandard2.0 File.Replace | — | ❌ **DOTNET-2** | — | — | — |
| P1-19 `.tmp` 晋升失败必须硬错 | ✅ | ✅（但 DOTNET-7 读失败误删残留） | ✅ | ✅ | ✅ |
| P1-20/30 防御性拷贝 | ✅ | ✅ | ✅ | ✅ | ✅（事件侧漏，KOTLIN-8） |
| P1-21/6 提交失败内存回滚 | ✅ | ✅ | ✅ | ✅ | ✅（IMMEDIATE 分支；**KOTLIN-3 为 AOL 后台面） |
| P1-22 错误类缺失 | — | — | — | ✅（backup 兜底仍裸 StateError，DART-10） | ⚠️（typealias 已引入，678ddfe） |
| P1-24 注册表强引用 | — | ✅（refCount+finalizer） | ✅ | ✅（Finalizer 方案） | ✅ |
| P1-25 readOnly 改文件系统 | ⚠️（RUST-3） | ❌（DOTNET-6） | — | ⚠️（DART-7） | ⚠️（KOTLIN-7） |
| P1-26 只读拦截收口 | ✅ | ✅ | ✅ | — | ✅（678ddfe 收口） |
| P1-27 后台刷盘代际守卫 | ✅（FIX-01 全链） | ✅（_dirtyGeneration） | ✅（generation 守卫） | ⚠️（DART-4） | ⚠️（KOTLIN-3 仅 Snapshot 有） |
| P1-28 双读锁竞态 | — | ✅（单锁内完成） | — | — | ❌ **KOTLIN-2** |
| P1-29 前台写持锁 O(N) | ✅ 两阶段 | ✅ 两阶段（⚠️ AOL 压实分支仍在写锁全量重写） | ✅ 两阶段（⚠️ GO-15 同构） | ✅ | ✅ |
| P1-33 锁内回调 | ✅（⚠️ open 冲突路径外漏 RUST-2） | ✅ | ✅ | ✅ | ✅（⚠️ open 直调 KOTLIN-20） |
| P1-34 onDirSyncUnavailable | ❌（RUST-4） | ⚠️（NET8 有，netstandard 静默） | ⚠️（无告警） | ❌（DART-8） | ✅ |
| P1-36 非法键过滤 | ⚠️（RUST-9） | ⚠️（DOTNET-15） | ✅（读侧）；加载双苞容 GO-9 | ❌（DART-3） | ⚠️（KOTLIN-10） |
| P1-37 四值事件 ABI | ✅ | ✅ | ✅ | ✅ | ✅ |
| 历史跨语言 P2 清单 | 批次一已修 7 项中的 FIX 种类见各语言报告 | P2-9/10/13/17/18/19 残留 | **P2-20/22/23/24/26/27/28/29/31/32/33/34 整批未修** | P2-48/51/53/55/56/57 残留 | U-5 ❌（KOTLIN-9） |

**结论**：P0/核心 P1 的修复质量高、有回归测试；未落地项几乎全部集中在 ① .NET rename 家族、② Go 历史 P2 技术债、③ 扫描/迭代语义与目录 fsync 告警类"跨语言一致性长尾"。历史批次五"工序项"（CI 门禁、dependabot）存在 changelog 与 git 史失真（CROSS-4/9）。

---

<a id="s11"></a>
## 十一、修复优先级路线图（批次六建议）

1. **第一批（P0，当天可完成）**
   - DOTNET-1：IncrementInt64 读改写移入写临界区 + 并发回归测试；
   - DART-1：flushAsync 栅栏 + 互斥收敛 + 探针改造回归；
   - RUST-1：`compact()` 补 `check_writable()` + 回归。
2. **第二批（P1，1~2 人日）**：KOTLIN-1（1 行）、KOTLIN-4（3 行）、RUST-2（告警移锁外）、GO-1（读移锁内 + race 回归）、GO-2（O_RDWR 统一 syncAppendLog）、DOTNET-2/3（同文件一并）、DOTNET-4（接告警通道）、KOTLIN-2/3（锁内重写 + 并发回归）、DART-2（温和语义 + 测试）。
3. **第三批（P2 清账）**：按 §5 分语言清单推进；GO-4（零值归一化）、GO-5、RUST-5（go/dotnet 同尺寸三元组统一）优先。
4. **发布收口**：P1 修复完毕后打 tag v0.2.1 → 触发发布 → 复验 registry/jitpack；API-SPEC §3.1 重写 + v1.1 版本演进；ci.yml 真实门禁决策（a/b 二选一）记录。
5. **规范级立项（跨语言）**：§7 的 10 项共性空白逐项走决策文档后统一实施（AOL 撕裂自愈、HeaderFlags、孤立代理项、迭代口径、`"default"` 加载、空 ns 元数据等）。

---

<a id="sa"></a>
## 附录 A：验证通过项摘要（按语言，核对通过无需改动）

- **字节级格式（五语言全部一致）**：32B 文件头（`UKVDB003` / ver=3 LE / flags+reserved 零 / CRC 覆盖 0..28）、28B 帧头（`0x4D52464B` 即 "KFRM" LE、HeaderCRC 覆盖 8..28、TxID u64 LE、OpCount∈[1,1M]、PayloadLen≤64MB）、10B 条目头（OpCode/ValueType/NsLen u16/KeyLen u16/ValLen u32 全小端）、贪心装货条件、快照仅 OP_PUT、空库 32B——golden 四向量逐字节锁定。
- **CRC32（五语言）**：0xEDB88320 表法/Go 硬件等价，`"123456789"→0xCBF43926` 全部有测试。
- **TxID 铁律（五语言）**：末帧继承/空库 0/清空不归零/`max(mem,disk)+1`/多帧连续递增；空库特判防伪并发修改（Rust 2758-2762）。
- **锁协议 C9（五语言）**：内核锁、锁文件不删不读、readOnly 共享、默认 10s 指数退避、close 显式释放。
- **两阶段/四阶段写管线（五语言）**：短锁预检+内存变更+CoW 快照提取 → 锁外序列化 fsync → 短锁轮转 → 锁外派发；失败内存原子回滚 + `isDirty` 保留 + 标准 Io 错误。
- **batch()（五语言）**：纯内存 staging、抛错丢弃、read-your-writes（倒序 + 墓碑）、批内 clear/drop 撤销同批 put、事件锁外合批派发。
- **压实 FIX-21（五语言）**：`garbage >= max(threshold, live/4)` + 死键比例（2MB/50%）+ threshold==0 全关 + O(1) livePayload 账本 + 批量装载重算。
- **Deferred/Manual（Rust/.NET/Go）**：后台线程全生命周期 + 代际守卫（Kotlin/Dart 的例外即本轮发现）。
- **ADR 单例所有权**：五语言 refCount + 幂等 close + 句柄隔离 + readOnly 冲突硬错全部落地；.NET 的三重终结器兜底。
- **发布矩阵（文档级）**：默认值 8 项 × 5 语言逐项一致；Watcher OP 1/2/3/4 全语言对齐；`NamespaceScope` 五语言全齐与 §3.3.5 一致。
- **仓库治理**：黄金向量确定性再生成成立；版本 stamp 统一；UTF-8 全合规；无构建产物入库。

<a id="sb"></a>
## 附录 B：探针脚本清单（temp/probes/，未入库）

| 目录 | 脚本 | 结论 |
| :--- | :--- | :--- |
| temp/probes/rust/ | probe_compact_closed.rs（临时 cargo 工程，已清理） | close 后 compact 实锤（P3 探针） |
| temp/probes/dotnet/ | probe（8 线程 increment + readonly tmp 晋升） | P0-1 / DOTNET-6 复现 |
| temp/probes/dart/ | flush_async_race_probe.dart | P0-2 复现 30/30 |
| temp/probes/go/ | probe1_crc_sync.go / probe2,3_backup_race2.go | GO-2（Access denied）/ GO-1（DATA RACE） |
| temp/probes/cross/ | analyze_workflows.py / check_versions.py / gen_testdata.py | CI 静检 / 版本同步 / 确定性复现 |

---

## 审查诚实度声明

- 本报告所有 P0/P1 均由主会话在 `678ddfe` 亲自逐行复核到代码（非转述代理）；P2/P3 为专项代理静态核实并标注待确认项。
- 五语言 lint/测试/黄金向量全部实跑；全部探针为一次性可复现脚本，工作树与 testdata 未被修改（`git status` 干净）。
- 每一项发现均给出文件:行与修复建议；影响评估标注了触发前提与跨语言对照；"待确认"条目（如 DOTNET-23、DART-13、CROSS-3 裁决方向）需维护者拍板后进入批次。
