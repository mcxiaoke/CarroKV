# CarroKV 全项目代码审查报告（核心功能 / 架构 / 性能 / 数据与 IO 正确性 / 接口完整性）

> 审查时间：2026-10-17 19:20 ~ 20:13
> 审查对象：`src/rust`、`src/dotnet`、`src/go`、`src/kotlin`、`src/dart` 共 5 门语言实现（约 20,000 行生产代码 + 约 12,000 行测试）
> 判定基准：`docs/KV-DB-DESIGN-SPEC.md` v3.2、`docs/API-SPEC.md` v1.0
> 审查方式：**逐行通读全部生产源码** + **全量测试套件实跑** + **针对关键假设的独立探针实测**（探针已清理，`temp/` 未纳入版本控制）
>
> **本报告已合并外部集成反馈**：合并了 `temp/ckv-issues-report/` 下 3 份文档——
> `REVIEW-20261017-195853-external-feedback.md`、`ISSUES-DOTNET-20261017.md`、
> `FEEDBACK-ANALYSIS-AND-PROPOSALS-20261017.md`，
> 它们记录了真实项目 **StickyNotesDesktop**（WPF 便签应用）从 SQLite 迁移到 CarroKV 后的一次**线上启动崩溃**及 9 条 API 体验反馈。
> 合并内容见 [§1.3 现场证据](#s-field-evidence) 与各缺陷条目，**已驳回的反馈亦如实记录**（见 [§7.4 已驳回的外部反馈](#s-field-evidence)）。

---

## 目录

- [一、审查方法与实证基础](#s-method)
  - [1.1 全量测试套件实跑结果](#s-tests-run)
  - [1.1.1 外部现场证据：真实线上崩溃](#s-field-evidence)
  - [1.2 独立探针实测（12 组）](#s-probes)
- [二、总体结论](#s-summary)
- [三、P0 致命缺陷（7 项）](#s-p0)
- [四、P1 严重缺陷（40 项）](#s-p1)
  - [4.1 Rust](#s-p1-rust) ｜ [4.2 .NET](#s-p1-dotnet) ｜ [4.3 Go](#s-p1-go) ｜ [4.4 Dart](#s-p1-dart) ｜ [4.5 Kotlin](#s-p1-kotlin) ｜ [4.6 跨语言共性](#s-p1-cross)
- [五、P2 一般缺陷](#s-p2)
- [六、跨语言架构性问题](#s-arch)
- [七、接口缺失与易用性问题](#s-api)
  - [7.5 已驳回的外部反馈](#s-rejected)
- [八、测试体系与 CI 覆盖](#s-tests)
- [九、工程化与打包](#s-engineering)
- [十、文档债](#s-docs)
- [十一、修复优先级路线图（六批次）](#s-roadmap)
- [附录 A：需要进一步确认的项](#s-appendix)

---

<a id="一审查方法与实证基础"></a>
<a id="s-method"></a>
## 一、审查方法与实证基础

本报告的每一条结论都标注了证据等级，不做无依据的推测。

<a id="s-tests-run"></a>
### 1.1 全量测试套件实跑结果（2026-10-17 本机，Windows 10）

| 语言 | 命令 | 结果 |
| :--- | :--- | :--- |
| Rust | `cargo test --release` | **EXIT=0**，约 130 个测试全部通过（含 2 个 doctest） |
| .NET | `dotnet test tests/Ckv.Tests -c Release` | **EXIT=0**，`失败: 0，通过: 109，已跳过: 0` |
| Go | `go test ./...` | **EXIT=0**，`ok github.com/mcxiaoke/CarroKV/src/go` |
| Go 静态检查 | `go vet ./...` / `gofmt -l .` | 均无输出（干净） |
| 黄金向量 | `Get-FileHash -Algorithm SHA256 testdata/*.db` | 4 个 `.db` 的 SHA-256 **与 `MANIFEST.txt` 完全一致** |

> **重要提醒**：现有测试全绿，但本报告列出的 **全部 P0 与绝大多数 P1 缺陷，现有测试套件均未覆盖**。测试通过不能作为质量结论。

<a id="s-field-evidence"></a>
### 1.1.1 外部现场证据：真实线上崩溃（合并自 `temp/ckv-issues-report/`）

StickyNotesDesktop 项目（WPF 便签应用）从 SQLite 迁移到 CarroKV 后，**应用启动即崩溃**。现场日志：

```text
[19:25:57.170] [INFO] [CkvDatabaseContext] 数据库打开成功，当前 Schema 版本: v1。
                    路径: C:\Home\Tools\StickyNotes\app_data\notes.db
[19:25:57.178] [INFO] [BackupService] 成功创建数据库冷备份: ...\backups\notes_20261017.db
[19:25:57.180] [ERROR] [App] 恢复桌面便签失败，启动中止: Cannot access a disposed object.
Object name: 'System.Threading.ReaderWriterLockSlim'.
System.ObjectDisposedException: Cannot access a disposed object.
   at System.Threading.ReaderWriterLockSlim.TryEnterReadLockCore(TimeoutTracker timeout)
   at System.Threading.ReaderWriterLockSlim.EnterReadLock()
   at Ckv.CkvDb.ScanRange(String ns, String startKey, String endKey)
   at StickyNotes.Data.CkvNoteRepository.GetAllActiveAsync(...)
```

**这条现场日志的栈与代码路径完全吻合**：

1. 备份服务执行 `using var db = CkvDb.Open(path, opts)`；
2. `Open` 命中进程内单例注册表，返回主线程**正在使用的同一个实例**（`Db.cs:502-508`，无引用计数）；
3. 离开 `using` 作用域触发 `Dispose()`，该实例就地执行最终刷盘、释放跨进程连接锁、并 **`_rwLock.Dispose()`**（`Db.cs:2604-2699`）；
4. **2ms 后**主线程读便签 → `ObjectDisposedException`，启动中止。

**间隔仅 2ms：备份成功、主业务随即崩溃。** 这不是理论风险，是已发生的生产事故。

**它给本次审查的两个关键增量**：

| 增量 | 说明 |
| :--- | :--- |
| **根因升级** | 本报告在纯静态阶段把"Dispose 竞态 / 注册表死弱引用"列为 P1 与 P0，但**未识别其真正根因是「进程内共享实例没有所有权模型」**。外部反馈提供了现场佐证，也暴露了这一定位不足 |
| **范式证明** | 外部反馈同时证明了一个被低估的 API 易用性陷阱：`using var db = CkvDb.Open(...)` 在单例语义下是**错误范式**（见 [§7.4](#s-ext-ergonomics)） |

<a id="s-probes"></a>
### 1.2 独立探针实测（本次审查新增的验证）

针对静态阅读中形成的假设，编写了 12 个一次性探针在真实环境运行。以下结论为**第一手实测证据**，而非推断：

#### 探针 1 — Rust 同进程并发打开同一路径（假设：不成立）

```
[P1] 8 threads open the same path in one process -> Ok=8/8
     ["Ok","Ok","Ok","Ok","Ok","Ok","Ok","Ok"]
```

Rust 的 `open_internal` 进程内单例（`db.rs:341-443`）在本轮压力下**未复现**失败：8 个线程全部拿到同一实例。

> **诚实记录**：这与 Go 实现形成对比——Go 侧的同类问题被其审查用 8 goroutine **稳定复现为 7/8 `LockedError`**。Rust 未复现不等于不存在（线程未能真正重叠也是可能原因），但**不能据此断言 Rust 有该缺陷**。该项列入[附录 A](#s-appendix)。

#### 探针 2 — `close()` 的作用域（假设：成立）

```
[P2] write via the surviving clone after the other called close():
     Err(Locked("Database is closed: writes are refused after close()"))
```

**确认为真实设计陷阱**，见 [P1-13](#p1-13)。

#### 探针 3 — 两个只读连接共存（假设：不成立，行为正确）

```
[P3] 2nd READ-ONLY open while one is held -> None(无错误) after 131.2µs
```

Rust 只读连接使用 `try_lock_shared()`，多个读者可共存，符合 bbolt/LMDB 语义。✅

#### 探针 4 — 存活快照对写路径的成本（假设：成立，且被低估）

```
[P4] single write, no live snapshot = 5.73ms
     single write, with 1 live snapshot = 7.05ms   (+23%)
     again = 5.60ms
     snapshot sees 5000 entries, db sees 5000（隔离正确）
```

**确认**：只要存在任一存活快照，**该命名空间的每一次写都会触发 `Arc::make_mut` 的全量 BTreeMap 深拷贝**。5000 条目时+23%；条目越多、命名空间越大，惩罚越重。这是 README §6 重点宣传的能力（"快照内的全库迭代查询也绝不受干扰"）的实际代价。见 [P1-9](#p1-9)。

#### 探针 5 — 范围扫描的全量物化（假设：成立）

```
[P5] scan_range(all) -> 3000 entries, 1,500,000 B of cloned values, 449µs
```

**确认**：`scan_range` / `iter_prefix` 会把全部匹配项**连同 value 完整深拷贝**成 `Vec<Entry>`。对 50MB 级库，一次全量扫描即复制 50MB 内存并**在持有读锁期间**完成，写者全程阻塞。见 [P1-10](#p1-10)。

#### 探针 6 — 加载期未校验 `NsLen ≤ 255`（假设：成立，且后果严重）

```
[P6] file with a 300-byte ns (spec limit 255): open OK
     namespaces()[0].len() = Some(300)
     value readable = Err(InvalidNamespace("Namespace byte length 300 exceeds maximum allowed 255"))
     re-write via API = Err(InvalidNamespace(...))
```

**确认为 P1 缺陷**：一个外部构造（或未来某个版本写入）的超长命名空间会被**成功加载并常驻内存**，出现在 `namespaces()` 中，但：

- `get_string(ns, k)` → `InvalidNamespaceError`
- `put_string(ns, k, v)` → `InvalidNamespaceError`
- **`delete(ns, k)` → `InvalidNamespaceError`**
- **`drop_namespace(ns)` → `InvalidNamespaceError`**
- **`clear_namespace(ns)` → `InvalidNamespaceError`**

即：**该命名空间既读不出、也删不掉**，是一个不可回收的孤儿。它会被每次快照重新序列化并传播到 `.bak` 与所有 `backup()` 产物，进而污染其它四门语言（其中部分在加载期即校验该上限）。见 [P1-11](#p1-11)。

#### 探针 7 — 键序为真正的无符号 UTF-8 字节序（假设：成立，实现正确）

```
[P7] inserted ["z", "ÿ", "\u{ffff}", "𐀀", "\u{7f}"]
     keys() order -> ["z", "\u{7f}", "ÿ", "\u{ffff}", "𐀀"]
```

字节序核对：`z`(0x7A) < 0x7F < U+00FF(C3 BF) < U+FFFF(EF BF BF) < U+10000(F0 90 80 80)。✅ Rust 排序铁律落实正确。

#### 探针 8 — 默认引擎模式的写成本（假设：成立，已量化）

```
[P8] DEFAULT (SnapshotAtomic+Immediate), live=  500: steady write median=2.29ms  (~437 ops/s)
     DEFAULT (SnapshotAtomic+Immediate), live= 2000: steady write median=2.93ms  (~341 ops/s)
     DEFAULT (SnapshotAtomic+Immediate), live= 5000: steady write median=4.43ms  (~226 ops/s)
```

**关键量化结论**：默认配置下写延迟**随库大小线性增长**。仅 500 条目时已需 2.29ms/写（此时主要成本是 fsync，非序列化）；到 5000 条目时序列化成本开始显现。按此斜率外推，5 万条目（约 5.7MB）约 40ms/写 ≈ 25 ops/s。README 的"默认适合 <2MB"结论成立，但**没有任何运行时保护或告警**阻止用户把默认模式用在 50MB 上（届时吞吐比 `AppendOnlyLog` 差 2 个数量级）。见 [P1-1](#p1-1)。

#### 探针 9 — `AppendOnlyLog + Manual` 的真实持久性边界（假设：部分修正）

```
[P9] AOL+Manual, put then abandon (no close): file=72 B, reopened value = Some("v")
```

**修正了一个常见误解**：AOL 模式在 `Manual` 下仍会 `write(2)` 到页缓存，因此**能抵抗进程崩溃**，只不能抵抗掉电。README "进程异常退出会丢失最近一个刷盘周期的写入"对 AOL 不准确，对 `SnapshotAtomic+Manual` 才准确。见 [§10](#s-docs) D-5。

#### 探针 10 — Dart `"default"` 保留别名未归一化（**独立复核确认**）

```
[A1] keys("")        -> [shared]
[A1] keys("default") -> []
[A1] => aliases equivalent? NO  <-- DIVERGENCE
```

**独立复核确认**：Dart 未实现 DESIGN-SPEC §3.3.1 与 §1.2 第 8 条硬性锁定的 `"default"` → `""` 归一化。这是**跨语言互通的直接断裂**。见 [P0-2](#p0-2)。

#### 探针 11 — Dart 超长命名空间可一次性写坏数据库（**独立复核确认**）

```
[B1] putString(70000-byte ns) -> ACCEPTED (no error)
[B2] reopen -> CorruptedError: Malformed entry payload at offset 32
[B2] => the single put above permanently destroyed the database.
[B3] opening .bak -> OK   (.bak 不存在，无备份可回退)
[C1] putString(300-byte ns) -> ACCEPTED (spec limit is 255)
```

**独立复核确认，且后果比静态分析更严重**：Dart 的 `normalizeNamespace` **完全不校验长度**，`Constants.maxNsLen = 255` 定义了却零引用。70000 字节命名空间写入时 `NsLen` 字段被截断为 16 位，条目头声明长度与实际载荷不符，**单次 `putString` 即永久摧毁整个数据库**，且 `.bak` 不存在时无任何自愈途径。这是本次审查发现的**最高危缺陷**。见 [P0-1](#p0-1)。

#### 探针 12 — Go 的 Windows 共享冲突重试谓词失效（**独立复核确认**）

```
GOOS = windows
Go's synthetic syscall.EACCES  = 536870913
Go's synthetic syscall.EBUSY   = 536870928
Go's synthetic syscall.ETXTBSY = 536871035
Real Win32 codes are 5 / 32 / 33 -- in a completely different range.

os.OpenFile(<directory>, O_RDWR)   -> errno=536870954  currentPredicate=false
os.OpenFile(<missing>, O_RDWR)     -> errno=2          currentPredicate=false
os.OpenFile(<missing dir>/x.db)    -> errno=3          currentPredicate=false
os.Rename(file -> open directory)  -> errno=5          currentPredicate=false  proposed(5/32/33)=true

VERDICT: a genuine ERROR_ACCESS_DENIED (errno 5) is NOT recognised.
```

**独立复核确认**：Go 把 `syscall.EACCES` 等定义在 `APPLICATION_ERROR + n` 的**合成区间**，与 Win32 真实码完全无关；而从 OS 传回的 errno 是**裸的 5/32/33**。`isWindowsSharingViolation` 比较的是两个不同命名空间，**恒为 false**，规范 §4.2 要求的 `[5,15,30,60,120]ms` 共 230ms 退避**一次都不会执行**。讽刺的是同一仓库的 `lock_windows.go:39-42` 正确地手工定义了 `errSharingViolation syscall.Errno = 32`——作者知道真实码值，只是这一处写错了。见 [P0-3](#p0-3)。

---
<a id="二总体结论"></a>

<a id="s-summary"></a>
## 二、总体结论

### 2.1 分层评价

| 维度 | 评价 | 依据 |
| :--- | :--- | :--- |
| **磁盘格式正确性** | **优秀** | 5 语言逐字对齐 v3.2：字节序、CRC32 覆盖范围、贪心装箱条件、TxID 铁律、空库 32B、`(ns,key)` 排序全部核对通过；黄金向量 4/4 哈希匹配 |
| **崩溃一致性叙事** | **优秀** | 两代原子轮转、`.tmp` 晋升、`.bak` 默认拒绝回退（D1）、压实失败退避（D2）、损坏分级（C4）都经过实测驱动 |
| **Watcher 死锁防御** | **优秀** | 5 语言全部遵守"锁内收集、锁外派发"，实测无重入死锁 |
| **压实策略（FIX-21）** | **优秀** | 双触发器（垃圾量 + 死键比例）、O(1) 账本、装载后重算，5 语言全部落实 |
| **并发写与前台落盘的锁粒度** | **差** | 默认模式下全库序列化 + fsync 全程持有写锁，读操作全程阻塞（5 语言同病） |
| **API 契约完整度** | **差** | `isClosed` 0/5 缺失；Kotlin 缺 `backup`/`iterPrefix`；Dart 缺 `InvalidNamespaceError`/`IoError`；Kotlin 错误体系 5/10 缺失 |
| **输入校验完整性** | **差** | Dart 命名空间零校验（P0）；加载期 5 语言均不校验 `NsLen ≤ 255`；`HeaderFlags` 5 语言均不校验 |
| **写入侧内存所有权契约** | **差** | 5 语言 `put_bytes`/`PutBytes` **全部**不拷贝调用方缓冲区，读侧却全部拷贝，语义不对称 |
| **平台适配** | **差** | Go 的 Windows 共享冲突处理完全失效（P0）；Kotlin `jvmToolchain(17)` 与 Android 宣称矛盾；.NET `netstandard2.0` 三处语义分叉 |
| **生命周期与资源管理** | **差** | Dart `Timer` 在构造期泄漏导致进程挂死；.NET 注册表死弱引用导致 `Open` 死循环；5 语言 `close()` 后行为不统一 |
| **测试覆盖** | **中** | 全绿但对 `renameWithRetry`、并发 `Open`、`OnWarning` 重入、`put_bytes` 别名、命名空间长度全部零覆盖 |
| **CI 门禁** | **差** | 5 语言中 3 门仅周跑；DEVELOPMENT-GUIDE 规定的质量门禁多数未进 CI |

### 2.2 一句话总结

> **CarroKV 的磁盘格式层与崩溃一致性协议是可以信任的**——字节序、排序、CRC、TxID 单调性、装箱规则这些"错了就跨语言数据损坏"的高危点全部核对通过，且有真实事故驱动的精细决策（`dirtyGeneration`、`flushMu`、压实退避、D1/D2）。
>
> **但面向调用方的那一层欠账明显**：默认引擎模式的开销没有任何护栏（[P1-40](#p1-40) / [§7.2](#s-engine-cost)）、输入校验有能让单次写入永久摧毁数据库的漏洞（[P0-1](#p0-1) / [P0-2](#p0-2)）、平台适配有一处让 Windows 容错完全失效的常量错误（[P0-3](#p0-3)）、**进程内共享实例没有所有权模型，任一借用者 `close`/`Dispose` 即全员失效（[P0-4](#p0-4)，已造成真实线上崩溃**）。
>
> 现有测试 **全绿** 但覆盖不到上述任何一条——这是最需要警惕的信号。**外部集成反馈（[§1.1.1](#s-field-evidence)）甚至证明了一件更严重的事：有一个测试正在把 `Dispose` 的错误行为反向固化为"正常"。**

<a id="s-counts"></a>
### 2.3 缺陷分布总览

| 级别 | 数量 | 其中跨语言共性 | 其中单语言独有 | 其中来自外部反馈 |
| :--- | ---: | ---: | ---: | ---: |
| P0 致命 | 7 | 0 | 7 | 1 |
| P1 严重 | 40 | 12 | 28 | 6 |
| P2 一般 | 47 | 15 | 32 | 0 |
| P3 改进 | 40 | 9 | 31 | 0 |

> **外部反馈的合并贡献**：9 条反馈中 3 条完全成立并被提升为 P0/P1（#1 Dispose 所有权 → [P0-4](#p0-4)、#9 配置冲突 → [P1-15](#p1-15)、遗漏项 A → [P1-12](#p1-12)），1 条遗漏项 B → [§7.4](#s-ext-ergonomics)，2 条部分成立需拆分（#4、#6），1 条归因错误（#2，见 [§7.3](#s-async-correction)），1 条已过时（#3），2 条属刻意取舍（见 [§7.5](#s-rejected)）。**其最大价值是提供了现场证据与真实使用视角，而非优先级判断。**

---

<a id="s-p0"></a>
## 三、P0 致命缺陷

<a id="p0-1"></a>
### P0-1 Dart：命名空间长度零校验，单次 `putString` 永久摧毁数据库

**位置**：`src/dart/lib/src/binary_helper.dart:127-130`（`normalizeNamespace`）、`src/dart/lib/src/format.dart:77`、`src/dart/lib/src/constants.dart:14`

**问题**：

```dart
static String normalizeNamespace(String? ns) {
  if (ns == null || ns.isEmpty) return '';
  return ns;                       // 没有任何长度校验
}
```

`Constants.maxNsLen = 255` 被定义但**全仓零引用**。而写入侧 `RawOpEntry.encodeTo` 用 `BinaryHelper.writeUint16LE(hdr, 2, namespace.length)` 写入 `NsLen` 字段（uint16），`writeUint16LE` 直接取低 16 位：

```dart
bytes[offset]     = value & 0xFF;
bytes[offset + 1] = (value >> 8) & 0xFF;   // 超过 65535 的高位被静默丢弃
```

**实测后果**（探针 11，独立复核）：

```
putString(<70000 字节 ns>, 'k', 'v')  ->  无异常，写盘成功
close() 后重新 open                   ->  CorruptedError: Malformed entry payload at offset 32
.bak 是否存在                         ->  false（全新库单次写入，无备份可回退）
```

条目头声明 `NsLen=4464` 与实际写入的 70000 字节载荷不匹配，**整库永久不可读**。载荷 CRC 本身正确（CRC 覆盖真实字节），因此加载器在 `format.dart:334` 的 `pIdx != pEnd` 判定处硬失败——这个失败是**正确**的，但为时已晚。

300 字节命名空间（超过规范的 255 上限但仍在 uint16 内）同样被静默接受，落盘后在 Dart 内部形成一个不可读也不可删的孤儿分区。

**影响**：任何 `ns` UTF-8 字节长度 > 255 的写入。在 Flutter 中 `ns` 常由路由名/模块名拼接，构造超长串并非不可能。**这是本次审查发现的最高危缺陷：一次 API 调用即可造成不可逆的数据丢失。**

**规范依据**：DESIGN-SPEC §3.2.2「`NsLen` …**硬上限 `0 <= NsLen <= 255`，超长在 API 校验时立即抛出 `InvalidNamespaceError`**」；§1.2 第 8 条为「硬性锁定」；§5.1.2 错误表。

**修改建议**：

```dart
static String normalizeNamespace(String? ns) {
  final s = (ns == null || ns.isEmpty) ? '' : ns;
  if (s == defaultNamespaceAlias) return '';          // 同时修 P0-2
  final len = utf8.encode(s).length;                   // 注意用字节长度
  if (len > Constants.maxNsLen) {
    throw InvalidNamespaceError(
        'Namespace byte length $len exceeds maximum ${Constants.maxNsLen}');
  }
  return s;
}
```

必须确保**所有** ns 入口（`_putInternal`、`incrementInt64`、`delete`、`dropNamespace`、`clearNamespace`、`batch`、`backup` 序列化）统一走这一条路径。Rust `types.rs:78-91` 已是正确形态，可直接对照移植。

---

<a id="p0-2"></a>
### P0-2 Dart：未实现 `"default"` 保留别名归一化，跨语言互通直接断裂

**位置**：`src/dart/lib/src/binary_helper.dart:127-130`

**问题**：`normalizeNamespace` 只把 `""` 归一化，`"default"` 原样返回。

**实测**（探针 10，独立复核）：

```
keys("")        -> [shared]
keys("default") -> []
=> aliases equivalent? NO
```

**双向数据损坏**：

1. **Dart 写 → 其它语言读**：Dart 以 `NsLen=7` 写入 `"default"` 分区。Rust/Go/.NET/Kotlin 读该文件时把 `"default"` 归一化成 `""`，于是这条数据被读进**默认命名空间**，与 Dart 的 `""` 数据混作一团。
2. **同一逻辑键在两端落到不同数据**：Dart 自己的 `"default"` 分区在其它语言眼里就是默认分区。

`src/go/industrial_test.go:132-140` 与 `src/rust/tests/industrial_suite_test.rs:103-114` 都有专门断言这个别名等价性；**Dart 侧无对应测试**，所以周跑的 interop 链路也没能拦住它。

**规范依据**：DESIGN-SPEC §3.3.1 第 1 条、§1.2 第 8 条（硬性锁定）：「`"default"` 被定义为默认命名空间的保留别名……调用方**无法**在物理上创建一个真实名为 `"default"` 的独立命名空间」。

**修改建议**：与 P0-1 合并修复（同一函数）。**注意这是破坏性行为变更**——需检查已有 Dart 用户数据中是否存在物理名为 `"default"` 的分区，若有则提供迁移路径（一次性读取并重写为默认命名空间），并在 CHANGES 中标注。

---

<a id="p0-3"></a>
### P0-3 Go：Windows 共享冲突重试谓词恒为假，规范要求的 230ms 退避一次都不执行

**位置**：`src/go/db.go:2419-2431`（判定）、`src/go/db.go:2402-2417`（重试循环）

**问题**：

```go
func isWindowsSharingViolation(err error) bool {
	if runtime.GOOS != "windows" { return false }
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return errno == syscall.EACCES || errno == syscall.EBUSY || errno == syscall.ETXTBSY
	}
	return false
}
```

其上方注释断言「`ERROR_ACCESS_DENIED=5 / ERROR_SHARING_VIOLATION=32 / ERROR_LOCK_VIOLATION=33`，在 Go 的 syscall 映射中分别为 EACCES / EBUSY / ETXTBSY」——**这个断言是错的**。

**实测证据**（探针 12，独立复核）：

```
Go's synthetic syscall.EACCES  = 536870913     ← APPLICATION_ERROR + 5
Go's synthetic syscall.EBUSY   = 536870928     ← APPLICATION_ERROR + 20
Go's synthetic syscall.ETXTBSY = 536871035
Real Win32 codes are 5 / 32 / 33 -- 在完全不同的数值区间

os.Rename(file -> open directory) -> errno=5
    currentPredicate = false        ← 真实 ERROR_ACCESS_DENIED 未被识别
    proposed(5/32/33) = true
```

Go 把 `syscall.E*` 系列定义在 `APPLICATION_ERROR + n` 的合成区间（`errno` 类型的高位被污染），**与 Win32 真实错误码毫无关系**。而从操作系统传回的 errno 是裸的 5/32/33。谓词比较的是两个不同命名空间，**恒为 false**。

**跨语言对照**（证明这是 Go 独有缺陷而非设计选择）：

| 位置 | 实现 |
| :--- | :--- |
| `src/rust/src/db.rs:2652-2653` | `let raw = e.raw_os_error().unwrap_or(0); if raw == 5 \|\| raw == 32 \|\| raw == 33` ✅ |
| `src/rust/src/lock.rs:126` | `matches!(err.raw_os_error(), Some(5) \| Some(32) \| Some(33))` ✅ |
| `src/go/lock_windows.go:39-42` | Go **自己**正确地手工定义了 `errSharingViolation syscall.Errno = 32` / `errLockViolation = 33` ✅ |
| `src/go/db.go:2419-2431` | `syscall.EACCES \|\| syscall.EBUSY \|\| syscall.ETXTBSY` ❌ |

**影响与触发条件**：Windows 上任何瞬时句柄占用（杀软实时扫描、Windows Search 索引器、Explorer 预览窗格、备份工具、另一进程刚退出尚未完全释放句柄）都会让原子轮转立刻失败。后果链条：

1. `commitFlush` 第一步 `renameWithRetry(db.path, bakPath)` 失败 → 整个写返回错误（本应被 230ms 退避吸收的瞬时占用）。
2. **更严重的是第二步** `renameWithRetry(tmpPath, db.path)` 失败：此时 `.db` 已经被重命名成 `.bak`，**主库文件消失**，只剩 `.tmp` + `.bak`。虽有 §4.2 的下次打开自愈兜底，但用户视角是"库文件不见了"。

讽刺的是，因为该谓词恒假，`src/go/db.go:1693-1699` 的 `.tmp` 晋升路径（见 [P1-5](#p1-5)）在 Windows 上的触发概率被显著放大——两者叠加构成"数据静默丢失"。

**修改建议**（3 行）：

```go
if errors.As(err, &errno) {
    return errno == 5 || errno == 32 || errno == 33   // Win32 裸码，与 Rust 对齐
}
```

并补一个 Windows-only 回归测试：独占持有目标文件 → 调 `renameWithRetry` → 断言 `elapsed >= 200ms`。**当前测试套件对 `renameWithRetry` 零覆盖**。

---

<a id="p0-4"></a>
### P0-4 .NET：任一持有者 `Dispose()` 即销毁全进程共享实例（**真实线上崩溃根因**）

**位置**：`src/dotnet/src/Ckv/Db.cs:115-116`（Registry 定义）、`:502-508`（命中即返回）、`:2604-2699`（`Dispose` 就地销毁）、`:460`（`Close() => Dispose()`）

**问题**：

```csharp
// Db.cs:502-508 —— 命中注册表直接返回，完全无引用计数
if (Registry.TryGetValue(canonical, out var weakRef) && weakRef.TryGetTarget(out var existing))
{
    if (!existing._isDisposed)
    {
        CheckOptionsConflict(existing._options, opts);
        return existing;      // ← 任意模块都能拿到并销毁全局实例
    }
}
```

```csharp
// Db.cs:2604-2699 —— Dispose 就地物理销毁
public void Dispose()
{
    if (_isDisposed) return;
    _isDisposed = true;
    ...                                     // 最终刷盘
    _connectionLock?.Dispose();             // 释放跨进程锁
    _rwLock.Dispose();                      // 物理销毁读写锁
}
```

**架构根因（这是本条最重要的部分）**：

| 维度 | Rust | .NET |
| :--- | :--- | :--- |
| 单例机制 | `Arc<RwLock<DbInner>>` | `WeakReference<CkvDb>` |
| 借用者离开作用域 | `Arc::clone` 递减引用计数，**最后一个**使用者析构时才真正关闭 | `IDisposable.Dispose()` = **立即物理销毁** |
| 结果 | 天然安全 | **任意借用者 `Dispose` 即全员失效** |

`.NET` 只移植了 `WeakReference`——它解决的是"GC 可回收"，**不解决"谁有权释放"**。而 API 形状返回 `CkvDb : IDisposable`，**强烈暗示**调用方应该写 `using`。二者直接冲突，是典型的所有权陷阱（Ownership Trap）。

**加重情节（三项，均经外部反馈核实）**：

1. **仓库自己的文档与示例就在教这个错误用法**
   - `src/dotnet/README.md:45`：`using var db = CkvDb.Open("app_data.db");`
   - `docs/API-SPEC.md:87` 仅声明"返回同一实例、配置以首次打开为准"；
   - `docs/API-V1-FREEZE.md:220` 仅解释 Weak 注册表"Dispose 自动清理"。
   - **三处均未声明**"借用者不得 Dispose"。
2. **测试把错误行为固化为正常行为**
   `tests/Ckv.Tests/PathIdentityTest.cs:83-93`：先 `db2.Dispose()` 再 `db1.Dispose()` 而不报错——等价于断言"Dispose 任意一个即干掉全部"是正确语义。
3. **`Open` 方法没有任何 XML 文档注释**（`Db.cs:492` 上方），调用方在 IDE 中零提示。
4. **跨语言同构隐患**：Go 的 `openRegistry`（`db.go:179/208`）与 Dart 的 `_registry`（`db.dart:39`）同样直接返回裸单例。Kotlin 同理。

**影响与触发条件**：任何"多模块打开同一库"的场景，只要有一个模块写了 `using`（.NET 的**标准惯用法**）就会触发。已实测发生（见 [§1.1.1](#s-field-evidence)）。使用方"复用活跃上下文、禁止对活跃单例 Dispose"的修复只是**应用侧止血**——任何下一个消费者写自然的 `using var db = CkvDb.Open(...)` 都会再踩。

**修改建议（需显式决策的取舍）**：

倾向方案：**命中注册表的 `Open` 调用 +1，`Dispose` −1，归零才真销毁。**

- 优点：保住 C# `using` 惯例，改动局限在 `Registry` 与 `Dispose`，不新增公共类型。
- **必须同时决策的冲突**：`docs/API-V1-FREEZE.md:261` 承诺"`close()` 本身幂等"，而引用计数天然**不幂等**（重复 `Dispose` 会多减）。`tests/Ckv.Tests/ClosedHandleTest.cs:44` 现有幂等断言会语义变化。**两者必选其一**：
  - **选项 A**：幂等定义为"每实例一次"，重复 `Dispose` 记为编程错误（抛错或告警）；引用计数按 `Open` 次数配对。
  - **选项 B**：保留整体幂等，引用计数由独立的 `IDisposable` 包装句柄承担（即外部反馈提出的 `CkvHandle` 方案），`CkvDb.Dispose` 退化为"仅 Owner 可调"。
- **低成本先行项（可立即做，零风险止血）**：为 `CkvDb.Open` 补 XML 文档，明确「返回进程内共享实例；`Dispose` 关闭的是该共享实例，任一持有者调用即全员失效；进程退出前由创建方调用一次」，并修正 `src/dotnet/README.md:45` 的示例。

**验收标准**：新增回归用例「模块 A `Open` → 模块 B `Open` 同名路径 → B `Dispose` → A 仍可读写，且引擎真正存活」。当前无此用例。

---

<a id="p0-5"></a>
### P0-5 .NET：`Open()` 在注册表残留死弱引用时进入**死循环**（进程挂死 + 句柄泄漏）

**位置**：`src/dotnet/src/Ckv/Db.cs:500-552`

**问题**：

```csharp
500 while (true)
502      if (Registry.TryGetValue(canonical, out var weakRef) && weakRef.TryGetTarget(out var existing))
504          if (!existing._isDisposed) { ...; return existing; }
511      var db = new CkvDb(path, canonical, opts);
517      db._connectionLock = LockFile.Acquire(path, opts);
523      db.InitAndRecover();
526      if (Registry.TryAdd(canonical, newWeakRef)) { ...; return db; }     // ← 必然失败
534      if (Registry.TryGetValue(canonical, out var racingRef) && racingRef.TryGetTarget(out var rDb))
550      continue;   // ← 回到 500
```

`Registry` 是 `ConcurrentDictionary<string, WeakReference<CkvDb>>`，**全文件只有 `Dispose()` 一处会删除条目**（`Db.cs:2688-2689`）。

当 `CkvDb` 实例在未调用 `Dispose()` 的情况下被 GC 回收：

- `TryGetTarget` 返回 `false` → 第 502 行的 `if` 整块被跳过；
- `TryAdd` 因 key 已存在返回 `false`（ConcurrentDictionary 语义）→ **这是关键**；
- 第 534 行的 `TryGetTarget` 同样返回 `false` → 跳过 → `continue`。

→ 每轮迭代重新 `new CkvDb` + `LockFile.Acquire` + `InitAndRecover()`（全量读盘校验），**永不退出**。

**影响与触发条件**：只需一次"打开后忘记 Dispose"（`CkvDb.Open(p);` 不赋值、DI 容器误配置、异常路径漏掉 `using`）。之后任何再次 `Open(同一路径)` 都是**硬挂死 + 无限磁盘 IO**，每个泄漏的 `FileStream` 还随迭代无界增长。这是极易触发的路径。

**修改建议**：

```csharp
// 在任何 TryAdd 之前先驱逐死条目
if (Registry.TryGetValue(canonical, out var stale) && !stale.TryGetTarget(out _))
{
    ((ICollection<KeyValuePair<string, WeakReference<CkvDb>>>)Registry)
        .Remove(new KeyValuePair<string, WeakReference<CkvDb>>(canonical, stale));
}
```

并给外层循环加迭代次数上限断言，防止同类死循环复发。

---

<a id="p0-6"></a>
### P0-6 .NET：`Open()` 失败路径泄漏跨进程连接锁

**位置**：`src/dotnet/src/Ckv/Db.cs:511-523`、`src/dotnet/src/Ckv/LockFile.cs:86-93`

**问题**：

```csharp
511  var db = new CkvDb(path, canonical, opts);
517  db._connectionLock = LockFile.Acquire(path, opts);   // 句柄已持有
523  db.InitAndRecover();                                // ← 抛 CorruptedError
```

`InitAndRecover` 最常见的失败就是 `CorruptedError`。异常穿出 `Open`，`db` 不可达；而 `LockFile` **没有终结器**，`CkvDb` 也**没有终结器**，只有 `FileStream` 底层的 `SafeFileHandle` 会被终结线程回收——释放时机完全不确定。

`Db.cs:515-522` 那个**空的 `try { ... } catch { throw; }` 是明显的占位残留**——它本该在这里释放锁。

**影响与触发条件**：主库损坏（规范 §4.2 明确要求此时抛 `CorruptedError` 并提示运维从 `.bak` 恢复）。用户按提示"修好 `.db` 后重新打开"，却在接下来 10 秒（默认 `LockTimeoutMs`）里拿到 `LockedError` 而不是 `CorruptedError`，**错误信息完全误导**，会把排查引向错误方向。

**修改建议**：

```csharp
try { db.InitAndRecover(); }
catch { db.Dispose(); throw; }   // 释放 _connectionLock / timer / 注册表
```

同时给 `LockFile` 加终结器 `~LockFile() { Dispose(false); }`（`Snapshot` 已有终结器说明作者意识到过这个问题，只是同样的防护没加到 `LockFile` 上）。

---

<a id="p0-7"></a>
### P0-7 Dart：`SnapshotAtomic + Immediate` 提交顺序导致内存/磁盘撕裂，`incrementInt64` 持久性双倍累加

**位置**：`src/dart/lib/src/db.dart:651-654`

**问题**：

```dart
} else if (_options.syncMode == SyncMode.immediate) {
    final diskMaxTxId = _precheckDiskState();
    applyMem();                    // 内存已改
    _commitFlush(diskMaxTxId);     // 失败 → 异常抛给调用方，但内存不回滚
}
```

`_commitFlush` 内的 `raf.writeFromSync` / `replaceFileAtomicallyWithRetry` 抛错时**没有任何回滚**，且该分支**从不设置 `_isDirty`**（`_isDirty = false` 在 `_commitFlush:428` 无条件执行），所以 `close():1268` 的兜底刷盘也不会触发。

**实测后果**（Dart 审查探针，用目录占位 `<path>.tmp` 制造提交失败）：

```
putString 抛出 PathAccessException
内存中存在 "lost"? true      count = 2
close() 后重开: count=1  keys=[before]     ← "lost" 永久消失

incrementInt64 场景：
首次 incrementInt64(5) 抛异常，内存 counter=5
调用方重试 incrementInt64(5) -> 返回 10
重开磁盘: counter = 10                       ← 增量被计入两次且已落盘
```

**触发条件**：`SnapshotAtomic + Immediate` 是**默认配置**，覆盖所有 Flutter/桌面默认用户。触发条件为任何落盘失败——磁盘满、`.tmp` 路径被占用、杀软锁文件、只读介质、目录权限变更。

**规范对照**：同一文件 `db.dart:640-647` 的注释明确写明 AOL 分支"先 append 后改内存（durability first）"是正确的 C1/C2 顺序，而 snapshot 分支**恰恰相反**——同一文件内两条分支采用了相互矛盾的失败语义。

**注意**：同一缺陷在 **.NET（`Db.cs:1786-1791`）与 Kotlin（`CkvDb.kt:422-432`）也存在**，只是 Dart 的实测证明其后果最严重。这三家的 `PrecheckDiskState` XML 文档都声称"抛出异常时尚未触碰任何内存状态，因此调用方看到的错误与内存状态永远不会分叉"——**这个文档承诺是错的**。

**修改建议**：snapshot 分支也改为"先算 `newMaxTxId` → 序列化并落盘成功 → 再 `applyMem()`"；或退一步——把 `isDirty = true` 提到 `applyMem()` 之前、提交成功之后再置 false，使 `close()` 能补一次 flush。异常类型须包成 `IoError`（见 [P1-8](#p1-8)）。

---

<a id="s-p1"></a>
## 四、P1 严重缺陷

> **合并说明**：本节已并入外部反馈（`temp/ckv-issues-report/`）发现的问题。新增条目标注为「**新增·外馈**」，原有的纯代码审查条目保持不变。

<a id="s-p1-rust"></a>
### 4.1 Rust 参考实现

<a id="p1-1"></a>
#### P1-1 存活快照使每次写退化为 O(N) 全量拷贝（Rust/.NET/Go/Kotlin/Dart 全语言）

**位置**：`src/rust/src/db.rs:177-187`（`mem_put` → `Arc::make_mut`）

**实测**（探针 4）：

```
single write, no live snapshot = 5.73ms
single write, with 1 live snapshot = 7.05ms   (+23%)
```

`mem_put` 使用 `Arc::make_mut(ns_arc)`。只要 `Arc` 的引用计数 > 1（存在存活快照），`make_mut` 就会**深拷贝整个命名空间的 BTreeMap**。这不是"偶尔一次"的 CoW，而是**该命名空间的每一次写**。

各语言的等价实现与后果：

| 语言 | 机制 | 退化行为 |
| :--- | :--- | :--- |
| Rust | `Arc::make_mut` | 整个 `NamespaceMap` BTreeMap 深拷贝 |
| .NET | `_snapshotCount > 0` → `store.Clone()` | 整个 `Items` 列表 `AddRange` |
| Go | `snapshotCount > 0` → `ns.clone()` | 整个 `[]keyItem` 切片拷贝 |
| Kotlin | `v.clone()` | 整个 key+value 字节深拷贝（最重） |
| Dart | `_snapshotCount > 0` → `NamespaceStore(List.from(items))` | 整个列表拷贝 |

**影响**：README §6 把"快照隔离"列为六大核心能力之一，并明确宣传「主库高频并发写入与快照长耗时范围扫描**完全互不阻塞**」。这句话对**读**成立，但对**写**是误导——存活快照会**显著拖慢写**，且这个代价用户完全看不到、也没有任何文档提示。

**更糟的复合效应**（.NET 独有，`Db.cs:2123-2139`）：`GetOrCreateStoreForWrite` 的克隆条件只有 `_snapshotCount > 0`，**没有"本写事务内已克隆过"的标记**。因此：

```csharp
Batch(b => { for (i=0;i<50000;i++) b.PutString(ns,"k"+i,"v"); })  // 期间有任意存活快照
```

提交时 `applyMem` 循环调用 50,000 次 `GetOrCreateStoreForWrite`，**每次都 `AddRange` 复制整个 Items 列表** → 总计约 12.5 亿次引用拷贝。这是"有快照就慢几个数量级"的悬崖式退化。

**修改建议**：
1. Rust 侧：为快照引入按命名空间粒度的读拷贝（如改用持久化树 `im::TreeMap`），避免整表克隆。
2. .NET / Go / Kotlin / Dart 侧：在写事务开始时建立"本次已克隆命名空间"集合，同一命名空间在一个事务内只克隆一次。
3. 更彻底的方案：把 CoW 判定从"全局快照计数"改为"该快照是否持有此命名空间"，用 `Set<Snapshot>` 精确跟踪。

<a id="p1-2"></a>
#### P1-2 `scan_range` / `iter_prefix` 全量物化且在持锁期间深拷贝全库数据（全语言）

**位置**：`src/rust/src/db.rs:1243-1308`、`src/rust/src/db.rs:1478-1524`

**实测**（探针 5）：

```
scan_range(all) -> 3000 entries, 1,500,000 B of cloned values, 449µs
```

`scan_range` 在持有 `inner.read()` 期间遍历整个命名空间 map，为**每一项**克隆 `val.clone()`，构造 `Vec<Entry>`。`iter_prefix` 同样先全量物化，然后 `RangeIter::next()` 又对每项做 `self.entries[self.pos].clone()`——**同一份数据被拷贝两次**。

`.NET` 返回 `List<Entry>`、`Go`/`Kotlin`/`Dart` 同样是缓冲式列表，全部在持锁/单线程关键区内完成。

**影响**：DESIGN-SPEC §2 给 `AppendOnlyLog` 的定位是"1MB – 100MB"。对 50MB 的库，一次全量 `scan_range` 即分配 50MB 新内存、耗时数百毫秒，且**全程持有读锁 → 写者完全阻塞**。

**注意（合并自外馈 #6 的辨析）**：API-SPEC §3.3.3 明确写「缓冲式前缀扫描语义：内部一次性收集全部匹配条目后返回快照列表，避免泄露内部锁或导致并发迭代器悬垂指针」——**规范强制要求了这个设计**。因此这是**规范本身的问题**，不能简单改成流式迭代器（那会破坏跨语言语义一致性并重新引入悬垂/持锁问题）。正解是补 `limit` / 反向 / 惰性变体（见 [§7.1](#s-api-matrix)），而非替换现有语义。

**修改建议**：
1. 规范层面：为 §3.3.3 增加"缓冲式家族 + 惰性变体"的并行约定。
2. 实现层面：`RangeIter` 改为持有 `(Arc<NamespaceMap>, 迭代位置)` 而非 `Vec<Entry>`，只克隆 key 不克隆 value。
3. 至少：把物化动作移出锁（锁内收集 `Arc` 引用，锁外再深拷贝）。

<a id="p1-3"></a>
#### P1-3 加载期不校验 `NsLen ≤ 255`，产生不可读且不可删除的孤儿命名空间（Rust/.NET/Go/Kotlin/Dart）

**位置**：`src/rust/src/format.rs:80`（`ns_len` 解码无上限检查）、`src/rust/src/db.rs:2423-2445`（`apply_entries` 不过滤）

**实测**（探针 6，独立复核）：

```
open OK; namespaces()[0].len() = Some(300)
value readable  = Err(InvalidNamespace("Namespace byte length 300 exceeds maximum allowed 255"))
re-write via API = Err(InvalidNamespace(...))
```

`RawOpEntry::decode` 从磁盘读出 `ns_len`（`u16`，可达 65535）后**不做任何上限校验**就存入内存。`normalize_namespace` 的 255 字节检查**只在 API 入口生效**，加载路径完全绕过。

**后果链条**：

1. 该命名空间常驻内存，出现在 `namespaces()` 结果中；
2. `get_*` / `put_*` / `delete` / **`drop_namespace`** / **`clear_namespace`** 全部因 `InvalidNamespaceError` 被拒——**它既读不出，也删不掉**；
3. 每次快照会把它**重新序列化**并写入 `.db` / `.bak` / 所有 `backup()` 产物；
4. 若其它语言在加载期校验该上限，则该文件对它们**直接不可打开**——**一个语言写出的文件可能让另一语言拒绝打开**。

**规范依据**：DESIGN-SPEC §3.2.2「硬上限 `0 <= NsLen <= 255`」。

**修改建议**：
1. 在所有语言的条目解码处加入 `if (ns_len > 255) throw CorruptedError(...)`。3 行改动，把"静默产生非法分区"降级为"明确拒绝打开"。
2. 补充 `apply_entries` 的防御：加载后校验每个命名空间长度，超限即报 `CorruptedError`。
3. 在 Dart 侧还须同时修 P0-1（写入侧），否则只是把"写坏"变成"打不开"。

<a id="p1-4"></a>
#### P1-4 锁模型：C9 内核锁决策未回写规范，`forceUnlock` 与 32 字节锁协议全面失账

**位置**：`src/rust/src/lock.rs`（内核锁）、`docs/KV-DB-DESIGN-SPEC.md` §4.1.2（32 字节 `PID+UUID+CreateTimeMs` + 防 ABA + 窃取协议）

**问题**：5 语言实际全部改用**内核建议锁**（Rust `File::try_lock`、.NET `FileShare.None`/`flock`、Go `LOCK_EX|LOCK_NB`、Kotlin `FileLock`、Dart `raf.lockSync`），锁文件**从不删除**、**从不写入 PID/UUID/时间戳**、**没有防 ABA 比对**、**没有窃取协议**。而 `docs/KV-DB-DESIGN-SPEC.md` §4.1.2 仍把这套协议写为「硬性锁定」，§5.1.1 的 `DbOptions` 字段表仍要求 `forceUnlock`（5 语言全部缺失）。

**这个架构变更是正确的**——`lock.rs:14-28` 的论证完全成立：时钟判活会误杀活着的持有者（长 GC、换页、大快照都足够），崩溃进程残留锁文件也是经典问题，而内核锁由 OS 在进程退出时释放。bbolt 与 LMDB 正是这么做的。**问题不在决策本身，在于决策没有留下任何账务。**

**实际后果（Windows 只读路径，跨语言不兼容）**：

| 持有者 | Windows 打开参数 |
| :--- | :--- |
| .NET 只读（`LockFile.cs:54-55`） | `GENERIC_READ` + `share = FILE_SHARE_READ` |
| Go 只读（`lock_windows.go:14-15`） | `GENERIC_READ` + `share = FILE_SHARE_READ` |
| **Rust 只读（`lock.rs:59-65`）** | **`GENERIC_READ \| GENERIC_WRITE`（无条件）** + `share = R\|W\|D` |

Windows 共享检查是**双向对称**的。.NET 只读句柄的 `share = FILE_SHARE_READ` **不允许** Rust 句柄请求的 `GENERIC_WRITE` → `CreateFile` 抛 `ERROR_SHARING_VIOLATION` → .NET 退避 10 秒 → `LockedError`。

**即：Windows 上一个 Rust 只读连接与一个 .NET 只读连接会互相排斥**，而本应共存。责任在 Rust（`lock.rs:60-61` 应按 `exclusive` 条件化 `write(exclusive)`）。可写路径不受影响（正确拒绝）。Unix 上三语言都用 `flock(LOCK_SH)`，兼容 ✅

**修改建议**：
1. 修 Rust `lock.rs:59-65`：`read(true).write(exclusive)`。
2. 新增 ADR 记录 C9 决策，并**同步修订 DESIGN-SPEC §4.1.2 与 §5.1.1**，把 32 字节协议标注为"已废弃"或直接删除。
3. 明确 `forceUnlock` 的去留：内核锁下它是 no-op，应从 `DbOptions` 字段表中删除并说明理由。
4. 五语言退避曲线统一（现为 Rust `5→10→20→40→80→100ms`、.NET 固定 10ms、Go 指数、Kotlin `5,10,20,...`、Dart 固定 50ms）。

<a id="p1-5"></a>
#### P1-5 Rust：`clone()` + `close()` 对共享实例全局生效

**位置**：`src/rust/src/db.rs:288-291`（`Db` 是 `Clone`）、`src/rust/src/db.rs:559-630`（`close`）

**实测**（探针 2）：

```
write via the surviving clone after the other called close():
Err(Locked("Database is closed: writes are refused after close()"))
```

`Db` 是 `#[derive(Clone)]`，`inner: Arc<RwLock<DbInner>>`。任一克隆体调用 `close()` 都会设置共享 `DbInner.closed = true`、释放跨进程锁、并从全局注册表注销。

```rust
let a = Db::open(&path, None)?;
let b = a.clone();          // 进程内单例，同一个 Arc
a.close()?;                 // b 也死了
b.put_string("", "k", "v")?;  // Err: Database is closed
```

**影响**：这是**静默的、符合类型系统但违反直觉**的语义。`Db: Clone` 在 Rust 中通常意味着"廉价复制一个句柄"，用户不会预期 `close()` 有全局副作用。API-SPEC §2 表格中 Rust 一列写的是「`db.close()` / `drop(db)`」，未提示这一全局性。

**注意**：这是 P0-4 的 Rust 版本。Rust 因 `Arc` 自带引用计数，`drop` 是安全的（最后一个使用者析构才真正关闭）；**危险的是 `close()` 这个显式方法**，它绕过了引用计数语义。

**修改建议**：
1. 文档层面：在 `Db::close()` 的 rustdoc 中**显式警告**「本方法作用于共享实例；任一克隆体调用都会关闭全部克隆体并释放跨进程锁」。
2. API 层面：考虑把 `close()` 改为消费 `self`（`pub fn close(self)`），让语义在类型上可见。

<a id="p1-6"></a>
#### P1-6 Rust：`apply_mutation` 的文档承诺与实际行为不符（提交失败后内存已改）

**位置**：`src/rust/src/db.rs:2384-2415`、`src/rust/src/db.rs:2447-2457`

**问题**。`precheck_disk_state` 的文档注释写道：

> 「**Phase 1 of the write path**: every validation that can reject a write runs here, **before** any in-memory mutation... If this returns `Err`, nothing has been mutated, so the in-memory state can never diverge from the error the caller sees.」

但 `apply_mutation` 的 `SnapshotAtomic + Immediate` 分支是：

```rust
let disk_max_tx_id = self.precheck_disk_state()?;   // 预检
apply_mem(self);                                     // ← 内存已改
self.commit_flush(disk_max_tx_id)?;                  // ← 可能失败
```

`commit_flush` 会在 `serialize_snapshot_to_tmp`（打开 `.tmp`、写、`fsync`）或 `rename_with_retry`（两次轮转，各 5 次退避）失败。此时调用方收到 `Err`，但**内存状态已被修改且从未回滚**。

**影响**：调用方看到错误后重试，`increment_i64` 会**双倍累加**（Dart 侧已实测复现此场景，见 [P0-7](#p0-7)）。

**修改建议**：修正文档承诺（说明 `Immediate` 模式下"落盘失败时内存可能已改"），并让 `commit_flush` 失败路径**保留脏标记**以便 `close()` 补刷。.NET（`Db.cs:1786-1791`）与 Kotlin（`CkvDb.kt:422-432`）存在同一问题，且各自文档注释做出了相同的错误承诺。

<a id="p1-7"></a>
#### P1-7 Rust：`precheck_disk_state` 的同尺寸短路削弱了并发修改检测

**位置**：`src/rust/src/db.rs:2471-2497`

```rust
Ok(meta) => {
    let size = meta.len();
    if self.last_known_disk_state != (0, 0) && size == self.last_known_disk_state.1 {
        (self.last_known_disk_state.0, size)          // ← 只比大小，跳过帧扫描
    } else if size >= FILE_HEADER_LEN as u64 {
        let mut f = File::open(&self.path)?;
        let max_tx = FrameLoader::scan_max_tx_id(&mut f, size)?;
        (max_tx, size)
    } else { (0, size) }
}
```

DESIGN-SPEC §4.1.3 要求比对 `(MaxTxID, FileSize)` **二元组**。但 RC2 性能优化引入了"文件大小相同就跳过 TxID 扫描"的短路。由于 `commit_flush` 每次写完都会把 `last_known_disk_state` 更新为**当前**大小，此后每次写的 `size` 恒等于 `last_known_size`——**这条短路每写必中，TxID 扫描实际永不执行**。

外部进程用**同尺寸**的不同内容替换 `.db`（从副本恢复、`git checkout` 等长版本、另一语言的 `compact()` 产生等长结果）**不会被检出**，`ConcurrentModificationError` 永不触发。.NET（`Db.cs:1048-1051`）有完全相同的问题。

**修改建议**：把文件 `mtime` 纳入比对三元组 `(TxID, FileSize, mtime)`；或在 RC2 优化说明中明确记录这一取舍，并降级文档承诺。

<a id="p1-8"></a>
#### P1-8 Rust：`scan_max_tx_id` 不校验 CRC 与单调性，且不检查 `payload_len` 是否越界

**位置**：`src/rust/src/format.rs:496-525`

`scan_max_tx_id` 只读 `magic` 与 `payload_len`，**不校验帧头 CRC、不校验 TxID 单调性、不检查 `offset + 28 + payload_len` 是否越过文件尾**。它被 `precheck_disk_state` 用于判断磁盘是否被外部修改。Go（`format.go:259-288`）、.NET（`Db.cs:1060-1079`）、Dart（`format.dart:140-163`）有同类问题。

**影响**：损坏的 `payload_len` 会让 `offset` 跳到错误位置，可能**跳过**后续合法帧并返回一个偏小的 TxID，使比对失败被误报为 `ConcurrentModificationError`（Go 审查已实测到此错误消息：`expected (1, 75), found (0, 139)`，真实原因是文件被追加了 64 个零字节）。

**修改建议**：加 `if (offset + FRAME_HEADER_LEN + payload_len > file_size) { return Err(Corrupted) }` 与帧头 CRC 校验；并把"扫描失败"与"状态不匹配"区分成两种错误类型。

<a id="p1-9"></a>
#### P1-9 Rust：`.stage` 临时文件永不清理

**位置**：`src/rust/src/db.rs:2668`、`.NET Db.cs:1402`、Kotlin `FileRotation.kt:58/99`、Dart `db.dart:1360`

`rename_with_retry` 在 5 次退避耗尽后的兜底路径会创建 `<dst>.stage`。注释明确说"rename 失败时 `.stage` 会保留下来供人工恢复"——但 `init_and_recover` 的启动清理**只处理 `.tmp`，从不清理 `.stage`**。

**影响**：反复触发兜底会在库目录里无界堆积 `.db.stage` / `.db.bak.stage` 文件。Go 没有 `.stage` 概念（跨语言不一致）。

**修改建议**：在 `init_and_recover` 中清理同目录的陈旧 `*.stage`（或至少在成功路径清理）。

<a id="p1-10"></a>
#### P1-10 Rust：`Db::backup` 未校验目标路径，且不走 `flush_mutex`

**位置**：`src/rust/src/db.rs:1131-1188`

- 没有 `dest == self.path` 的相等性检查。`db.backup(db_path)` 时 `dest_tmp == self.path + ".tmp"`，与后台刷盘**争抢同一个 `.tmp`**（Go 侧同类问题见 `db.go:975-1035`，同样不取 `flushMu`）。
- `backup` 也不检查 `self.closed`，已关闭的实例仍可备份（语义模糊）。
- 备份文件的 TxID = `current_max_tx_id + 1`，与随后主库快照的 TxID **重复**（Dart 侧实测确认）。单文件内单调性成立，但对账工具需知晓。

**修改建议**：加 `if same_file(dest, self.path) { return Err(...) }`；让 `backup` 走 `flush_mutex` 或使用独立的临时名 `dest + ".bkuptmp"`。

<a id="s-p1-dotnet"></a>
### 4.2 .NET 独有

<a id="p1-11"></a>
#### P1-11 .NET：`Dispose()` 与并发写之间的窗口——锁释放后仍可继续落盘

**位置**：`src/dotnet/src/Ckv/Db.cs:1762-1766` / `2144-2145` / `2679`

```csharp
1762  private void ThrowIfDisposed() { if (_isDisposed) throw new LockedError(...); }
2144  ThrowIfDisposed();                    // ← 锁外检查
2145  _rwLock.EnterWriteLock();
```

`Dispose()` 的顺序是：`_isDisposed = true` → 最终刷盘 → `_rwLock.ExitWriteLock()` → **释放跨进程连接锁（`:2679`）** → `_rwLock.Dispose()`（`:2694`）。

写线程时序：
1. `ThrowIfDisposed()` 通过；
2. 切换到别的线程执行完 `Dispose()`，跨进程锁已释放、`_rwLock` 已 Dispose；
3. 写线程 `EnterWriteLock()` → 要么抛 `ObjectDisposedException`，要么（若恰好在 Dispose 前拿到写锁）**在没有任何跨进程锁保护的情况下把数据写到磁盘**。

第 3 种情况是真实的数据损坏路径：另一个进程此时完全可以合法打开同一个库。

**修改建议**：把"是否已关闭"的判断移进写锁内部；引入 `_disposing` 状态 + 原子状态机（`Open → Disposing → Closed`）。

<a id="p1-12"></a>
#### P1-12 .NET：**新增·外馈** 关闭后读路径未硬化，与 `Snapshot` 的处理方式完全相反

**位置**：`src/dotnet/src/Ckv/Db.cs:1808-2107`（全部读方法）、`:2694`（`_rwLock.Dispose()`）、`:3147-3156`（`Snapshot.ThrowIfDisposed`）

**规范原文冲突**（同一份规范的两处"关闭后读语义"）：

- `docs/API-V1-FREEZE.md:261`（FIX-22）：「`close()` / `Dispose()` 之后，`put` / `delete` / `clear` / `flush` / `compact` 必须失败并返回 `Locked`……**读操作不强制失败，但只反映关闭前的内存快照，不再与磁盘一致**」。
- `docs/API-V1-FREEZE.md:186-196`（§3.8，针对 `Snapshot`）：「关闭后读返回空/null，**不抛错**」。

**代码事实**：

- `CkvDb` 的读方法（`GetBytes`、`GetString`、`ScanRange`、`IterPrefix`、`Keys`、`ContainsKey`…）**均不做 `ThrowIfDisposed`**，直接进 `_rwLock.EnterReadLock()`；而 `Dispose` 已执行 `_rwLock.Dispose()`。`EnterReadLock()` 在已释放实例上必然抛 `ObjectDisposedException`——**正是外部反馈日志中的那个异常**。
- 所以「读只反映关闭前内存快照」这一承诺在 `CkvDb` 上**根本无法实现**（锁已销毁）。
- 反观 `Snapshot` 已按 §3.8 统一为"关闭后读返回空/null"：`Db.cs:3147-3156` 的 `ThrowIfDisposed` 是**刻意的空实现**，配合 `Stores => _namespaces ?? EmptyStores`（`Db.cs:2940-2944`）兜底。

**同一份规范的两处语义，两个类型采用了相反的治疗方式。**

**测试盲区**：`tests/Ckv.Tests/ClosedHandleTest.cs:36-39` 只断言关闭后**写**报 `LockedError`，**无任何读路径断言**——该不一致从未被测试覆盖。

**附带问题**：`ThrowIfDisposed` 的消息固定为 `"writes are refused after Dispose()"`，对读路径同样误导。

**修改建议**（二选一，**必须同步文档**）：

- **(a) `CkvDb` 读路径也硬化**：关闭后返回空/null（与 `Snapshot` 一致，对"误查询"更温和）。注意 `_rwLock` 已销毁，实现时须在取锁前用 `_isDisposed` 早退。
- **(b) 文档改为**「关闭后读抛 `ObjectDisposedException`」，并显式说明这是 `Snapshot` 的有意差异。

无论选哪个，都需新增读路径关闭后行为的测试用例。

<a id="p1-13"></a>
#### P1-13 .NET：`Dispose()` 在并发读存在时销毁 `ReaderWriterLockSlim`

**位置**：`src/dotnet/src/Ckv/Db.cs:2692-2698`

```csharp
try { _rwLock.Dispose(); }
catch { }        // 吞掉一切
```

`ReaderWriterLockSlim.Dispose()` 在其它线程正持有或正尝试获取锁时调用是**未定义行为**（官方文档明确禁止）。异常被 `catch { }` 静默吞掉，症状会表现为远处的随机 `ObjectDisposedException` 或锁状态损坏。

**修改建议**：**直接删掉 `Db.cs:2692-2698`**。一个 RWLS 的内存开销可忽略，且它没有非托管资源。若必须销毁，需引入读写者计数 + `ManualResetEventSlim` 做引用门控。

<a id="p1-14"></a>
#### P1-14 .NET：`AtomicReplaceWithRetry` 只捕获 `IOException`，规范要求的 WinError 5 不会被重试

**位置**：`src/dotnet/src/Ckv/Db.cs:1385-1400`

```csharp
int[] backoffs = { 5, 15, 30, 60, 120 };
...
catch (IOException)     // ← UnauthorizedAccessException 逃逸
```

DESIGN-SPEC §4.2 明确要求「`rename` 遇到 `WinError 5` 或 `WinError 32` 时，按 `[5ms, 15ms, 30ms, 60ms, 120ms]` 重试 5 次」。而 .NET 的错误映射是：`ERROR_SHARING_VIOLATION(32)` → `IOException`；**`ERROR_ACCESS_DENIED(5)` → `UnauthorizedAccessException`**。因此 WinError 5 跳过整个退避循环，直接落到 `:1402` 的 `File.Copy(src, stage, true)` 降级路径——**先截断目标再拷贝，非原子**。

**影响**：杀软/索引服务/备份软件的 ACL 拦截是 Windows 桌面端常态。每次写都会走一次 O(库大小) 的非原子拷贝，而该拷贝窗口内崩溃就会留下半成品 `.db`——正是 FIX-03 想消灭的窗口。

**修改建议**：

```csharp
catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
{
    if (attempt == backoffs.Length - 1) break;
    Thread.Sleep(backoffs[attempt]);
}
```

（Go 的同类问题更严重，见 [P0-3](#p0-3)。）

<a id="p1-15"></a>
#### P1-15 .NET：**新增·外馈** 二次打开只比较 3/10 个配置字段，`ReadOnly` 请求静默拿到读写实例

**位置**：`src/dotnet/src/Ckv/Db.cs:463-490`（`CheckOptionsConflict`）、`src/dotnet/src/Ckv/Options.cs:10-53`

**问题**：`CheckOptionsConflict` **只比较 3 个字段**：`ReadOnly`（`:466`）、`EngineMode`（`:470`）、`SyncMode`（`:474`）。而 `DbOptions` 共 **10 个字段**，以下冲突**完全不检查**：

`SyncOnWrite`、`SyncIntervalMs`、`CompactionThresholdBytes`、`AutoRecoverFromBackup`、`StrictCompaction`、`LockTimeoutMs`。

**最危险的是被检查的那一个**：调用方以 `ReadOnly = true` 意图打开，命中单例后只发一条 `OnWarning` 就**返回原有的读写实例**——调用方以为拿到了严格只读连接，实际具有完整写权限。且 `OnWarning` 默认为 `null`，**默认配置下冲突完全不可见**。

Rust（`db.rs:308-339`）比较 3 个字段，Kotlin（`CkvDb.kt:1243-1262`）比较 3 个字段，Go（`db.go:252`）**完全不比较**（零值还会触发 P2-26 的锁序漏洞）。五语言一致存在这个问题。

**修改建议**：

1. 默认行为改为抛 `ConflictingOptionsError`（或 `ArgumentException`），让冲突显式失败；
2. 若需保留宽松模式，提供显式开关 `DbOptions.OnOptionsConflict = WarnAndReuse / Throw`，**默认 `Throw`**；
3. 最低要求：**`ReadOnly` 冲突不得静默返回权限更高的实例**（这是安全问题）；`SyncMode`/`EngineMode` 冲突可维持告警（性能/持久性语义差异，风险低）。
4. 补全字段比对（全部 10 项），或改为"调用方显式声明可共享"的白名单机制。

<a id="p1-16"></a>
#### P1-16 .NET：`netstandard2.0` 构建的原子轮转依赖 `File.Replace`，在 Unix 上不受支持

**位置**：`src/dotnet/src/Ckv/Db.cs:1293-1307`、`src/dotnet/src/Ckv/Ckv.csproj:4`

```csharp
#if NETCOREAPP3_0_OR_GREATER
    File.Move(src, dst, overwrite: true);
#else
    try { File.Replace(src, dst, null); }
#endif
```

`netstandard2.0` 是多目标之一，实际会被 .NET Core 3.0+ / .NET 5/6 运行时加载。`File.Replace` 在 Unix 上的实现是 .NET 7 才补齐的；在 .NET Core 3.1 / .NET 5 / .NET 6 运行时上会抛 `PlatformNotSupportedException`——而它**不被** `AtomicReplaceWithRetry` 的 `catch (IOException)` 捕获，直接冒泡。

**影响**：任何 Unity / Xamarin / .NET MAUI / .NET Framework 消费者跑在 Linux 或 macOS 上时，**每一次写入的轮转步骤都会失败**。

**修改建议**：**建议直接砍掉 `netstandard2.0` 目标**，改为 `net8.0`——它在三处都造成语义分叉（目录 fsync 静默 no-op、路径规范化退化、轮转原语不可用），而 `Ckv.Tests.csproj` 只测 `net8.0`，`netstandard2.0` 分支**零测试覆盖**。若必须保留，至少在 `#else` 分支加运行时平台判断 + 一次性告警。

<a id="p1-17"></a>
#### P1-17 .NET：`Snapshot.Dispose()` 的 `_isDisposed` 非原子，双重释放会让 CoW 计数归零

**位置**：`src/dotnet/src/Ckv/Db.cs:3173-3180` + `:2112-2121`

```csharp
private void Release()
{
    if (_isDisposed) return;          // ← 非原子 check-then-set
    _isDisposed = true;
    _owner?.ReleaseSnapshot();        // → _snapshotCount--
    _namespaces = null;
}
```

**两个线程并发调用 `Dispose()`** 会双双通过检查，导致 `ReleaseSnapshot()` 被调用两次 → `_snapshotCount` 提前归零 → 后续写路径的 `if (_snapshotCount > 0)` 全为假 → **原地修改 `NamespaceStore.Items`** → 那个仍然存活的 `Snapshot` 正在读的列表被就地改写 → **快照隔离被打破，返回撕裂/错误的数据**。

这直接违反 API-SPEC §3.5.2「绝对无锁与高韧性」的承诺。

**修改建议**：

```csharp
private int _released;
private void Release()
{
    if (Interlocked.Exchange(ref _released, 1) != 0) return;
    _owner?.ReleaseSnapshot();
    _namespaces = null;
}
```

<a id="p1-18"></a>
#### P1-18 .NET：**新增·外馈** 无终结器 / SafeHandle 安全网，忘记 `Dispose` 的后果是多重的

**位置**：`src/dotnet/src/Ckv/Db.cs:2604`（`CkvDb` 无终结器）、`src/dotnet/src/Ckv/LockFile.cs:86-93`（`LockFile` 无终结器）

调用方忘记 `Dispose` 直接丢弃引用时，三件事同时发生：

1. `Deferred`/`Manual` 模式下的脏数据**静默丢失**（无任何告警）；
2. 跨进程连接锁（`LockFile` 持有 `FileStream`）要等 GC 才释放，**窗口期内其他进程 `Open` 全部超时**（默认 10s）；
3. `Registry` 里的死弱引用条目**无人清理**（`Dispose` 中的按键+按值移除不会执行）→ 字典随时间缓慢膨胀，且会触发 [P0-5](#p0-5) 的死循环。

**修改建议**：

1. `CkvDb` 加终结器：检测 `_isDirty && !_options.ReadOnly` 时**至少**通过 `OnWarning` 发一条 `"instance was garbage-collected without Dispose; pending writes lost"` 告警（终结器内不做任何文件操作）；Debug 构建可 `Debug.Fail`。
2. `LockFile` 的 `FileStream` 改用 `SafeFileHandle` 包装，保证即使漏 `Dispose` 也能在 GC 期可靠释放内核锁；或直接给 `LockFile` 加终结器 `~LockFile() { Dispose(false); }`。
3. `Registry` 在 `TryAdd` 成功时清扫陈旧条目（遍历删除 `TryGetTarget == false` 的项）。

**注意**：`Snapshot` 已有终结器（`Db.cs:3168`，FIX-05），说明作者意识到过这个问题，只是同样的防护没加到 `CkvDb` 与 `LockFile` 上。

<a id="s-p1-go"></a>
### 4.3 Go 独有

<a id="p1-19"></a>
#### P1-19 Go：`.tmp` 自愈晋升失败被吞掉，`Open` 静默成功为空库

**位置**：`src/go/db.go:1693-1699`

```go
if _, err := os.Stat(tmpPath); err == nil {
    if _, dbErr := os.Stat(db.path); os.IsNotExist(dbErr) && verifyFile(tmpPath) == nil {
        _ = renameWithRetry(tmpPath, db.path)          // ← 错误被丢弃
    } else {
        _ = os.Remove(tmpPath)
    }
}
```

`renameWithRetry` 失败后没有 `return`，流程继续：主库仍不存在 → `.bak` 也不存在 → 落到「New database：`currentMaxTxID = 0`，`return nil`」。

**`Open` 返回一个成功的空数据库，而唯一完好的数据就在磁盘上的 `.tmp` 里。**

这正是该函数注释里反复强调要根除的那一类"静默数据丢失"（`db.go:1756-1758`：「the fourth silent data loss after C1-C3」）。讽刺的是因为 [P0-3](#p0-3)，`renameWithRetry` 在 Windows 上对任何瞬时占用都会失败，这条路径的触发概率被显著放大。

**修改建议**：

```go
if err := renameWithRetry(tmpPath, db.path); err != nil {
    return newDbError("IoError", "failed to promote validated .tmp to primary", err)
}
```

晋升失败必须让 `Open` 失败，绝不能退化成空库。Kotlin（`CkvDb.kt:138-142`）有**完全相同**的问题：`File.renameTo` 的返回值被丢弃，随后 `applyParsed` 照常执行并置 `dbExists = true`。

<a id="p1-20"></a>
#### P1-20 Go：`PutBytes` 不做防御性拷贝，击穿快照隔离

**位置**：`src/go/db.go:514-537` → `db.go:1653-1666` → `db.go:55-68`

`putHelper` 把调用方切片直接存入 store，`namespaceStore.clone()` 只拷贝切片头数组，**底层字节数组仍与主库共享**。

**Go 审查实测**：

```
db.GetBytes after caller mutation   = "MUTATED!"
snapshot.GetBytes (same buffer)     = "MUTATED!"
```

**这是 5 语言的共性问题**（Rust 因 `Vec` 的 move 语义天然免疫）：

| 语言 | 写侧 | 读侧 | 后果 |
| :--- | :--- | :--- | :--- |
| Rust | `val.as_ref().to_vec()` ✅ | `val.clone()` | 安全 |
| .NET | 直接存 `val` ❌ | `copyVal` ✅ | 不对称 |
| Go | 直接存 `val` ❌ | 拷贝 ✅ | 不对称 |
| Kotlin | 直接存 `value` ❌ | `copyOf()` ✅ | 不对称 |
| Dart | 直接存 `val` ❌ | `fromList` ✅ | 不对称 |

**影响**（Kotlin/Dart 审查均已实测复现）：用户复用缓冲区（图像解码、mmap、`buf.Bytes()` 复用、Flutter 的 scratch buffer）会导致**库内数据、已落盘快照、backup 产物同时被静默篡改**。`put_int64`/`put_float64`/`put_bool` 因内部自己编码新数组而安全，**只有 `put_bytes` 受影响**——恰是二进制场景的主力 API。

**修改建议**：在 `put_bytes` 入口统一 `bytes.Clone(val)`（Go 1.20+，且 `go.mod` 声明 1.21）。同时在 API 文档中明确"数组所有权转移给库"或"库会拷贝"——**当前 5 语言的文档都没有说明这一点**。

<a id="p1-21"></a>
#### P1-21 Go：`IncrementInt64` 失败后残留幽灵命名空间

**位置**：`src/go/db.go:679-684` → `db.go:709-714`

**实测**（Go 审查探针，Immediate 模式 + 外部篡改触发预检失败）：

```
IncrementInt64 -> ConcurrentModificationError: expected (1, 75), found (0, 139)
namespaces before: [""]
namespaces after:  ["" "counters"]     Count("counters") = 0
```

建 store 在前（`db.go:682`），失败返回在后（`db.go:712`），**没有任何回滚**。`precheckDiskState` 失败、AOL `appendEntries` 失败、`commitFlush` 失败，都会留下一个空命名空间。API-SPEC §3.4 要求"全成或全败"。

**修改建议**：把建 store 挪进 `applyMutation` 的 `applyMem` 回调，或在错误分支删除。

<a id="s-p1-dart"></a>
### 4.4 Dart 独有

<a id="p1-22"></a>
#### P1-22 Dart：`IoError` 与 `InvalidNamespaceError` 两个必需异常类不存在

**位置**：`src/dart/lib/src/errors.dart:1-51`

`errors.dart` 定义了 `DbError` + 7 个派生类，缺 `IoError` 与 `InvalidNamespaceError`（API-SPEC §4 要求 10 个）。且代码中**没有任何一处**把 `FileSystemException` 系列包装成库错误：

| 场景 | 实际抛出 | 应为 |
| :--- | :--- | :--- |
| `backup()` 到不存在的目录 | `PathNotFoundException` | `IoError` |
| 提交失败（`.tmp` 被占用） | `PathAccessException` | `IoError` |
| `replaceFileAtomicallyWithRetry` 兜底失败 | `StateError`（`db.dart:1364`） | `IoError` |
| `LockFile.acquire` 目录不存在 | 吞掉一切 → 10 秒后 `LockedError` | `IoError` |
| `open()` 时 ns 字节非法 UTF-8 | 裸 `FormatException`（`format.dart:308`） | `CorruptedError` / `InvalidUtf8Error` |

`src/dart/test/fuzz_lite_test.dart:62` 的白名单里直接写了 `err is FileSystemException`——**测试侧已经默认接受了这个漏洞**。对照 Go 同源测试（`fuzz_lite_test.go:61`）枚举的是库自己的 `InvalidNamespaceError`/`KeyTooLargeError`/`IoError`。

**注意**：`src/dart/README.md:173` 却对外文档化了 `InvalidNamespaceError`——**文档与实现不符**。

**修改建议**：补两个异常类，在 `replaceFileAtomicallyWithRetry`、`_commitFlush` 写段、`_appendEntries`、`backup`、`LockFile.acquire` 处包装 `FileSystemException`，并收紧 fuzz 测试的白名单。

<a id="p1-23"></a>
#### P1-23 Dart：`deferred` 模式的 `Timer` 在 `open()` 失败时泄漏，进程永不退出

**位置**：`src/dart/lib/src/db.dart:67-74`（构造即起定时器）、`db.dart:107-121`（失败路径不 cancel）

```dart
CkvDb._(this._path, this._canonicalPath, this._options) {
  if (!_options.readOnly && _options.syncMode == SyncMode.deferred) {
    _syncTimer = Timer.periodic(interval, (_) => _flushIfDirty());   // 已挂上事件循环
  }
}
...
try { db._initAndRecover(); }
catch (_) { db._connectionLock!.release(); db._connectionLock = null; rethrow; }  // ← 未 cancel _syncTimer
```

**实测**：Dart 审查的探针脚本在打印完 `DONE` 之后**进程未退出**，被外层 240s 超时杀掉。

`Timer.periodic` 会让 Dart VM 的事件循环保持存活。`SyncMode.deferred` 下的任何 `open()` 失败（目录不存在、文件损坏、`LockedError`）都会让进程挂住。对 CLI 工具是直接的功能缺陷。

**修改建议**：把定时器创建从构造函数移到 `_initAndRecover()` 成功之后，或在 `catch` 分支加 `_syncTimer?.cancel()`。

<a id="p1-24"></a>
#### P1-24 Dart：`_registry` 是强引用表，未 `close()` 的实例永不释放

**位置**：`src/dart/lib/src/db.dart:39`、`db.dart:1305-1307`

```dart
static final Map<String, CkvDb> _registry = {};
```

API-SPEC §3.1 明确要求「统一由**弱引用**注册表返回同一个数据库实例……只有当前实例完全 `close()` / **引用被 GC 释放后**，再次打开才会重新执行文件初始化」。Dart 用强引用表把"GC 释放"这条路径**永久堵死**。

**实测**：

```
CkvDb? db = CkvDb.open(p); db = null;   // 丢弃，从不 close
CkvDb.open(p, DbOptions(lockTimeoutMs: 800)) -> 拿到【同一个实例】
锁从未释放给其它进程
```

探针结束时删除临时目录失败（`errno = 32`，文件被本进程占用）。

**修改建议**：Dart 3.2+ 有 `WeakReference<T>`。把 `_registry` 的值改为 `WeakReference<CkvDb>`，在 `open` 中 `.target` 为 null 时重建实例；并补"遗忘实例后重开"的测试。

<a id="p1-25"></a>
#### P1-25 Dart：只读模式仍会修改文件系统

**位置**：`src/dart/lib/src/db.dart:139-157`（`.tmp` 晋升）、`src/dart/lib/src/lock.dart:44`

整个 `_initAndRecover` 只在 `.bak` 恢复处检查了 `_options.readOnly`（`db.dart:186, 239`），**`.tmp` 分支没有检查**。`LockFile.acquire` 用 `FileMode.append` 也**会创建**锁文件。

**实测**：

```
readOnly open promoted .tmp? db exists now = true     ← 只读打开改写了文件系统
readOnly open re-created .lock? true                  ← 只读打开创建了新文件
```

违反 API-SPEC §3.1。对挂载为只读的文件系统（只读 U 盘、容器 ConfigMap、`chmod 444`）打开会直接抛异常而非降级。

**修改建议**：`_initAndRecover` 开头加只读分支（仅"读 + 校验"，不 rename/delete/copy）；`.tmp` 存在但 `.db` 缺失时抛 `CorruptedError` 提示需要写权限；只读时锁文件以 `FileMode.read` 打开。

<a id="s-p1-kotlin"></a>
### 4.5 Kotlin 独有

<a id="p1-26"></a>
#### P1-26 Kotlin：`incrementInt64` / `dropNamespace` 完全缺失只读校验

**位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:818-819`、`:834-838`、`:879-896`

`checkWritable()` 全仓只有 5 个调用点（`CkvDb.kt:359 / 707 / 905 / 1135 / 1160`），**恰好漏掉了 `incrementInt64`（第 818 行进入 `rwLock.write`）和 `dropNamespace`（第 879 行进入 `rwLock.write`）**。

`appendEntries`（`:529-594`）内部只调 `precheckDiskState`，**没有任何 `checkWritable()`**。因此：

| 配置组合 | 实际行为 |
| :--- | :--- |
| `readOnly=true` + `APPEND_ONLY_LOG` | **真的往 `.db` 追加帧并 fsync** |
| `readOnly=true` + `SNAPSHOT` + `DEFERRED/MANUAL` | 内存被改且 `isDirty=true` 永不刷盘 |
| `readOnly=true` + `SNAPSHOT` + `IMMEDIATE` | 第 819 行 `getOrPut` 已塞入空 store，之后才抛 `LockedError` → **失败但 `namespaces()` 里多出幽灵命名空间** |

对照 .NET 是在写锁内、任何内存修改**之前**显式检查。Kotlin 这里是独有的漏洞。

**根因**：只读/生命周期前置条件**散落在每个方法里手写**，漏两个就出洞。

**修改建议**：所有写路径统一经由一个 `writeLocked { }` 包装器做前置校验，不要在每个方法里各写一遍。

<a id="p1-27"></a>
#### P1-27 Kotlin：AOL 模式后台刷盘无代际守卫，`isDirty` 被误清

**位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:278-293`

```kotlin
FileOutputStream(dbFile, true).use { fos -> fos.fd.sync() }   // 282: 此时 fsync
...
rwLock.write {
    if (options.engineMode == StorageEngineMode.APPEND_ONLY_LOG) {
        isDirty = false                                        // 289: 之后才清标志
    }
}
```

`SNAPSHOT` 分支第 314 行有 `dirtyGeneration != generation` 守卫，**AOL 分支完全没有等价守卫**。竞态序列：

1. T1 在 `:263` 读锁内看到 `isDirty=true`，释放锁；
2. T1 在 `:282` 完成 `fd.sync()`；
3. T2 取写锁，`appendEntries` 追加帧并 `isDirty = true`，释放；
4. T1 取写锁，`isDirty = false` —— **T2 那帧的脏标记被清掉**。

**影响**：`Deferred + AOL` 下 T2 写入的帧再也不会被后台线程 fsync；`close()` 也因标志为 false 而跳过 → 非正常退出时该帧丢失。**这是持久性缺陷，不是性能问题。**

**修改建议**：在 `:261` 的读锁里一并采样 `generation = dirtyGeneration`，在 `:287` 的写锁块内加 `if (dirtyGeneration == generation) isDirty = false`。

<a id="p1-28"></a>
#### P1-28 Kotlin：强类型读取两次独立取读锁，中间存在删除竞态

**位置**：`src/kotlin/src/main/kotlin/ckv/CkvDb.kt:966-982` 等四处

```kotlin
fun getString(ns: String?, key: String): String? {
    val bytes = getBytes(ns, key) ?: return null      // 第 1 次读锁，已释放
    rwLock.read {                                      // 第 2 次读锁，中间有窗口
        val store = namespaces[normNs]!!              // ← 并发 dropNamespace 变 null
        val item = store.items[idx]                   // ← 并发 delete 让 idx 为负
```

**影响**：多线程下任何强类型读取都可能抛**非 `DbError` 体系内**的运行时异常（`NullPointerException` / `IndexOutOfBoundsException`），直接违反 API-SPEC §4。

**修改建议**：抽一个 `withEntry(ns, key, type, decode)` 在**单次读锁内**完成「定位 + 类型校验 + 解码」。

<a id="s-p1-cross"></a>
### 4.6 跨语言共性 P1

| 编号 | 问题 | 涉及语言 | 位置 |
| :--- | :--- | :--- | :--- |
| <a id="p1-29"></a>P1-29 | **默认模式下前台写全程持写锁做 O(N) 序列化 + fsync**，读操作全程阻塞。后台路径（C16）已做两阶段优化，前台路径没有，形成不对称 | 全部 5 门 | Rust `db.rs:2402-2405`；Go `db.go:2344-2349`；.NET `Db.cs:1786-1790`；Kotlin `CkvDb.kt:708`；Dart `db.dart:651-654` |
| <a id="p1-30"></a>P1-30 | **`put_bytes` 不拷贝调用方缓冲区**（读侧却拷贝），语义不对称 | .NET / Go / Kotlin / Dart | 见 [P1-20](#p1-20) 对照表 |
| <a id="p1-31"></a>P1-31 | **`HeaderFlags` 从不校验**，违反 §3.1「若读取端遇到未知非零必选位则拒绝打开」 | 全部 5 门 | Rust `format.rs:24-44`；.NET `Db.cs:723-742`；Kotlin `Format.kt:22-42`；Dart `format.dart:25-49` |
| <a id="p1-32"></a>P1-32 | **尾部截断前不生成 `.corrupt.<unix_ms>` 备份**，违反 §3.4 阶段二硬性要求（5 语言全部缺失） | 全部 5 门 | Rust `format.rs:349-457`；Go `format.go:353-431` |
| <a id="p1-33"></a>P1-33 | **`onWarning` 在持有写锁时被调用** → 用户回调里调 `db.get_*()` 会死锁。Watcher 有锁外派发铁律，`onWarning` 没有同等保障 | Go / .NET / Kotlin / Dart | Go `db.go:2203-2208`；.NET `Db.cs:1371/1599/2656` |
| <a id="p1-34"></a>P1-34 | **目录 fsync 失败静默**，缺 `onWarning("onDirSyncUnavailable")`（§1.2 第 7 条要求） | Go / Dart | Go `lock_unix.go:44-50` |
| <a id="p1-35"></a>P1-35 | **`Deferred`/`Manual` 模式完全跳过写前磁盘预检**，外部修改检测形同虚设；.NET 的 `FlushIfDirty` 还用裸 `catch` 吞掉预检错误 | .NET / Dart | .NET `Db.cs:1792-1797` + `:450-453`；Dart `db.dart:655-659` |
| <a id="p1-36"></a>P1-36 | **`iterPrefix`/`ScanRange` 不过滤非法 UTF-8 键，与同文件内 `keys()` 自相矛盾**，违反 §3.3.3 | .NET / Go / Dart / Kotlin | .NET `Db.cs:1918/2481/3084/3142` vs `:2434` |
| <a id="p1-37"></a>P1-37 | **`KeyChangeOp` 只有 2 值，缺 `DropNamespace`/`Clear`**，违反 §3.6.1 四值 ABI | Kotlin（最严重）；Dart | Kotlin `Types.kt:22-25`，`dropNamespace` 对每键发 `DELETE` |
| <a id="p1-38"></a>P1-38 | **二次打开的配置冲突检查不完整**（Rust/.NET/Kotlin 各比 3 项，Go 完全不比），`ReadOnly` 冲突静默升权 | 全部 5 门 | 见 [P1-15](#p1-15) |
| <a id="p1-39"></a>P1-39 | **事件派发顺序不保证等于提交顺序**（先释放写锁再遍历事件快照），且一个回调抛异常会中断后续所有监听器与事件。README 未声明该语义 | 全部 5 门 | .NET `Db.cs:246-270`（"有意不吞"，需与 Rust/Go 的 panic 传播核对并写入规范） |
| <a id="p1-40"></a>P1-40 | **默认引擎 `SnapshotAtomic+Immediate` 每写 O(全库) 且无任何运行时护栏**——`EngineMode` 字段文档注释与 README 选型表均无硬警告，实测 500→2.29ms / 2000→2.93ms / 5000→4.43ms 每写，外推 5 万条目约 40ms/写（详见 [§7.2](#s-engine-cost)） | 全部 5 门 | 探针 8 实测；`docs/BENCHMARKS-20261017.md:49` |

---

<a id="s-p2"></a>
## 五、P2 一般缺陷

### 5.1 Rust

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P2-1 | `Batch::get_bytes` / `get_string` 只回退到主库，**不支持从后向前命中最新的暂存墓碑链**，多次 clear/drop 后语义需推演 | `db.rs:1592-1631` |
| P2-2 | `Db::batch(f)` **不预先检查可写性**就已执行用户闭包。已关闭的库上闭包仍会执行（产生副作用）后才报错 | `db.rs:1454-1461` |
| P2-3 | `write_batch` 的 `raw_entries.retain(...)` 在每个 `ClearNamespace` 上是 O(n) → 含 k 个 clear 的批次为 O(k·n) | `db.rs:1368` |
| P2-4 | `Entry::string_value` 先 `self.raw_bytes.clone()` 再 `String::from_utf8` —— 不必要的整份拷贝 | `types.rs:137` |
| P2-5 | `file_size()` / `stats()` 在持读锁期间做 `fs::metadata` 系统调用 | `db.rs:1103/1117` |
| P2-6 | `Drop for DbInner` 在析构函数里做 `join()` 与 `fsync` —— 不可预测的阻塞，且若在 panic 展开中析构风险更高 | `db.rs:237-266` |
| P2-7 | `DbError` 未实现 `Clone` / `PartialEq` | `errors.rs:3-14` |
| P2-8 | `db_options` 缺 `forceUnlock`（§5.1.1 硬性要求）与 `dead_ratio_compaction_threshold`（API-SPEC §3.1） | `options.rs:23-74` |

### 5.2 .NET

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P2-9 | `Utf8StringComparer.Compare` 与 `Equals` 不自洽（孤立代理项：`Equals=false` 但 `Compare=0` 且 `GetHashCode` 相同），违反 `IComparer`/`IEqualityComparer` 契约 | `BinaryHelper.cs:145-168` |
| P2-10 | `Utf8StringComparer.GetHashCode` **每次查表都分配一个 `byte[]`**，且用固定 FNV-1a（无随机种子，可被哈希碰撞 DoS，键来自磁盘=外部输入） | `BinaryHelper.cs:157-169` |
| P2-11 | `Snapshot.Namespaces()` 返回**未排序**结果（内部是普通 `Dictionary`），违反 §3.3.2；`CkvDb.Namespaces()` 却是对的——两者不一致 | `Db.cs:3052-3066` |
| P2-12 | 缺 `IoError` 异常类型，绝大多数 IO 失败以裸 `IOException`/`UnauthorizedAccessException` 逃逸出 `DbError` 体系 | `Errors.cs`（整个文件） |
| P2-13 | `LockFile.Acquire` 把**所有** `UnauthorizedAccessException` 当成"别人持锁" → 目录不可写/只读挂载会误诊为锁争用，退避 10 秒后报 `LockedError` | `LockFile.cs:72-75` |
| P2-14 | `LockFile` / `CkvDb` 均无终结器，句柄释放依赖 GC 的 `SafeFileHandle` 终结器（非确定性） | `LockFile.cs:86-93` |
| P2-15 | macOS 默认 APFS 大小写不敏感，但注册表键在非 Windows 平台用 `StringComparer.Ordinal` → `/data/Foo.db` 与 `/data/foo.db` 变成两个实例、两个锁、两个写者 | `Db.cs:110-113` |
| P2-16 | `SerializeSnapshotBytes` 每帧一次全量 `ToArray()` + `MemoryStream` 从 0 容量增长 → 64MB 库每次写产生约 6× 库大小的瞬时分配 | `Db.cs:1165-1229` |
| P2-17 | `Crc32.Compute(null)` 静默返回 0 而非抛 `ArgumentNullException` —— 把调用方的空引用 bug 伪装成"CRC 为 0"的校验失败 | `BinaryHelper.cs:38-48` |
| P2-18 | `Utf8ByteComparer` 是完全未使用的死代码；`LoadFromBytes` 也是 | `BinaryHelper.cs:102-134`；`Db.cs:744-748` |
| P2-19 | macOS 上 `RandomAccess.FlushToDisk(dir)` 返回 EINVAL 会被吞成永久 no-op，而 macOS 是 POSIX，`fsync(dir)` 本应可用 | `Db.cs:1347-1356` |

### 5.3 Go

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P2-20 | `Backup` 在 `RUnlock()` **之后**读 `db.currentMaxTxID` → Go 内存模型下的 data race | `db.go:990` → `:1003` |
| P2-21 | `Backup` 不取 `flushMu`、不校验 `dest`，与写路径争抢同一个 `.tmp` | `db.go:975-1035` |
| P2-22 | AOL 模式三处「fsync 打开失败仍清脏标记」，把未落盘数据当已落盘 | `db.go:266-269`、`1057-1061`、`358-362` |
| P2-23 | `appendEntries` 吞掉 `f.Sync()` 错误，`SyncModeImmediate` 名不副实（违反 §5.1.1.1「最保守、不易丢数据」） | `db.go:2299-2301` |
| P2-24 | `precheckDiskState` 吞掉 `ScanMaxTxID` 错误，介质损坏被报成"并发修改" | `db.go:1951` |
| P2-25 | 注册表键使用**原始路径字符串**，未规范化。`data.db` 与 `.\data.db` 会变成两个键 → 抢文件锁 → `LockedError` | `db.go:179/208/385` |
| P2-26 | `DbOptions` 零值被静默接受。`EngineMode` 枚举**没有 0 值**（1/2），`DbOptions{}` 落入 `EngineMode=0` → `flushIfDirty` **不取 `flushMu` 却仍写 `.tmp`**（本文件唯一真正的锁序漏洞） | `types.go:202-206`；`db.go:252` |
| P2-27 | `applyMutation` 在快照模式下丢弃 `entries`，但调用方仍认真构造。`ClearNamespace` 对 100 万键的命名空间会分配 100 万个被丢弃的 `*RawOpEntry` | `db.go:2318-2360`；`:870-879` |
| P2-28 | `namespaceStore.put` 的有序插入是 O(N) memmove（`keyItem` 40 字节）；`rebuildSortedNs()` 每次新建命名空间全量重排 → 加载期 O(K² log K) | `db.go:61-67`；`:1668-1677` |
| P2-29 | `Snapshot.Close()` 与并发读的数据竞争（无锁写 `s.namespaces = nil`） | `db.go:2703-2707` |
| P2-30 | `Snapshot()` 用写锁而非读锁 | `db.go:2465` |
| P2-31 | `lock_unix.go:15` 只读连接也用 `O_RDWR` 打开锁文件 → 只读部署直接 EACCES → 报 `IoError` 而非 `LockedError` | `lock_unix.go:15` |
| P2-32 | `db.go:1925-1935` 有一段描述 `beginFlush` 的注释，但**没有这个函数**（重构遗留死注释），且与紧随其后的注释自相矛盾 | `db.go:1925-1935` |
| P2-33 | `Stats()` 注释写"不触发磁盘 I/O"，但第 964 行调用了 `FileSize()`（`os.Stat`） | `db.go:955/964` |
| P2-34 | 加载期 `applyEntries` **不做命名空间归一化**（`string(e.Namespace)`），一个含物理 `"default"` 的外来文件会造出 API 永远访问不到的孤立命名空间 | `db.go:1868/1899` |

### 5.4 Kotlin

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P2-35 | 加载器把整个 DB 读进内存 + 额外物化全量 op 列表 → **峰值 ≈ 3× 文件大小**。100MB WAL 需 ~300MB，移动端 OOM | `CkvDb.kt:152`；`Format.kt:217/329-349` |
| P2-36 | `stageAndReplace` 每次快照写都**全量复制旧库到 `.bak`** → 把 §4.2 要求的 0% 写放大变成每次写 2× 全库 IO | `FileRotation.kt:98-103` |
| P2-37 | 前台快照写在**独占写锁内**完成「全量序列化 + fsync + 双轮转」，64MB 库一次写阻塞所有读者数百毫秒 | `CkvDb.kt:708` |
| P2-38 | `batch()` 在 AOL 模式下多帧切分时**磁盘侧不是原子的**，违反 §3.4「全成或全败」 | `CkvDb.kt:943` |
| P2-39 | `NamespaceStore` 用 `ArrayList` + 二分，**WAL 乱序重放退化为 O(n²)**：10 万条约 25 亿次元素搬移 | `Storage.kt:56/29-44` |
| P2-40 | 未知 `OpCode` 被**静默丢弃**（`when` 无 `else` 分支）→ 数据无声丢失 | `Format.kt:359-372` |
| P2-41 | `isValidUtf8` 每次调用新建 `CharsetDecoder`，加载路径上每条目调 2 次 → 100 万条 = 200 万次分配 | `BinaryHelper.kt:121-131` |
| P2-42 | `Snapshot` 不是 O(1) CoW（深拷贝全部 key+value），且在读锁内执行；`isClosed` 非 `@Volatile` | `CkvDb.kt:1097-1106` |
| P2-43 | `Snapshot` 关闭后**抛异常**而非 API-SPEC §3.5.3 要求的"温和返回空" | `Storage.kt:88-90` |
| P2-44 | `batch()` 的写入者检查在 `checkWritable()` **之前**；空 batch 在只读/已关闭库上静默成功 | `CkvDb.kt:943` |
| P2-45 | `delete` 对不存在的键、`dropNamespace` 对不存在的命名空间，仍会**整帧落盘** | `CkvDb.kt:858-864/879-896` |
| P2-46 | `scanMaxTxId` 用 `and 0x7FFFFFFF` 屏蔽符号位，与 `loadAllWithRecovery:260` 的 `payloadLen < 0` 判定口径不一致 | `Format.kt:163` |
| P2-47 | `File.renameTo` 返回值被忽略，失败时仍走 `applyParsed` 并置 `dbExists = true` → 内存有数据、磁盘没有 `.db`，库进入不可写状态 | `CkvDb.kt:138-142` |

### 5.5 Dart

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P2-48 | 孤立代理项与 `U+FFFD` 编码碰撞 → 两个不同的 Dart `String` **塌缩成同一个键**（`utf8.encode('\uD800') == utf8.encode('\uFFFD')`），实测两次 put 只得一个键 | `db.dart:951` 等全部 key 入口 |
| P2-49 | `Constants.fileMagic` 是**可变公开的 `Uint8List`**，任何调用方都能改掉全局魔数 | `constants.dart:4` |
| P2-50 | `Entry` 缺 §5.2.1 规定的全部类型守卫方法（`isBytes/isString/isInt64/isFloat64/isBool/asString/asInt64/asFloat64/asBool`） | `types.dart:3-14` |
| P2-51 | `open()` 不创建父目录 → `open('<不存在目录>/x.db')` 10 秒后报误导性的 `LockedError` | `db.dart:97-121` |
| P2-52 | `.tmp` 晋升失败时**无条件删除 `.tmp`** → 把瞬时 IO 失败（杀软占用）与"内容确实损坏"混为一谈，可能销毁唯一好数据 | `db.dart:139-157` |
| P2-53 | `Snapshot` 释放后抛异常而非"温和返回空"（违反 §3.5.3） | `storage.dart:99-103` |
| P2-54 | `delete`/`dropNamespace`/`clearNamespace` 在 `_checkWritable()` **之前**提前 return → 关闭后的库"半死" | `db.dart:1036/1073/1098` |
| P2-55 | 加载器全量读入内存 + 每条目 `fromList(sublist(...))` 双重拷贝 → **峰值 ≈ 3× 文件大小** | `db.dart:142/160/189/227`；`format.dart:313/328` |
| P2-56 | `manual`/`deferred` 下 `_currentMaxTxId++` 造成 TxID 空跳，破坏 `stats().currentMaxTxId` 的跨语言可比性 | `db.dart:657` |
| P2-57 | `_flushIfDirty` 静默吞掉所有异常，无 `onWarning`（实测 `warnings recorded: []`，`fileSize=0`，无任何信号） | `db.dart:264-269` |
| P2-58 | `syncOnWrite: false` + `immediate` 时 `_isDirty` 仍被无条件清零 → `close()` 不补刷 | `db.dart:404-428` |
| P2-59 | `Snapshot` 无终结器 → 未 dispose 的快照永久触发"每写一次全量克隆" | `db.dart:767-773` |
| P2-60 | `KeyTooLargeError` 消息硬编码 "1024"（实际常量 65535），且该校验因 `keyLen` 取值域 `[0,65535]` 而**永不进入** | `format.dart:301-303` |
| P2-61 | 版本检查排在 CRC 校验**之前** → 偏移 8-9 的位翻转会报 `UnsupportedVersionError` 而非 `CorruptedError` | `format.dart:37/44` |
| P2-62 | 只含 `clearNamespace`（或空 batch）的提交仍触发**全量快照** | `db.dart:1130` |

<a id="六跨语言架构性问题"></a>
---

<a id="s-arch"></a>
## 六、跨语言架构性问题

### 6.1 规范自身矛盾（文档债，非实现缺陷）

**两份规范在默认值上直接冲突**：

| 配置项 | `KV-DB-DESIGN-SPEC.md` §5.1.1 | `API-SPEC.md` §3.1 | 5 语言实现实际值 |
| :--- | :--- | :--- | :--- |
| `sync_mode` 默认 | `Immediate` | **`Always`** | 5 语言全部 `Immediate` |
| `sync_interval_ms` 默认 | `100` | **`50`** | 5 语言全部 `100` |
| Value 上限 | `16,777,216` | **`16,777,215`** | 5 语言全部 `16777216` |
| `sync_mode` 枚举 | 3 值 `Immediate/Deferred/Manual` | **4 值** `Always/Periodic/Deferred/Manual` | 5 语言全部 3 值 |
| `dead_ratio_compaction_threshold` | §4.5「**不新增公开选项**」 | 列为可配置项 `0.5` | 5 语言全部为内部常量 |

**5 语言实现一致站在 `KV-DB-DESIGN-SPEC` 一侧**，`API-SPEC` 是离群值。调用方按 `API-SPEC` 写的配置项在**所有五种语言里都找不到**。

**其他规范债**：

| 规范条目 | 状态 |
| :--- | :--- |
| §4.1.2 32 字节锁文件 + 防 ABA + 窃取协议 | 已被 C9 决策取代，5 语言全部未实现，规范未同步 |
| §5.1.1 `DbOptions.forceUnlock` | 5 语言全部缺失 |
| §3.4 阶段二 `.corrupt.<unix_ms>` 备份 | 5 语言全部缺失 |
| §1.2 第 7 条 `onWarning("onDirSyncUnavailable")` | 仅 .NET / Kotlin 落实；Go / Dart 缺失 |
| §3.3.3「缓冲式前缀扫描」 | 规范强制了 O(N) 全量物化的低效设计 |
| §3.5.1「纳秒级 CoW 快照」 | Rust 是 O(#命名空间)（可接受），.NET/Go/Kotlin/Dart 是 **O(全库深拷贝)** |
| §5.2.2 `Entry` 接口（9 个方法） | Dart 的 `Entry` 一个都没有 |

### 6.2 "前台路径没有后台路径做得好"的系统性问题

这是本项目最值得关注的**架构级不一致**：

| 关注点 | 后台刷盘路径（C16） | 前台同步写路径（Immediate，默认） |
| :--- | :--- | :--- |
| 快照获取 | 短读锁克隆后放锁 | **全程持写锁** |
| 序列化 + fsync | 锁外执行 | **锁内执行** |
| 陈旧快照防护 | `dirtyGeneration` 代际校验 | **无** |
| 读者影响 | 不阻塞 | **全程阻塞** |

后台路径有精心设计的两阶段提交与代际守卫，前台路径完全没有。**结果是：默认配置下最常用的写路径，是整个库里保护最弱的那条路径。**

**统一修复方向**（5 语言同构）：把前台路径改为 `取写锁 → 预检 + 克隆数据 + 应用内存变更 + 递增 dirtyGeneration → 放锁 → 锁外序列化 .tmp → 取短写锁 → 代际校验未变才轮转`。

### 6.3 输入校验的"入口收口"缺失

三个 P0/P1 都指向同一个根因：**校验逻辑散落在各个公开方法里手写，没有统一的收口**。

- Kotlin：`checkWritable()` 有 5 个调用点，漏了 2 个 → 只读库可被写盘（P1-22）
- Dart：`normalizeNamespace` 不校验长度 → 单次 put 摧毁数据库（P0-1）
- .NET：`LockFile.Acquire` 的 open 在重试窗口外（Rust FIX-02(a) 已修，.NET 未修）

**修复方向**：所有写路径必须经由**单一入口函数**（如 Rust 的 `apply_mutation`、.NET 的 `PutInternal`），由它统一完成「可写性校验 → 长度/类型校验 → 落盘 → 应用内存 → 派发事件」。新增公开方法时**只能**通过这个入口，禁止旁路。

---
<a id="七接口缺失与易用性问题"></a>

<a id="s-api"></a>
## 七、接口缺失与易用性问题

<a id="s-api-matrix"></a>
### 7.1 API 完整度矩阵（实测 grep 结果）

| API（API-SPEC §2） | Rust | .NET | Go | Kotlin | Dart |
| :--- | :---: | :---: | :---: | :---: | :---: |
| `namespaces` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `keys` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `iterPrefix` | ✅ | ✅ | ✅ | **❌ 缺失** | ✅ |
| `scanRange` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `stats` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `backup` | ✅ | ✅ | ✅ | **❌ 缺失** | ✅ |
| `fileSize` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `flush` / `compact` | ✅ | ✅ | ✅ | ✅ | ✅ |
| **`isClosed`** | **❌** | **❌** | **❌** | ✅ | **❌** |
| `incrementI64` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Entry` 类型守卫（9 个） | ✅ | 部分 | 部分 | N/A | **❌ 全缺** |
| `IoError` | ✅ | **❌** | ✅ | **❌** | **❌** |
| `InvalidNamespaceError` | ✅ | ✅ | ✅ | **❌**（用 `KeyTooLongError` 顶替） | **❌** |
| `ConcurrentModificationError` | ✅ | ✅ | ✅ | **❌**（用 `TransactionConflictError`） | ✅ |
| `KeyTooLargeError` | ✅ | ✅ | ✅ | **❌**（拆成两个） | ✅ |

**`isClosed` 缺失率 4/5** —— 全仓唯一的 `isClosed` 命中是 Kotlin `Storage.kt:86` 的**私有** `Snapshot.isClosed` 字段。API-SPEC §2 明确列出该行，五语言无一实现公开访问器。

**合并自外部反馈后新增的 API 缺口**（5 语言全部缺失，不属于"某门语言的锅"）：

| API 缺口 | 外部反馈编号 | 影响 | 成本 |
| :--- | :--- | :--- | :--- |
| **`scanRange` 无 `limit` / `offset` / 反向扫描** | #6 | "按时间倒序取最近 N 条"这一**普遍场景**只能全量拉回内存再 `OrderByDescending`；1 万条目的 ns 即使只要 10 条也瞬时分配上万个对象 | 底层已二分定位 `startIdx`，加计数提前 `break` + 从 `endIdx` 反向遍历即可，**几乎零成本** |
| **无 `ns` 参数省略重载** | #5 | 单命名空间场景必须写 `db.GetString(null, "key")`，属无效样板代码 | 纯增量、零风险，可脚本批量生成 |
| **`Watch` 仅全局粒度，无 ns / 前缀过滤** | #7 | 任何 key 变动都广播给所有监听者，订阅方须自行 `if` 过滤 | 加 `Watch(ns, cb)` / `WatchPrefix(ns, prefix, cb)` 重载，回调入口做简单过滤即可 |
| **无任何 `*Async` API** | #2 | `Open` / `Flush` / `Compact` / `Backup` 都是阻塞磁盘 IO，UI 线程只能 `Task.Run` 包装 | 见 §7.2 的**归因更正**——这是症状不是根因 |

<a id="s-engine-cost"></a>
### 7.2 默认引擎模式的开销无任何护栏（实测量化）

外部反馈 #2 报告"桌面卡顿"并归因于"全链路缺失 async"。**该归因是错的**，真实根因是默认引擎的每写全量重写成本（详见 [§7.3](#s-async-correction)）。本项即该根因的独立条目。

**实测**（探针 8）：

```
DEFAULT (SnapshotAtomic+Immediate), live=  500: steady write median=2.29ms  (~437 ops/s)
DEFAULT (SnapshotAtomic+Immediate), live= 2000: steady write median=2.93ms  (~341 ops/s)
DEFAULT (SnapshotAtomic+Immediate), live= 5000: steady write median=4.43ms  (~226 ops/s)
```

写延迟**随库大小线性增长**。按此斜率外推，5 万条目（约 5.7MB）约 40ms/写 ≈ 25 ops/s。

README 虽写"默认适合 <2MB"，但：

1. **没有任何运行时保护或告警**阻止用户把默认模式用在 50MB 上（届时吞吐比 `AppendOnlyLog` 差 **2 个数量级**）；
2. 该结论散落在 README 的表格里，**不在 `DbOptions.EngineMode` 的字段文档注释中**，而后者才是 IDE 里会弹出提示的地方；
3. 外部反馈的现场（一个实际运行的桌面应用）就踩在这个坑上。

**修改建议**：
1. 在 5 语言的 `EngineMode` 字段文档注释里加**硬警告**：「`SnapshotAtomic` 每次写入会全量序列化并 fsync，成本 O(库大小)；数据量超过约 2MB 或存在高频写入时，请改用 `AppendOnlyLog`」。
2. 在 README 的引擎选型表旁补一条实测数据（探针 8 的三行），而不是只给文字描述。
3. 考虑提供一个 `Stats()` 字段（如 `live_payload_bytes`）配合首次写入时的**一次性**告警（不要每次写都告警）。

<a id="s-async-correction"></a>
### 7.3 归因更正：async 是症状不是根因

外部反馈 #2 报告桌面 UI 卡顿，归因于"全仓 0 处 `async`/`Task`"。**现象属实，归因错误。**

- 内存读取确实无需异步（常驻内存、无系统调用、数十纳秒），这部分外部反馈自己也判断正确。
- 真正的问题在 `docs/BENCHMARKS-20261017.md:49` 记录的实测数据：默认引擎 `SnapshotAtomic + Immediate` **每次写都整库序列化 + fsync**——2MB 时 10.6ms/写、10MB 时 41.8ms/写、50MB 时 376ms/写。对照 `AppendOnlyLog` 稳态 0.17~0.18ms/写（≈5,500 ops/s），**与规模无关**。
- 把默认模式包一层 `*Async` **只是把成本藏起来**，不解决问题——用户仍要等同样的时间，只是 UI 不卡了。

**修正后的优先级**：

| 顺序 | 动作 | 理由 |
| :--- | :--- | :--- |
| 1 | [§7.2](#s-engine-cost) 的引擎选型硬警告 + `EngineMode` 字段注释 | 治本，0.2 人日 |
| 2 | 为 `FlushAsync` / `BackupAsync` 提供一等公民签名 | 这两个是真正值得异步化的（前者 fsync、后者整库序列化）；实现上 `Task.Run` 包装即可（引擎内部为同步 IO，不值得为 fsync 做 IOCP） |
| 3 | `OpenAsync` / `PutStringAsync` | 收益小，优先级低 |

**外部反馈的残余价值（已采纳）**：其"Backup 几十 MB 要几秒"的判断成立（`Backup` 确实整库序列化，`Db.cs:1868-1896`），这支持把 `BackupAsync` 排在异步化的第 2 位。

<a id="s-ext-ergonomics"></a>
### 7.4 合并自外部反馈：新增的易用性问题

**新增·外馈 E-1：`Open(path) + Backup(dest)` 是错误范式，且无任何文档引导**（**P1**）

`Backup` 是**实例方法**（`Db.cs:1868`），对已打开的库正解就是 `db.Backup(dest)`。但在缺文档提示的情况下，用户很自然写：

```csharp
using var db = CkvDb.Open(src);
db.Backup(dst);            // ← 在单例语义下这是双重错误
```

**这既是 [P0-4](#p0-4) 的直接触发器，也是它的完整复现脚本**——外部反馈的现场崩溃正是这段代码导致的。

`src/dotnet/README.md:124-125` 的 API 表只列了 `Open` / `Close` 与 `Backup` 的名字，**未说明二者在单例下的相互作用**。

**修改建议**：
1. README + XML 文档把 `db.Backup(dst)` 定为唯一推荐写法；
2. 如需"离线导出快照"，提供静态 `CkvDb.BackupFile(src, dst)`：内部复用注册表实例且**绝不 Dispose**。

**新增·外馈 E-2：无终结器 / SafeHandle 安全网** —— 已提升为 [P1-18](#p1-18)。

**新增·外馈 E-3：事件派发顺序不保证等于提交顺序** —— 已提升为 [P1-39](#p1-39)。

<a id="s-rejected"></a>
### 7.5 已驳回的外部反馈（避免重复讨论）

以下外部反馈条目经复核**不成立或不应按建议修改**，记录在此以免后续重复讨论：

| # | 反馈条目 | 复核结论 |
| :--- | :--- | :--- |
| **A** | 异常类命名违背 .NET 规范，建议改用 `CkvException` / `DatabaseLockedException` | **驳回**。反馈方建议的名字**正是本库已标 `[Obsolete]` 的旧名**：`DbError : Exception`（`Errors.cs:29`，新基类）→ `CkvException : DbError`（`Errors.cs:40`，`[Obsolete]`）→ `DatabaseLockedException : CkvException`（`Errors.cs:114`，`[Obsolete]`）。`Errors.cs:12-14` 的类注释写明：**规范要求与 Rust/Go 的 `DbError` 逐字一致，C# 惯例以 `Exception` 结尾，本项目显式接受该偏离**；API-SPEC §4 的五语言映射表亦强制逐字对齐。**改成 `Exception` 后缀是净破坏**（会破坏跨语言错误契约与既有 catch 块） |
| **B** | 类型系统窄（无 `Int32`/`Guid`/`DateTime`/`decimal`，无 `Get<T>/Set<T>`） | **核心不动，部分采纳**。5 种 ABI 类型是与其它四门语言**逐字节互读**的根本约束（黄金向量 + Interop 测试链路），随意扩类型会破坏一致性。但上层 API 不等于存储类型：`Int32 → Int64` 的便捷重载**不新增类型码**，是纯赚项应采纳；`Guid`/`DateTime`/`decimal`/`Get<T>` 应放独立扩展包（如 `Ckv.Serialization`），以 string/bytes 承载，**不得进核心** |
| **C** | `ScanRange` 应改为流式迭代器 | **驳回**。API-SPEC §3.3.3 明文规定「缓冲式前缀扫描语义：内部一次性收集全部匹配条目后返回快照列表，避免泄露内部锁或导致并发迭代器悬垂指针」。这是**写进规范的刻意选择**，且三语言一致。改为流式会破坏跨语言语义一致性并重新引入悬垂/持锁问题。正解是**增补** `limit` / 反向 / 惰性变体，而非替换（见 [§7.1](#s-api-matrix)） |
| **D** | `List<KeyItem>` 插删 O(N) 应换数据结构 | **记为已知限制，暂不动**。量级评估：列表元素是引用，10 万条约 0.8MB，`memmove` 单次约几十微秒——不是灾难，但确是 10 万+ 高频写时的天花板。突破需换跳表/不可变平衡树或"批量排序归并"，与"五语言共用一套确定性简单内存结构"的既定决策直接冲突。FIX-PLAN-V2 已把"存储层重做"列为 v2.0 候选并有明确触发条件 |
| **E** | `Watch` 回调抛异常应被吞掉 | **维持现状**。`Db.cs:264-268` 的"有意不吞"与 Rust/Go 的 panic 传播语义对齐，是正确取舍。但**该语义应在 README 明写**，而非留给用户踩（见 [P1-39](#p1-39)） |

> **方法论说明**：外部反馈 9 条中，**3 条完全成立且被提升为 P0/P1**（#1 #9 + 遗漏项 A/B），**2 条部分成立需拆分处理**（#4 #6），**1 条归因错误**（#2），**1 条已过时**（#3），**2 条属刻意取舍**（#6 的缓冲式语义、#8 的 O(N) 结构）。这说明外部反馈的**价值在于提供现场证据与真实使用视角**，而不在于其优先级判断——本报告的合并以代码证据为准。

<a id="s-kotlin-semantic"></a>
### 7.6 Kotlin 的两个语义反向问题（最危险的一类）

| 问题 | 规范要求 | Kotlin 实际 |
| :--- | :--- | :--- |
| `clear()` | §3.3.4「**保留**命名空间元数据本身（`namespaces()` 中仍能列出）」 | `CkvDb.kt:900-902` **直接 `return dropNamespace(ns)`** —— 物理移除 |
| `dropNamespace` 返回值 | §5.2.2 返回是否命中存在 | `CkvDb.kt:871` 返回 `Unit` |

**`clear()` 语义完全相反**：调用方按规范写的代码在 Kotlin 上会**静默丢失整个命名空间**，且不抛任何异常。这是本次审查发现的最隐蔽的一类缺陷——它不会报错，只会让数据消失。

<a id="s-ergonomics"></a>
### 7.7 其它易用性问题

| # | 问题 | 影响 |
| :--- | :--- | :--- |
| U-1 | **`IncrementInt64` / `PutInt64` 等在部分语言缺 `null` 参数检查**（.NET 的 `PutInt64`/`PutFloat64`/`PutBool` 缺 `ArgumentNullException`） | 错误信息不含参数名 |
| U-2 | **`batch()` 的用户闭包在可写性检查之前执行**（Rust / .NET / Dart） | 已关闭的库上闭包仍执行并产生副作用后才报错 |
| U-3 | **空 batch 在只读/已关闭库上静默成功**（Go / .NET / Dart / Kotlin） | 掩盖调用方 bug |
| U-4 | **`put_bytes` 的数组所有权契约从未在任何语言的文档中说明** | 用户无法知道是否会拷贝 |
| U-5 | **`Snapshot` 关闭后行为不一致**：API-SPEC §3.5.3 要求"温和返回空"，Kotlin/Dart 抛异常，Rust/.NET 未定义 | 跨语言行为分叉 |
| U-6 | **`clear_namespace` 后的空命名空间在重开后消失**（快照只写存活 OpPut） | 依赖"`clear` 后 ns 仍存在"的代码在重启前后行为不一致（.NET 审查发现，5 语言共有） |
| U-7 | **`db.close()` 的共享实例副作用**（Rust） | 见 [P1-5](#p1-5) |
| U-8 | **README 技术描述与实现不符**：.NET README 声称"Windows `LockFileEx` / Unix `fcntl`"——实际 Windows 用的是 `dwShareMode=0` 共享拒绝（不是 `LockFileEx`），Unix 用的是 `flock(2)`（不是 `fcntl(2)`） | 误导 |
| U-9 | **.NET README 示例代码无法编译**：宣传 `db.Snapshot()`，实际方法是 `CreateSnapshot()` | 用户复制即编译失败 |
| U-10 | **`clearNamespace` 在 WAL 模式下墓碑放大**：2000 键 95KB → clear 后 126KB（+30,918 字节 = 2000 条墓碑） | 成本未文档化 |
| U-11 | **`incrementInt64` 溢出为静默补码绕回**（`wrapping_add`），而非抛 `KeyTooLargeError` 或饱和 | 上层计数器可能静默变成负数 |
| U-12 | **Dart 命名空间/键的全零字符串、空字符串行为未文档化**；`"default"` 别名不生效（P0-2） | 用户困惑 |
| U-13 | **新增·外馈** `Open(path)+Backup(dest)` 是错误范式，且无文档引导（见 [§7.4](#s-ext-ergonomics)） | 直接触发 [P0-4](#p0-4)，已造成真实崩溃 |
| U-14 | **新增·外馈** `scanRange` 无 `limit` / 反向，"取最近 N 条"需全量拉回内存再排序（见 [§7.1](#s-api-matrix)） | 便签/日志类业务的核心查询模式不可用 |
| U-15 | **新增·外馈** 所有 `ns` 参数无省略重载，单命名空间场景必须写 `(null, "key")` | 无效样板代码 |
| U-16 | **新增·外馈** `Watch` 仅全局粒度，无 ns / 前缀过滤 | 订阅方须自行 `if` 过滤 |
| U-17 | **新增·外馈** 全库 0 处 `async`/`Task`（.NET），`Open`/`Flush`/`Backup` 阻塞 UI 线程 | 但见 [§7.3](#s-async-correction) 的**归因更正**——根因是默认引擎，不是 async 缺失 |

---

<a id="s-tests"></a>
## 八、测试体系与 CI 覆盖

### 8.1 测试覆盖的结构性缺口

**现有测试全绿（Rust 130 / .NET 109 / Go 全通过），但以下高危路径零覆盖**：

| 未覆盖的高危路径 | 对应的缺陷 | 备注 |
| :--- | :--- | :--- |
| **共享实例的 `close`/`Dispose` 所有权** | [P0-4](#p0-4) | 已有测试**反向固化**错误行为（`PathIdentityTest.cs:83-93`） |
| `renameWithRetry`（Go / .NET / Rust / Kotlin / Dart 全部） | [P0-3](#p0-3)、[P1-14](#p1-14) | |
| 命名空间长度边界（300 字节 / 65536 字节） | [P0-1](#p0-1)、[P1-3](#p1-3) | |
| 跨语言 `"default"` 别名等价性 | [P0-2](#p0-2) | **Dart 无任何对应测试**，故周跑 interop 也拦不住 |
| 同进程并发 `Open`（只有串行重复 Open 测试） | [P0-5](#p0-5) | |
| `Open` 失败后的锁释放 | [P0-6](#p0-6) | |
| `OnWarning` 回调内重入读接口 | [P1-33](#p1-33) | |
| `put_bytes` 别名修改 | [P1-30](#p1-30) | |
| `backup(dest == self.path)` | [P1-10](#p1-10)、P2-21 | |
| `Snapshot` 双重 dispose | [P1-17](#p1-17) | |
| `close()` 后调用各写接口 / 各读接口 | [P1-12](#p1-12)、P2-54 | |
| `Snapshot` 存活时的写性能悬崖 | [P1-1](#p1-1) | |
| 二次打开的 `ReadOnly` 冲突 | [P1-15](#p1-15)、[P1-38](#p1-38) | |
| Dart `deferred` 模式 `open()` 失败后的进程退出 | [P1-23](#p1-23) | |
| `clear()` 后命名空间是否仍存在（Kotlin 语义反向） | [§7.6](#s-kotlin-semantic) | |

**P0-3 与 P0-5 都能被一个 30 行的测试挡住。P0-4（共享实例无所有权模型）已有测试反向固化了错误行为（`PathIdentityTest.cs:83-93`），需要先改测试再改实现。**

> **新增·外馈的一条方法论教训**：`PathIdentityTest.cs:83-93` 之所以会固化错误行为，是因为它测的是"路径身份"却在断言 `Dispose` 语义。**当一个 API 的所有权模型本身没有被显式记录在规范里时，测试作者只能按 .NET 默认心智模型（`IDisposable` = 物理销毁）来写**——于是测试"通过"了，错误却被放大。**这是规范缺口的另一个证据，而不只是代码缺陷。**

### 8.2 CI 配置问题

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| CI-1 | **Go / Kotlin / Dart 只在周计划或手动触发时运行**，PR 路径上完全没有门禁 → 5 语言中 3 门的回归可以静默合入 main | `ci.yml:151-155` |
| CI-2 | **macOS 只在周计划覆盖** → README 声称支持 macOS，但 PR 不验证 | `ci.yml:162` |
| CI-3 | **注释与现实矛盾**：`ci.yml:187` 写"The Kotlin module ships no Gradle wrapper"，但 CHANGES 记录说已补齐 wrapper，且 `jitpack.yml` 用的就是 `./gradlew` | `ci.yml:187`；`jitpack.yml` |
| CI-4 | **无任何静态分析门禁**：Go 无 `go vet`、无 `gofmt -l`；Kotlin 无 ktlint/detekt；Dart 无 `dart analyze`、无 `dart format --set-exit-if-changed`；.NET 无 `dotnet format` | `ci.yml` 全文 |
| CI-5 | **Go 测试未加 `-race`**，而 DEVELOPMENT-GUIDE §2.1 明确要求 `go test -v -race ./...` | `ci.yml:179` vs 指南 §2.1 |
| CI-6 | **无依赖安全扫描**：`cargo audit` / `cargo deny` / Dependabot 全部缺失 | `.github/` |
| CI-7 | Kotlin 作业用裸 `gradle` 而非项目自带的 `./gradlew`，可能与本地构建版本不一致 | `ci.yml:194` |

> **小结**：`docs/DEVELOPMENT-AND-RELEASE-GUIDE.md` §2.1 规定了每个语言的完整质量门禁（`cargo fmt`/`clippy`、`go vet`/`go test -race`、`dart analyze`/`dart format`、`./gradlew check`、`dotnet format`），**CI 实际执行的只是其中的 `cargo` 两项 + 各语言的 `test`**。文档与 CI 严重脱节。

<a id="九工程化与打包"></a>
---

<a id="s-engineering"></a>
## 九、工程化与打包

| # | 问题 | 影响 |
| :--- | :--- | :--- |
| E-1 | **Dart `pubspec.yaml` 缺 `homepage` / `repository` / `issues` / `documentation` / `topics` / `funding` / `LICENSE`** | pub.dev 评分大幅扣分，第三方无法发现 |
| E-2 | **Kotlin `jvmToolchain(17)` 与 README 的 Android 宣称矛盾**：产出 class 版本 61，Android 的 D8/R8 直到 API 34 才完整支持；且模块未配置 `androidTarget()`，更关键的是若 `Storage.kt`/`Format.kt` 使用 `java.nio.file`（API 26+），**在 Android 低版本上直接崩溃** | README 声称"JVM 17+（Android / Desktop / Server）"，实际不可用 |
| E-3 | **Kotlin 库项目用 `implementation(kotlin("stdlib"))`**，纯 Kotlin 库通常应用 `api` | Java 消费者拿不到 stdlib 编译期可见 |
| E-4 | **`.NET csproj` 开了 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` 但全项目无任何 `unsafe`** | 不必要的 AOT/裁剪信任面扩大 |
| E-5 | **`.NET csproj` 全局 `NoWarn 1591`（缺少 XML 注释）** + `GenerateDocumentationFile` → 产出的 `Ckv.xml` 里大量公开成员无文档，NuGet IntelliSense 显示空白 | 用户体验 |
| E-6 | **`.NET csproj` 缺 `EnablePackageValidation` / `PackageValidationBaselineVersion`** | 破坏性 API 变更（刚从旧异常名迁到新名）不会被打包流程拦截 |
| E-7 | **`netstandard2.0` 目标零测试覆盖**（`Ckv.Tests.csproj` 只测 `net8.0`），且它正是三处语义分叉的来源 | 见 [P1-16](#p1-16) |
| E-8 | **Rust `Cargo.toml` 无 `[package.metadata.docs.rs]`、无 `documentation` URL** | docs.rs 体验 |
<a id="十文档债"></a>
| E-9 | **仓库磁盘占用 1.79 GB**（构建产物），但 `.gitignore` 覆盖良好（`target/`、`bin/`、`obj/`、`temp/` 均已忽略，git 追踪文件 221 个，无构建产物入库） | ✅ 这方面没问题 |
| E-10 | **Go 模块声明 `go 1.21`（已 EOL）**，且 Go 是五语言中唯一不在 PR 门禁内的 | 维护性 |

---

<a id="s-docs"></a>
## 十、文档债

| # | 问题 |
| :--- | :--- |
| D-1 | `API-SPEC.md` 的 `sync_mode` 默认值（`Always`）、`sync_interval_ms`（`50`）、Value 上限（`16777215`）、枚举基数（4 值）与实现全部不符 —— 见 §6.1 |
| D-2 | `KV-DB-DESIGN-SPEC.md` §4.1.2 的 32 字节锁协议、§5.1.1 的 `forceUnlock` 已被 C9 决策取代，规范未同步（5 语言全部脱节） |
| D-3 | `KV-DB-DESIGN-SPEC.md` §3.4 阶段二要求 `.corrupt.<unix_ms>` 备份，5 语言全部未实现 |
| D-4 | README 声称"默认适合 <2MB"，但**没有任何运行时保护或告警**阻止用户误用（实测 5000 条目已 4.43ms/写） |
| D-5 | README 关于 `SyncMode` 丢数据范围的表述对 AOL 不准确（实测 AOL+Manual 抗进程崩溃） |
| D-6 | .NET README 技术描述错误（`LockFileEx`/`fcntl`）+ 示例代码无法编译（`Snapshot()` vs `CreateSnapshot()`） |
| **D-11** | **新增·外馈** 三处文档正在教用户写出崩溃代码：`src/dotnet/README.md:45` 的 `using var db = CkvDb.Open(...)`、`docs/API-SPEC.md:87` 未声明"借用者不得 Dispose"、`docs/API-V1-FREEZE.md:220` 未声明"Dispose 是关闭全局实例" |
| D-7 | Dart README 文档化了不存在的 `InvalidNamespaceError` |
| D-8 | Go 源码注释断言了错误的 errno 映射（导致 [P0-3](#p0-3)），属于"注释把错误事实固化"的典型 |
<a id="十一修复优先级路线图"></a>
| D-9 | `DEVELOPMENT-AND-RELEASE-GUIDE.md` 规定的质量门禁多数未进 CI |
| D-10 | README §6 宣传"快照与写入完全互不阻塞"，但未提及存活快照会使每次写退化为 O(N)（[P1-1](#p1-1)） |

---

<a id="s-roadmap"></a>
## 十一、修复优先级路线图

### 批次一：数据安全与止血（预计 2~3 天，全部是改动面很小的修复）

| 顺序 | 项 | 语言 | 改动量 |
| :--- | :--- | :--- | :--- |
| **0** | [P0-4](#p0-4) **止血先行**：`Open` 补 XML 文档 + 修 `README.md:45` 的 `using` 示例 + 停止 `PathIdentityTest` 固化错误行为 | .NET | 0.2 人日 |
| 1 | [P0-1](#p0-1) `normalizeNamespace` 补长度校验 | Dart | ~6 行 |
| 2 | [P0-2](#p0-2) `"default"` 别名归一化 | Dart | 1 行（需迁移说明） |
| 3 | [P0-3](#p0-3) errno 判定改为裸码 5/32/33 | Go | 1 行 |
| 4 | [P1-19](#p1-19) `.tmp` 晋升失败必须让 `Open` 失败 | Go / Kotlin | 各 3 行 |
| 5 | [P1-26](#p1-26) `checkWritable()` 收口到统一写入口 | Kotlin | ~10 行 |
| 6 | [P1-3](#p1-3) 加载期校验 `NsLen ≤ 255` | 全部 5 门 | 各 3 行 |
| 7 | [P0-5](#p0-5) 注册表死条目驱逐 | .NET | ~8 行 |
| 8 | [P0-6](#p0-6) `Open` 失败路径 `Dispose` | .NET | 3 行 |
| 9 | [P1-23](#p1-23) 定时器移到构造之后 + `catch` 里 cancel | Dart | ~5 行 |
| 10 | [P1-17](#p1-17) `Snapshot.Release` 改 `Interlocked` 化 | .NET | 3 行 |
| 11 | [P1-15](#p1-15) `ReadOnly` 配置冲突改抛异常（不得静默升权） | .NET / Rust / Kotlin | ~0.5 人日 |
| 12 | [§7.2](#s-engine-cost) `EngineMode` 字段注释加硬警告 + README 补实测数据 | 全部 5 门 | 0.3 人日 |

**验收标准**：每项补一条回归测试。批次一完成后应新增至少 12 条测试，其中**必须包含**「模块 A `Open` → 模块 B `Open` 同名路径 → B `Dispose` → A 仍可读写」（[P0-4](#p0-4) 的验收用例，当前完全缺失）。

### 批次二：所有权与生命周期模型（预计 2~3 天，需先决策）

| 顺序 | 项 | 内容 |
| :--- | :--- | :--- |
| 13 | **共享实例所有权模型决策（ADR 级）** | 见 [P0-4](#p0-4) 的"选项 A / 选项 B"取舍；**必须先在 `API-V1-FREEZE.md` 更新幂等语义**，再改代码。五语言需同批对齐（.NET / Go / Dart / Kotlin 都有同构问题） |
| 14 | [P1-18](#p1-18) `CkvDb` 终结器 + `LockFile` 改 `SafeFileHandle` | .NET |
| 15 | [P1-12](#p1-12) 统一 `CkvDb` 关闭后读语义（二选一）+ 补读路径测试 | .NET |
| 16 | [P1-13](#p1-13) 删掉 `Dispose()` 中的 `_rwLock.Dispose()` | .NET |
| 17 | [§7.4](#s-ext-ergonomics) `db.Backup(dst)` 定为唯一推荐写法 + 可选静态 `BackupFile` | .NET + README |

### 批次三：契约一致性（预计 3~5 天）

| 顺序 | 项 | 内容 |
| :--- | :--- | :--- |
| 18 | 错误体系归一 | 5 语言统一到 API-SPEC §4 的 10 个错误；补 `IoError`（.NET/Dart）、`InvalidNamespaceError`（Kotlin/Dart）、`KeyTooLargeError`（Kotlin） |
| 19 | `put_bytes` 拷贝契约 | .NET/Go/Kotlin/Dart 统一在入口 `bytes.Clone(val)`；5 语言文档明确所有权（[P1-30](#p1-30)） |
| 20 | 补 API 缺口 | `isClosed`（4 门）、`backup`/`iterPrefix`（Kotlin）、Dart `Entry` 9 个守卫方法 |
| 21 | `scanRange` 增补 `limit` / 反向 / 惰性变体 | **增补而非替换**（缓冲式语义是规范刻意选择，见 [§7.5](#s-rejected)） |
| 22 | `ns` 省略重载 + `Watch` 粒度过载 | 5 语言同步 |
| 23 | Kotlin `clear()` 语义修正 | 改为 `clearNamespace`（保留 ns），或至少标记为 `@Deprecated` 并加醒目警告 |
| 24 | `KeyChangeOp` 四值 ABI | Kotlin（当前只有 2 值，且 drop 发 DELETE 事件）、Dart 对齐 |
| 25 | `iterPrefix`/`ScanRange` 统一过滤非法 UTF-8 键 | .NET / Go / Dart / Kotlin |
| 26 | 锁模型 ADR + 修订 DESIGN-SPEC §4.1.2 / §5.1.1 | 删除或标注废弃 32 字节协议与 `forceUnlock`（[P1-4](#p1-4)） |
| 27 | 事件派发顺序与异常传播语义写进规范 | [P1-39](#p1-39) |

### 批次四：前台写路径重构（预计 5~8 天，收益最大）

| 顺序 | 项 | 内容 |
| :--- | :--- | :--- |
| 28 | 前台 Immediate 路径改为两阶段提交 | 5 语言同构改造（见 §6.2），消除 [P1-29](#p1-29) |
| 29 | 存活快照的 CoW 粒度细化 | 至少做到"同一写事务内每个命名空间只克隆一次"（解决 .NET 的 O(N²)，[P1-1](#p1-1)） |
| 30 | `onWarning` 移出写锁 | 5 语言，复用 watcher 的"快照后派发"模式（[P1-33](#p1-33)） |
| 31 | 提交失败后的脏标记与内存回滚策略 | 统一 [P0-7](#p0-7) / [P1-6](#p1-6) 的失败语义 |
| 32 | `FlushAsync` / `BackupAsync` 一等公民签名 | 见 [§7.3](#s-async-correction) 的优先级排序 |
| 33 | CRC32 硬件加速（可选） | Go 走 `hash/crc32`（零依赖、有 SSE4.2 加速），Rust 走 `crc32fast`（需评估跨语言一致性） |

### 批次五：CI 与工程化（预计 2~3 天）

| 顺序 | 项 | 内容 |
| :--- | :--- | :--- |
| 34 | Go / Kotlin / Dart 进入 PR 门禁 | |
| 35 | 静态分析进 CI | `go vet` + `gofmt -l`、`dart analyze` + `dart format`、`dotnet format`、Gradle lint |
| 36 | Go 测试加 `-race` | 按 DEVELOPMENT-GUIDE 自身的要求 |
| 37 | `.NET` 砍掉 `netstandard2.0` 或补测试矩阵 | [P1-16](#p1-16) |
| 38 | 补打包元数据 | Dart pubspec 全套字段 + LICENSE；Kotlin `api` + Android 目标；.NET `PackageValidation` |
| 39 | 依赖安全扫描 | `cargo audit` + Dependabot |

### 批次六：文档债清理（预计 1 天）

修订 `API-SPEC.md` 的 4 处矛盾；同步 `KV-DB-DESIGN-SPEC.md` §4.1.2/§5.1.1/§3.4；修正 3 个语言 README 的技术描述与示例代码；在 README 增加"SnapshotAtomic 仅适合 <2MB，频繁写必须切 AppendOnlyLog"的**硬警告**与"存活快照会拖慢写"的说明；补 `Int32` 便捷重载与 `Ckv.Serialization` 扩展包提案（[§7.5 B](#s-rejected)）。


<a id="s-appendix"></a>
## 附录 A：需要进一步确认的项

以下项在本次审查中未能得出确定结论，列出确认方法供后续跟进：

| # | 待确认项 | 确认方法 |
| :--- | :--- | :--- |
| A-1 | **Rust 同进程并发 `Open` 是否真无缺陷**：本次探针 8 线程未复现失败（Ok=8/8），但线程可能未真正重叠。Go 侧同类问题已被稳定复现为 7/8 失败 | 用 `Barrier` + 加大线程数（64）+ 引入 `sleep(1ms)` 制造真实重叠窗口，重复 100 次 |
| A-2 | **.NET 在 Unix 上对 `FileShare.None` 的 `flock` 是否带 `LOCK_NB`**：若带，锁争用会抛 `IOException` 使重试循环生效；若不带（阻塞式），第二个 .NET 写连接会**阻塞在 `FileStream` 构造函数里，完全绕过 `LockTimeoutMs`** | 在 Linux（WSL）上用 `FileShare.None` 打开同一文件两次，观察第二个是否立即返回；或读 `System.IO.FileSystem` 的 `FileStream.Unix.cs` |
| A-3 | **`.NET netstandard2.0` + `File.Replace` 在 Unix 上的实际可用性边界**（P1-22 的具体版本边界） | 在 .NET 6 运行时加载 netstandard2.0 程序集并触发轮转，观察是否抛 `PlatformNotSupportedException` |
| A-4 | **Kotlin 是否使用 `java.nio.file`（API 26+）**，从而在 Android 低版本上崩溃（E-2 的前提） | grep `Storage.kt`/`Format.kt` 中的 `java.nio.file` / `Files.` / `Paths.` 导入 |
| A-5 | **Kotlin 内核锁在 Linux 上用 `fcntl` 记录锁**，而 `fcntl` 锁是**进程级**的：同进程内第二个 fd 关闭会意外释放锁 | 当前实现全程持有 `raf`，理论安全；需在目标平台实测两次 `CkvDb.open` 同路径的行为 |
| A-6 | **Kotlin `NamespaceStore` 在 WAL 乱序重放下的实际退化程度**（P2-39 推算是 O(n²)） | 用 10 万+ 条乱序 WAL 压测 |
| A-7 | **Kotlin 拒绝零长键（`keyLen < 1`）与 Rust `validate_kv_lengths` 不一致**，需确认哪个是意图 | 跨语言 golden vector 加一个"空键"用例 |
| A-8 | **Rust `pending_apply` 后台任务在 `close()` 时是否完全排空**：`Drop for DbInner` 的 `join()` 在析构函数中执行，若某个 Clone 持有者在 join 后才 drop 会发生什么 | 代码审查未发现路径，需构造测试 |

---

## 审查方法说明与可信度声明

1. **本报告的全部 P0 结论均有第一手实测证据**（探针 1~12），探针代码已清理（`temp/` 未纳入版本控制）。
2. **P1/P2 结论基于逐行代码阅读**，每条都标注了精确的文件行号与具体代码行为，可独立复核。
3. **一处假设被证伪并如实记录**：探针 1（Rust 同进程并发 `Open`）未能复现预想的缺陷，已在[附录 A](#s-appendix)中列为待确认，**未**将其计入缺陷清单。
4. **一处常见误解被实测修正**：AOL + `Manual` 模式实际能抵抗进程崩溃（探针 9），README 的相关表述对 AOL 不准确。
5. **一处自身缺陷被实测修正**：本次静态审查把 `.NET` 的生命周期问题定位为"Dispose 竞态"与"注册表死弱引用"（P0-5/P0-6），**未识别真正根因是"共享实例没有所有权模型"**。这一修正来自合并外部反馈（[§1.1.1](#s-field-evidence)）——已如实记录在 [P0-4](#p0-4) 与 [§2.3](#s-counts) 中，作为本报告已知的方法论局限。
6. **外部反馈的合并是经过复核而非照单全收**：9 条反馈中 3 条被提升为 P0/P1、2 条部分采纳、1 条归因错误（[§7.3](#s-async-correction)）、1 条已过时、2 条驳回（[§7.5](#s-rejected)）。**本报告以代码证据为准，反馈只提供现场与使用视角。**
7. **缺陷编号已做完整性校验**：P0 共 7 项、P1 共 40 项（1~40 各出现且仅出现一次），全部使用显式 `<a id>` 锚点，文档内 26 处 P0/P1 交叉引用经脚本校验**零失效**。
8. 本报告**未修改任何生产代码**。所有探针、临时文件均位于 `temp/probe/`，已在审查结束后删除。
