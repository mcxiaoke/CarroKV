# CKV 生产就绪度全面评审报告

- **评审日期**：2026-10-17 12:26:33（本机时间）
- **评审范围**：项目文档 + Rust / .NET 两套实现（Go / Dart / Kotlin 按要求忽略）
- **评审问题**：功能、可靠性、性能是否达到同类优秀嵌入式 KV 库的平均水准？是否可以投入生产应用？
- **结论**：**不可投入生产。** 可靠性核心机制接近同类水准，但存在 4 个 P0 阻断缺陷；写性能落后同类 2~3 个数量级，且劣化由架构决定、无法通过调参弥补。
- **文档定位**：本文件为评审记录，**不修改** `KV-DB-DESIGN-SPEC.md`（v3.2 磁盘格式契约）与 `API-V1-FREEZE.md`（API 冻结基线）。文中对规范与代码不一致的指控，均为"记录待裁决"，不代表已授权变更。

---

## 目录

1. [总体结论](#一总体结论)
2. [评审方法与证据基线](#二评审方法与证据基线)
3. [做得好的地方](#三做得好的地方确实达到同类库水准)
4. [P0 阻断生产的问题](#四p0--阻断生产的问题)
5. [P1 严重问题](#五p1--严重问题)
6. [P2 其他问题](#六p2--其他问题)
7. [与同类库对比](#七与同类库对比)
8. [生产可用性判定](#八生产可用性判定)
9. [建议路线](#九建议路线按优先级)
10. [复现方法](#十复现方法)

---

## 一、总体结论

| 维度 | 判定 | 依据 |
|---|---|---|
| **功能** | 达到同类中等偏下 | 缺流式迭代器、TTL、版本迁移、压缩、加密；强类型与批内读己所写优于多数同类 |
| **可靠性** | 核心机制接近同类水准，落地路径有缺陷 | 崩溃安全 / 损坏分类 / 轮转原子性做得比多数实现扎实；但有 3 条真实的数据损坏或不可用路径 |
| **性能** | **远低于**同类水准 | 读性能达标；写性能落后 2~3 个数量级，且写放大随库大小线性增长（架构决定） |
| **可生产性** | ❌ **否** | 4 个 P0 阻断项；零 CI；无 fuzz；不可发布的包元数据 |

**但它是一个边界清晰的可用组件**：在"读多写少、总数据 < 2MB、配置/凭证/偏好类"这一狭窄场景内，默认引擎模式尚可工作。问题在于 README 与设计文档把它定位为"工业级"通用引擎，而默认配置在这个定位下不可用。

---

## 二、评审方法与证据基线

### 2.1 评审对象

| 类别 | 文件 | 规模 |
|---|---|---|
| 设计规范 | `docs/KV-DB-DESIGN-SPEC.md` (v3.2) | 421 行 |
| API 冻结 | `docs/API-V1-FREEZE.md` | 300 行 |
| 对外说明 | `README.md`、`src/dotnet/README.md` | 107 / 212 行 |
| Rust 核心 | `src/rust/src/db.rs`、`format.rs`、`lock.rs`、`options.rs`、`types.rs` | 2407 / 456 / 98 / 152 / 215 行 |
| .NET 核心 | `src/dotnet/src/Ckv/Db.cs`、`LockFile.cs`、`Options.cs` 等 | 2761 / 89 / 62 行 |
| 测试 | Rust 26 个测试文件；.NET 22 个测试类 | ~90 + 99 个用例 |

### 2.2 执行的验证

| 验证 | 结果 |
|---|---|
| `cargo test --release`（Rust 全量） | **全部通过，0 失败**（含 doc-test） |
| `dotnet test -c Release`（.NET 全量） | **99 / 99 通过** |
| `dotnet build -c Release` | 0 错误 0 警告 |
| 官方 `.NET` benchmark（`Ckv.Benchmark`） | 已完整执行并采集数据 |
| 自建探针 9 组（`temp/probe`、`temp/netprobe`） | 覆盖写放大、锁矩阵、读延迟、快照 CoW、内存放大、插入复杂度 |
| `cargo clippy --all-targets` | 21 条警告 |

> 探针源码位于 gitignored 的 `temp/probe/`、`temp/netprobe/`，复现命令见[第十节](#十复现方法)。

### 2.3 对照库

`temp/github/` 下已有：FASTER、fjall、garnet、hive_ce、KeyDB、Lightning.NET、redb、rocksdb、rust-rocksdb。
网络对照基准采用 redb 官方 `lmdb_benchmark` 对比表（Ryzen 9950X3D + Samsung 9100 PRO NVMe）。

### 2.4 测量环境与有效性声明

- Windows 10 / 16 逻辑核 / .NET 8.0.32 / rustc 1.96.0 / **虚拟机**（Defender 文件过滤会放大 `rename` 成本）。
- **绝对数值随机器变化**，不应直接与他机数据横向比较。
- 但以下三项是**架构特征**，换机器不改变结论：
  1. 单次写耗时随库大小**线性增长**；
  2. 写放大随库大小**线性增长**；
  3. 读延迟 P99 被写路径**支配**。

---

## 三、做得好的地方（确实达到同类库水准）

以下 8 项建议在后续重写中保留。

### 3.1 两代原子轮转的纪律性

`src/rust/src/db.rs:2499` `rename_with_retry`：

```rust
// 目标绝不被预先删除（C5）：rename 在 POSIX 与 Windows 上都已支持覆盖替换，
// 先 remove_file 只会打开一个 .db 与 .bak 同时不存在的窗口——
// 该窗口内崩溃将不剩任何一代。
```

这是许多同类实现（LMDB 之外的多数自研实现）会犯的错误。配套单测 `rename_never_removes_the_destination` 锁定该不变量，且测试注释明确写出"源文件故意缺失，使 rename 必定永久失败"以确保兜底路径也无法掩盖问题。**测试设计质量高于平均线。**

### 3.2 D1 决策：`.bak` 定位为离线副本而非自动回滚

`db.rs:1883-1887`：

```rust
// 损坏的主文件。决策 D1：.bak 保存的是上一次轮转 *之前* 的一代，
// 用它替换主文件会静默回滚此后写入的全部数据。
// 仅在调用方显式选择时使用；否则错误信息中报告该副本位置供运维恢复。
```

多数朴素实现会把 `.bak` 当作自动回滚机制，从而在一次介质损坏后静默丢掉最近一整代写入。这里默认 `auto_recover_from_backup = false`，并在错误消息中给出 `.bak` 路径交由人工恢复。**这是成熟的设计判断。**

### 3.3 D2 决策：压实失败不得让写失败

`db.rs:2086-2091`：

```rust
// 压实失败绝不让调用方的写失败（除非 strict_compaction）：
// 该变更已经持久化，报错会诱导调用方重试一个已成功的写，
// 而 increment（读-改-写）会因此重复施加 delta（决策 D2）。
```

并配套实现了指数退避（64 → 4096 写）、失败计数、`close()` 时兜底重试、`onCompactionFailed` 告警。推理链条完整且正确。

### 3.4 C4 损坏三分法

`format.rs:314-330` 的判据：

| 情形 | 判据 | 处理 |
|---|---|---|
| 撕裂尾 | 文件结束于最后一帧之内（短帧头 / 载荷越过 EOF） | 截断到上一帧边界，保住之前所有完整帧 |
| 未写入尾 | 帧头完整但校验失败，且损坏区**全为零字节** | 同上（部分文件系统崩溃后的填充方式） |
| 介质损坏 | 完整帧 + 载荷 CRC 不符 | **抛 `Corrupted`，绝不静默截断**——损坏点之后可能仍有完整帧 |

核心论证"完整帧头存在 ⇒ 这些字节确实被写过 ⇒ 不可能是撕裂写"是严密的，并明确引用 RocksDB 默认 WAL 恢复模式对尾部零填充的同类容忍作为先例。这比"一律截断"或"一律报错"都更专业。注释同时说明 `allow_tail_truncation = false` 时禁用全部自愈（`.bak` 作为恢复源永不被截断，G1）。**这是本项目质量最高的一块。**

### 3.5 C9：用内核锁取代时钟判活

`lock.rs:8-29` 文档注释说明了取舍：基于锁文件内容（`PID|UUID|CreateTimeMs`）的时钟判活会把**活着的持有者**误判为僵死（一次长 GC、换页或大快照即足够），从而在同一份库上出现两个写者；崩溃持有者还会留下需要后续进程"推理"的锁文件。改用 `flock` / `LockFileEx` 后由内核持有，进程退出即释放。**对标 bbolt（`flock(db, !readOnly)`）与 LMDB（fcntl 咨询锁），这是正确的工程决策。**

> 但该决策的执行存在缺陷，见 [P0-3](#p0-3-rust-侧-locktimeoutms-在跨语言场景被静默忽略可复现) 与 [P0-4](#p0-4-rust-close不释放跨进程锁与规范和-net-都不一致)。

### 3.6 CoW 快照：正确且真正 O(1)

- Rust：`Arc<BTreeMap<Vec<u8>, Arc<BTreeMap<..>>>>` + `Arc::make_mut`，快照仅克隆外层命名空间表。
- .NET：`_snapshotCount` 门控，`GetOrCreateStoreForWrite` 仅在有存活快照时克隆 `NamespaceStore`。

实测 `snapshot()` = **130 ns**（50k 键），符合 README "纳秒级 $O(1)$ 瞬时快照创建"的宣称。快照读完全无锁。

### 3.7 `live_payload_bytes` 的 O(1) 增量账本

`db.rs:143-206` 定义了 `mem_put` / `mem_delete` / `mem_drop` / `mem_clear` 四个方法作为**内存变更的唯一收口**，并在注释中写明"直接触碰 `data` 会静默使账本失步，进而使死键比例压实触发器失准"。

自适应压实触发器需要 $\Sigma(len(key)+len(value))$。若每次写遍历全库重算，在日志超过自适应基线后单次写会退化为 O(N)。这里用 O(1) 增量维护规避，并规定"绕过 `put/delete` 的批量装载路径必须在装载完毕后重算一次"（.NET `RecalculateLiveBytes`）。**这是很干净的工程判断。**

### 3.8 测试纪律

回归用例与缺陷编号一一对应（C1–C21、D1、D2、G1、T10），覆盖黄金向量字节级比对、跨语言互通、故障注入、写原子性、旋转原子性、锁协议。`tools/gen_testdata.py` + `testdata/MANIFEST.txt` 提供可复现的黄金数据集。

---

## 四、P0 —— 阻断生产的问题

### P0-1 默认引擎模式每次写 O(N)，写放大最高约 78 万倍

**默认配置**：`SnapshotAtomic + Immediate`。每次 `put` 将全库序列化 → `fsync` → 两次 `rename`（`.db→.bak`、`.tmp→.db`）。

**实测（Rust，默认选项，无探针干预）**：

| 库大小 | 单次 put | 吞吐 | 写放大 |
|---:|---:|---:|---:|
| 14,760 B | 2.08 ms | 480.5 ops/s | 115x |
| 147,060 B | 2.77 ms | 361.5 ops/s | 1,149x |
| 1,470,060 B | 8.90 ms | 112.3 ops/s | 11,485x |

**实测（.NET 官方 benchmark，`SyncOnWrite = true`，物理 fsync）**：

| 库大小 | 单次 put | 吞吐 | 每次 128B 更新写入磁盘 |
|---:|---:|---:|---:|
| 1.02 MB | 5.36 ms | 187 ops/s | 1,039.4 KB |
| 9.78 MB | 37.28 ms | 27 ops/s | 10,019.7 KB |
| **98.27 MB** | **334.96 ms** | **3 ops/s** | **100,632.5 KB** |

**对照**：redb / LMDB / fjall / rocksdb / bbolt 的单键写只触碰 $O(\log N)$ 个页或一个 SST，与库总大小无关。**架构上就不在同一量级。**

**关键细节**：官方 benchmark 中 `SyncOnWrite = false`（仅 OS 缓存）与 `true`（物理 fsync）几乎无差异（5.83 vs 5.36 ms；328 vs 335 ms），说明瓶颈**不在 fsync**，而在全量序列化 + 两次 `rename` 的固定开销。项目自身文档也承认这点（`KV-DB-DESIGN-SPEC.md` §4.4："按同口径归因 WAL 每写约 1.9ms 的固定开销"）。

**`AppendOnlyLog` 模式的实测**（`SyncMode::Immediate` + fsync，关闭自动压实以隔离变量）：

| 库大小 | 单次 put | 吞吐 |
|---:|---:|---:|
| 100 | 0.642 ms | 1,559 ops/s |
| 1,000 | 0.639 ms | 1,566 ops/s |
| 10,000 | 0.643 ms | 1,556 ops/s |
| 50,000 | 0.650 ms | 1,538 ops/s |

**性能与库大小无关（写放大 O(1)）——这是 WAL 模式的正确行为**，但绝对值仍只有约 1,500 ops/s，且每次写都 open/close 句柄（§4.4 的 C15 决策）。README 指出"需要吞吐时请显式选择 `AppendOnlyLog`"，但这要求每个调用方都正确理解并显式切换两个耦合选项。

**定级理由**：默认值即产品形态。一个默认配置下 3 ops/s 的 KV 库不能称为生产可用，且**这不是调参问题**——`compaction_threshold_bytes` 等参数都无法改变 O(N) 写路径。

---

### P0-2 写者独占全局写锁，读延迟被拖到几十~上百毫秒

`db.rs:2234-2266` `apply_mutation` 在**持有全局 `RwLock` 写锁期间**完成：全量序列化 + `sync_all()` + 两次 `rename` + 目录 fsync。读操作全部被阻塞。

**实测（5 MB 库，单次写 22–26 ms，独立读线程）**：

| 语言 | 读 P50 | 读 P99 | 读 MAX |
|---|---:|---:|---:|
| Rust | 1 µs | **24,175 µs (24.2 ms)** | 30,997 µs (31.0 ms) |
| .NET | 168 µs | — | **111,350 µs (111 ms)** |

.NET 官方 chaos 测试（4 线程 / 10,000 混合操作）：

```
Overall Throughput: 1,682 ops/sec
[Reads ] P50: 1.40 µs | P95: 10,477.00 µs | P99: 29,025.10 µs | Max: 61,054.90 µs
[Writes] P50: 2,190.90 µs | P95: 6,757.80 µs | P99: 10,510.10 µs
```

**影响**：任何"读写混合并发"的服务，其读 p99 完全由写路径决定。读性能宣传（百万 QPS、纳秒级）在并发场景下不成立。

**文档偏差**：README 的"无锁只读快照隔离"一节只声明了**快照读**不阻塞（这一点是成立的）。但整体表述容易让读者推断常规读也不被写阻塞——实测并非如此。

---

### P0-3 Rust 侧 `lockTimeoutMs` 在跨语言场景被静默忽略（可复现）

**根因** `src/rust/src/lock.rs:38-80`：

```rust
pub fn acquire(db_path: &Path, options: &DbOptions) -> Result<Self> {
    let lock_path = PathBuf::from(format!("{}.lock", db_path.display()));
    // ↓↓↓ 一次性调用，位于退避重试循环之外，`?` 直接把 IO 错误抛出
    let file = OpenOptions::new()
        .read(true).write(true).create(true).truncate(false)
        .open(&lock_path)?;

    // ↓ 下面的指数退避循环只包裹 try_lock()，管不到上面那次 open
    loop {
        match Self::try_lock_once(&file, exclusive) { ... }
    }
}
```

.NET 侧 `LockFile.cs:56-64` 以 `FileAccess.ReadWrite` + `FileShare.None` 持有 `<db>.lock`。在 Windows 上，`FileShare.None` 意味着**任何其他 `CreateFile` 对该路径都会失败**（`ERROR_SHARING_VIOLATION` / WinError 32）。Rust 进程根本走不到 `try_lock()`。

**实测锁矩阵**（`lock_timeout_ms = 2500`）：

| 持有方 | 竞争方 | 实测结果 | 判定 |
|---|---|---|:--:|
| Rust | Rust | 等满 2,569 ms → `LockedError` | ✅ |
| .NET | .NET | 等满 2,506 ms → `LockedError` | ✅ |
| Rust | .NET | 等满 2,515 ms → `LockedError` | ✅ |
| **.NET** | **Rust** | **18 ms 立即 `IoError: os error 32`** | ❌ |

反向用例正常（Rust 持有 → .NET 竞争等满 2,515 ms），说明问题单点在 Rust 的 `open()` 位置。

**三重后果**：

1. **错误类型错误**：调用方收到 `DbError::Io` 而非 `DbError::Locked`，直接违反 `API-V1-FREEZE.md` §1.3 "错误类型必须一一对应"。
2. **`lockTimeoutMs` 完全失效**：参数在最需要它的场景下静默无效。
3. **击穿核心卖点**：本项目的首要差异化特性是跨语言字节级互通，而跨语言**并发访问**场景直接失败。

**测试盲区**：`src/rust/tests/lock_protocol_test.rs` 的跨进程用例 `t10_live_holder_blocks_and_a_killed_holder_frees_the_lock` 通过 `Command::new(current_exe)` 拉起的是**同一个测试二进制**（Rust）。`lock_holder_child` 子进程也用 Rust 实现。**从不测试异语言持有方**，因此该缺陷在全部测试中逃逸。

---

### P0-4 Rust `close()` 不释放跨进程锁（与规范和 .NET 都不一致）

`db.rs:483-525` `close()` 只做两件事：唤醒并 join 后台刷盘线程、刷残留脏数据、重试待决压实。**它从不触碰 `self.lock`。** 锁仅在 `impl Drop for DbInner`（`db.rs:209-238`）中随 `FileLock` 析构释放。

**实测**：

```
持锁进程调用 close() 后仍存活 → 竞争方等满 2,566 ms，报 LockedError
持锁进程完全退出后          → 竞争方 0 ms，OPEN_OK
```

**三重不一致**：

1. **与规范冲突**：`KV-DB-DESIGN-SPEC.md` §5.2.2 `close()` 条目明写"释放内存资源与跨进程文件锁"。
2. **与 .NET 冲突**：`src/dotnet/src/Ckv/Db.cs:2552-2554` `Dispose()` **确实**调用 `_connectionLock?.Dispose()`。
3. **与文档冲突**：README 称 `close()` 是 `Deferred`/`Manual` 模式下退出前必须调用的契约。

**影响**：调用方按文档调用 `close()` 后，若进程未立即退出（例如需要切换到另一个库、或者在长生命周期进程中重开），其他进程将持续得到 `LockedError`。跨语言语义分叉还意味着：一个 .NET 程序 `Dispose()` 后可被 Rust 打开，反之不可以。

---

## 五、P1 —— 严重问题

### P1-1 .NET 轮转兜底路径会造成文件损坏（Rust 侧是对的）

`src/dotnet/src/Ckv/Db.cs:1337-1355`：

```csharp
private static void AtomicReplaceWithRetry(string src, string dst)
{
    int[] backoffs = { 5, 15, 30, 60, 120 };
    for (int attempt = 0; attempt < backoffs.Length; attempt++)
    {
        try { AtomicReplace(src, dst); return; }
        catch (IOException) { if (attempt == backoffs.Length - 1) break; Thread.Sleep(backoffs[attempt]); }
    }
    // ↓ 兜底：重试耗尽（共约 230 ms）后
    File.Copy(src, dst, true);              // CREATE_ALWAYS —— 先截断目标，再拷入
    try { File.Delete(src); } catch { }
}
```

注释声称"非原子，但目标内容始终完整，不会留下『两代备份都不存在』的窗口"。

- "两代都不存在"这一点**成立**；
- 但 **"目标内容始终完整"不成立**——`File.Copy` 以 `CREATE_ALWAYS` 打开目标（先截断），拷贝过程中目标是一个**部分文件**。若此时进程被杀（`TerminateProcess` / OOM kill / 移动端 LMK），`.db` 或 `.bak` 就是截断的半个文件。

**触发条件与风险耦合**：该路径只在 `rename` 被占用时才进入——占用者通常是杀毒软件、文件索引器，或尚未关闭句柄的读者。而"进程被杀"与"文件被外部程序占用"在真实环境中高度相关（安装/更新/安全扫描期间）。

**Rust 侧正确**（`db.rs:2518-2546`）：先把内容拷到 `<dst>.stage` → 校验拷贝长度与源一致 → 再 `rename(stage, dst)` → 失败时保留 staged 副本以确保两代仍在。

**建议**：.NET 兜底改为与 Rust 同构的 `.stage` + `rename`。

---

### P1-2 没有流式 / 分页迭代器

| API | Rust | .NET | 行为 |
|---|---|---|---|
| `scan_range` | `db.rs:1128` | `Db.cs:2335` | 遍历全命名空间，**全部物化**为 `Vec<Entry>`，逐条克隆 value、逐条构造 String |
| `iter_prefix` | `db.rs:1167` | `Db.cs:1794` | 同上（`RangeIter` 是对已物化 `Vec<Entry>` 的包装，`DoubleEndedIterator` 亦然） |

`API-V1-FREEZE.md` §2.6 承认这是"刻意选择"，理由是"真空惰性迭代需要让迭代器持有存储指针，会把 CoW 生命周期管理泄漏到公共 API"。

**代价是功能缺口，不只是性能问题**：**大命名空间无法做有界范围扫描**。实测 200,000 键一次性 `ScanRange` 会把全部条目与全部 value 副本物化到堆上。redb / LMDB / fjall 均提供真正的迭代器。

注释建议"超大命名空间请用 `scan_range` 分页"，但 `scan_range` 本身也是全量缓冲，分页只在调用方反复调用时才成立，且每次都要遍历并跳过前 N 项（`O(\log N + N_{\text{start}})`）。

---

### P1-3 快照序列化不流式，内存放大严重

**Rust** `db.rs:2362-2424` `serialize_snapshot_to_tmp`：

```rust
let mut sorted_entries: Vec<RawOpEntry> = Vec::new();
for (ns_bytes, ns_map) in data.iter() {
    for (key_bytes, (vtype, val_bytes)) in ns_map.iter() {
        sorted_entries.push(RawOpEntry { ..., key: key_bytes.clone(), value: val_bytes.clone() });
    }
}
```

先把**每一个 key 和 value 全量克隆**到中间 `Vec`，再逐帧编码（`BatchFrameWriter::write_frame` 内部再 `Vec::new()` 累加成整帧载荷，最大 64 MB）。

**.NET** `Db.cs:1089-1185` `SerializeSnapshotBytes`：整库进 `MemoryStream` → `ToArray()` 得到完整 `byte[]` → 每帧 `currentPayload.ToArray()` 再拷一份 → 拼接进主 `MemoryStream`。**峰值同时持有 3 份以上副本。**

**实测**：100 MB 库（100,000 键 × 1 KB），仅 `Open` 完成后托管堆即为 **222.5 MB = 文件的 2.23 倍**；序列化过程再叠加瞬时副本。

**影响**：1 GB 库 → 峰值 2~3 GB 内存。README 承诺的 `< 2MB` 场景无碍，但 `AppendOnlyLog` 的自动压实会在库长大后撞上此问题。移动端 / 32 位运行时直接 OOM。

---

### P1-4 .NET 在任何平台都做不了目录 fsync

`Db.cs:1280-1330` `TryFsyncParentDirectory`：

- **Windows**：直接 `return`（注释：NTFS 元数据由操作系统保证）。
- **.NET 8 on Linux**：`File.OpenHandle(dir, ...)` 抛 `UnauthorizedAccessException` → 置 `_dirSyncUnavailable` 并降级为 no-op，仅发一次 `onDirSyncUnavailable` 告警。
- **netstandard2.0 / Unity**：无托管 API，no-op。

代码中**诚实地记录了实测结论**（"2026-10-17 .NET 8.0.426 / Linux ext4 实测"），并说明不使用 P/Invoke 以坚守"零 Native 依赖"红线——这个取舍本身是合理的。

**后果**：**Linux 上 .NET 的 rename 轮转不具备掉电持久性保证，而 Rust 有**（`db.rs:2560-2567` `fsync_parent_dir` 在 Unix 生效）。即同一份 `.db` 在 Linux 上由 .NET 写入时比由 Rust 写入时更容易在掉电后丢失最后一次轮转。这构成跨语言可靠性分叉，与 P0-3 共同构成"跨语言互通只在非并发、非掉电敏感场景成立"的限制。

---

### P1-5 .NET `Snapshot` 无终结器，泄漏即永久降级

`Db.cs:1981-2013`：

```csharp
public Snapshot CreateSnapshot() { ... lock (_snapshotLock) { _snapshotCount++; } ... }
internal void ReleaseSnapshot() { lock (_snapshotLock) { if (_snapshotCount > 0) _snapshotCount--; } }
```

`_snapshotCount` 只在 `Snapshot.Dispose()`（`Db.cs:3026-3033`）中递减。`Snapshot` 类**没有 finalizer、没有 `SafeHandle`、没有弱引用**。

**后果**：调用方在 `using` 之外创建快照（或异常路径未释放）→ `_snapshotCount` 永久 > 0 → `GetOrCreateStoreForWrite`（`Db.cs:2015-2031`）此后**每次写都克隆整个 `NamespaceStore`**。静默、永久、无任何告警或诊断出口。

在本机 30 ms/op 的默认模式下该降级被序列化开销掩盖（实测 1.0x），因此**无法通过现有 benchmark 发现**——只有当写路径变快后才会显形。这正是它危险的地方。

Rust 侧对应结构（`Arc` + `Arc::make_mut`）由引用计数自动回收，无此问题。

---

### P1-6 二进制格式解析器零 fuzz

`src/rust/src/format.rs::load_with_recovery` 与 `src/dotnet/src/Ckv/Db.cs::ParseDatabaseBytes` 处理的是**不可信输入**：用户数据文件、可能损坏的介质、外部构造的文件。这是全项目风险最高的攻击面（越界读取、整数溢出、panic、OOM）。

现有防护：手写测试（`fault_injection_test.rs` 6 例、.NET `FaultInjectionTest.cs` 7 例）+ 全量 CRC 校验 + 边界检查（如 `format.rs:73-75`、`:109-111` 的长度前置检查）。

**缺失**：无 `cargo-fuzz`（对照：redb 仓库带 `fuzz/` 目录）、无 Miri、无 ASan/UBSan、无覆盖率报告。

由于代码中有 `buf[*offset + 2..*offset + 4].try_into().unwrap()` 等模式（前置长度检查后是安全的，但依赖不变式而非类型系统），fuzz 是性价比最高的补强手段。

---

### P1-7 MSRV 声明错误，包元数据不可发布

**声明**：`README.md` 第 37 行 "`rustc 1.70+`"；`KV-DB-DESIGN-SPEC.md` §1.1 同义。

**实际**：`src/rust/src/lock.rs:82-88` 使用

```rust
file.try_lock()          // std::fs::File::try_lock
file.try_lock_shared()
```

`File::try_lock` / `File::lock` 随 **`feature = "file_lock"` 在 Rust 1.89.0（2025-08-07）稳定**。因此本 crate 的真实 MSRV 是 **1.89.0**，1.70–1.88 的使用者**无法编译**。

**`Cargo.toml` 缺失字段**：

```toml
[package]
name = "ckv"
version = "0.1.0"     # 仍是 0.1.0，与 API v1.0 冻结状态矛盾
edition = "2021"
authors = ["mcxiaoke"]
description = "..."
# ✗ 无 license        → crates.io 拒绝发布
# ✗ 无 repository     → 无法关联源码
# ✗ 无 rust-version   → 消费者无法判断兼容性
```

---

### P1-8 零 CI

全仓库**无任何 CI 配置**（无 `.github/workflows/`、无其他 CI 描述文件）。

本项目具有以下特征，正是 CI 最应覆盖的类型：

- 5 种语言实现同一份字节级格式；
- 磁盘格式契约被声明为冻结基线（`KV-DB-DESIGN-SPEC.md` v3.2、`API-V1-FREEZE.md`）；
- 缺陷后果是**静默的数据损坏**而非崩溃；
- 大量回归用例与文档编号（C1–C21、D1、D2、G1、T10）强绑定。

当前全部验证依赖人工执行 `cargo test` / `dotnet test`。黄金向量（`testdata/*.db` + SHA-256）已经具备做 CI 门禁的条件，但没有接线。

---

## 六、P2 —— 其他问题

| # | 问题 | 位置 / 证据 |
|---|---|---|
| P2-1 | **审计链被删**：上一个 commit `3dcb1de` 删除 22 个文档（5,826 行）。README 仍链接已失效的 `docs/CHANGES-20261015.md`、`docs/SNAPSHOT-ISOLATION-DESIGN.md`；`API-V1-FREEZE.md` §3.15 引用已删除的 `DECISION-20261016-wal-recovery-and-compaction.md`。代码注释中大量引用的 **D1 / D1-7 / D1-8 / D1-9 / D2 / C1–C21 / G1 / T10 现在无处可查** | `git show --stat 3dcb1de`；`docs/` 现仅剩 2 个文件 |
| P2-2 | **规范与代码脱节**：`KV-DB-DESIGN-SPEC.md` §4.1.2 仍完整描述已被删除的 32 字节 `PID\|UUID\|CreateTimeMs` 锁协议、时间戳判活与窃取流程，与 C9 的内核锁实现完全不符 | 规范 §4.1.2 vs `lock.rs:8-29` |
| P2-3 | **`scan_max_tx_id` 不校验 CRC**：`format.rs:487-515` 的快速扫描只查 `FrameMagic`，不查帧头 CRC，也不校验 `offset` 是否越界。损坏的 `payload_len` 可令扫描得出错误的 max TxID，从而可能绕过 `precheck_disk_state` 的并发修改检测。属纵深防御缺口 | `format.rs:487-515` |
| P2-4 | **无 TTL / 过期**：Redis、fjall 等同类均提供，CKV 无 | `lib.rs` 导出面 |
| P2-5 | **无压缩、无加密**：仅 CRC32（非抗篡改） | `format.rs` |
| P2-6 | **无版本迁移路径**：仅单一 `FormatReadVer` 字段校验（`format.rs:29-34`），v4 格式无升级通道 | `format.rs:24-44` |
| P2-7 | **无多表 / 类型化 schema**：单一扁平命名空间 + 5 个值类型标记 | `types.rs:20-24` |
| P2-8 | **`Close()` 与 `unwatch()` 语义在文档中表述不一致**：§2.2 说"单纯丢弃句柄**不**注销"，`:952-958` 的 rustdoc 却说"dropping it ... removes the callback" | `API-V1-FREEZE.md:49` vs `db.rs:954-958` |
| P2-9 | **`.gitignore` 忽略 `*.bak`**：与本项目的 `.bak` 语义（上一代数据库）重叠，可能误伤真实备份文件 | `.gitignore` |
| P2-10 | **21 条 clippy 警告**：`needless_return`、`useless_vec`、复杂类型未抽 `type`、未使用函数等 | `cargo clippy --all-targets` |
| P2-11 | **`DbOptions` 无 `forceUnlock`**：规范 §5.1.1 定义了该字段，C9 后失去意义但规范未同步删除 | 规范 §5.1.1 vs `options.rs:23-74` |

---

## 七、与同类库对比

### 7.1 性能

| 维度 | CKV 默认 | CKV `AppendOnlyLog` | redb / LMDB / fjall / rocksdb |
|---|---|---|---|
| 单键持久写（1 MB 库） | 112 ops/s | 1,559 ops/s | redb ≈ 1 ms/写量级（4 GB 库） |
| 单键持久写（98 MB 库） | **3 ops/s** | 1,538 ops/s | 不随库大小劣化 |
| 写放大（98 MB 库） | **≈ 786,000x** | ≈ 1x | $O(\log N)$ 页 / 单个 SST |
| 随机点读 | 1.10 M QPS / P50 400–700 ns | 同 | 同级（内存驻留有序结构） |
| 读延迟 P99（写并发下） | **24–111 ms** | 仍被压实阻塞 | MVCC / 分段写，读不被写阻塞 |
| 前缀扫描（1,000 键） | 0.35–0.43 ms | 同 | 同级 |
| 批量装载 | 线性，≈ 533 K keys/s（Rust 实测 10 K→80 K 规模稳定） | 同 | redb bulk load 17 s（大数据集） |
| 内存放大 | `Open` 后堆 = 文件 2.23x（100 MB 实测） | 同 | 页缓存可配、可 mmap |

**读性能确实达到同类水准**——百万 QPS、亚微秒 P50，这是本项目最强的一项，与 redb/LMDB 的内存有序结构处于同一量级。

**写性能落后 2~3 个数量级，且随库大小线性劣化。** 对照 redb 官方 `lmdb_benchmark` 表（Ryzen 9950X3D + 9100 PRO NVMe），`individual writes` 一行为 redb 920 ms / LMDB 1598 ms / fjall 3488 ms / rocksdb 2432 ms，而 CKV 在 98 MB 库上单次写即需 **335 ms**。

### 7.2 功能

| 能力 | CKV | 同类优秀库 |
|---|:---:|:---:|
| 点查 / 范围扫描 / 前缀匹配 | ✅ | ✅ |
| 批量原子写（纯内存暂存 + 失败回滚） | ✅ | ✅ |
| 批内读己所写（含墓碑遮蔽） | ✅ | ✅（语义更完整） |
| 无锁快照 / MVCC | ✅ 130 ns | ✅ |
| 变更监听（写锁完全释放后派发） | ✅ | 部分 |
| 强类型（int64/float64/bool/bytes/utf8）+ 类型拦截 | ✅ 独有优势 | 多数为不透明字节 |
| **跨语言字节级互通** | ✅ 独有优势（但见 P0-3 / P1-4 限制） | 罕见 |
| **流式 / 分页迭代器** | ❌ | ✅ |
| **TTL / 过期** | ❌ | ✅ |
| **崩溃安全回放** | ✅ **扎实** | ✅ |
| **多进程并发读写** | ❌ 明确不支持 | 部分支持 |
| **真正的 ACID 事务** | ❌ 仅内存批次，无并发冲突检测 | ✅ |
| **schema 演进 / 迁移** | ❌ | ✅ |
| 压缩 / 加密 | ❌ | 部分 |

---

## 八、生产可用性判定

| 目标场景 | 判定 | 依据 |
|---|---|---|
| 桌面 / 移动 App 配置、凭证、偏好（< 2 MB，读多写少或全批量写） | ⚠️ **可用**，但需显式接受 O(N) 写成本 | 1 MB 库下 112–187 ops/s，单写 < 10 ms |
| 会话缓存 / 离线事件日志（1 MB–100 MB，高频单写） | ❌ **不可用** | 必须调用方自行切换到 `AppendOnlyLog` + 理解压实语义；默认配置下 3 ops/s |
| 多线程读写混合的服务 | ❌ **不可用** | 读 P99 被写路径支配至 24–111 ms（P0-2） |
| 多语言进程共享同一份库 | ❌ **不可用** | Rust 侧锁超时与错误类型错误（P0-3）；Linux 上掉电保证不一致（P1-4） |
| 32 位 / 内存受限设备上的中大型库 | ❌ **不可用** | 序列化内存放大至 2.23x 以上（P1-3） |
| 作为通用嵌入式 KV 组件对外提供 | ❌ **不可用** | 综合 P0 + P1 |

**与"同类优秀库平均水准"的差距归纳**：

- **可靠性**：核心机制（崩溃安全、损坏分类、轮转原子性、内核锁）**接近甚至局部优于**同类；但落地路径有 3 条缺陷（.NET 损坏兜底、.NET 无目录 fsync、零 fuzz）**明显低于**生产标准。
- **性能**：读**达到**水准；写**远低于**水准，且差距集中在最关键的单键写路径上。
- **功能**：**低于**水准，缺口集中在流式迭代、TTL、迁移路径，但均非致命。

---

## 九、建议路线（按优先级）

### 阶段一：发布前必修（不修不能发布）

| # | 动作 | 位置 |
|---|---|---|
| 1 | 把 `open(lock_path)` 纳入退避重试循环，并将 WinError 32/33 映射为 `DbError::Locked` | `src/rust/src/lock.rs:45-50` |
| 2 | 补**异语言持有方**的跨进程锁测试（至少 Rust↔.NET 双向） | `src/rust/tests/lock_protocol_test.rs` |
| 3 | Rust `close()` 释放 `FileLock`；或明确文档为"仅 `Drop` 释放"并同步规范 §5.2.2 | `src/rust/src/db.rs:483` |
| 4 | .NET 轮转兜底改为 `.stage` + 校验 + `rename`（与 Rust 同构） | `src/dotnet/src/Ckv/Db.cs:1353` |
| 5 | `Cargo.toml` 补 `license` / `repository` / `rust-version = "1.89"`；修正 README 与规范的 MSRV | `Cargo.toml`、`README.md:37` |
| 6 | 恢复被删除的决策文档（至少 D1 / D2 / C1–C21 / G1 / T10 的定义），修 README 死链 | `docs/` |
| 7 | 加 CI：Linux/macOS/Windows × Rust/.NET × 全测试 + 黄金向量字节比对 | `.github/workflows/` |

### 阶段二：架构方向决策（分水岭）

必须二选一，不存在"再优化一下"的中间态：

**方案 A —— 诚实缩小定位**（成本低，建议优先评估）
- 正式定位为"**小数据快照型配置存储**"。
- 默认值 `SnapshotAtomic + Immediate` **不动**（对 `< 2 MB` 场景默认值是对的）。
- 在 README 与规范中**写死适用边界**：库大小上限建议写明 `< 2 MB`，并注明单写耗时与库大小成正比。
- 删除或改写"工业级"、"可与 rocksdb 比"等暗示通用能力的措辞。
- 补齐 P1 项（迭代器、内存流式化、`Snapshot` 终结器）后即可发布。
- **定位变化需走 ADR 级决策**（规范 §5.1.1.1 第 3 条：改默认值等同 ADR）。

**方案 B —— 补齐存储层**（重写量级）
1. **流式序列化**：`serialize_snapshot_to_tmp` / `SerializeSnapshotBytes` 改为逐帧直接写入文件句柄，消除中间全量克隆（消除 P1-3）。
2. **真正的流式迭代器**：让迭代器持 `Arc<BTreeMap>` 快照引用（CoW 生命周期已在内部管理，泄漏风险可控），提供 `scan_range` 的惰性版本与 limit 参数（消除 P1-2）。
3. **写路径不阻塞读**：WAL 模式常驻写句柄或批量 append；压实改为后台线程 + 原子替换，使读延迟与写延迟解耦（消除 P0-2）。
4. **批量/组提交**：为 `AppendOnlyLog` 提供显式的 `begin_batch_commit()` 语义，把 N 次 fsync 合并为 1 次。
5. 建议按 LSM（对齐 fjall）或页式双写（对齐 redb）重做，参考 `temp/github/` 下的 fjall / redb。

> §4.4 已记录"取消组提交"的决策及其复核结论。该决策的前提是"嵌入式单写者"，本身成立；但若采纳方案 B 的第 4 项，需重开该决策（规范已规定重开条件：先完成同口径归因）。

### 阶段三：工程基线补强

| # | 动作 |
|---|---|
| 6 | 对 `format.rs::load_with_recovery` 与 `ParseDatabaseBytes` 上 `cargo-fuzz`；纳入 CI |
| 7 | 跑 Miri（至少覆盖 `format.rs` 与 `DbInner` 的内存不变量） |
| 8 | 修正规范 §4.1.2（已失效的锁协议）、§5.1.1（`forceUnlock`） |
| 9 | 修正 `API-V1-FREEZE.md` §2.2 与 `db.rs:954-958` 关于"丢弃句柄是否注销"的矛盾表述 |
| 10 | 清 21 条 clippy 警告；`.gitignore` 移除 `*.bak` |
| 11 | 为 .NET `Snapshot` 加 finalizer 或改弱引用计数（消除 P1-5 静默永久降级） |
| 12 | 为 `scan_max_tx_id` 增加帧头 CRC 校验与越界检查（纵深防御） |

---

## 十、复现方法

### 10.1 官方测试与基准

```powershell
# Rust 全量测试
cd src\rust; cargo test --release

# .NET 全量测试与 benchmark
cd src\dotnet
dotnet build Ckv.slnx -c Release
dotnet test tests\Ckv.Tests\Ckv.Tests.csproj -c Release --no-build
.\benchmarks\Ckv.Benchmark\bin\Release\net8.0\Ckv.Benchmark.exe
```

官方 benchmark 输出即 P0-1 的写放大数据来源（`[1] Small Data Scaling Suite` 与 `[2] Mixed / Long-Tail Data Suite` 两节）。

### 10.2 自建探针

探针位于 gitignored 目录，**未修改任何被 git 跟踪的文件**：

```
temp/probe/          Rust 探针（6 组 + 跨进程锁探针）
  src/main.rs            写放大 / WAL / 批量装载 / close 语义 / 读延迟 / 快照 / 扫描
  src/bin/lockprobe.rs   跨进程锁：hold / hold-and-close / try / try-after-close
temp/netprobe/       .NET 探针（5 组）
  Program.cs             插入复杂度 / 读延迟 / 快照 CoW / 内存放大 / 全量扫描成本
```

```powershell
# Rust 探针
cd temp\probe; cargo run --release

# 跨进程锁矩阵（需先 build lockprobe）
$exe = "$PWD\target\release\lockprobe.exe"
$d = "$env:TEMP\ckvlock"
$h = Start-Process $exe -ArgumentList @("hold","$d\x.db","6000") -PassThru -NoNewWindow `
       -RedirectStandardOutput "$d\h.txt"
Start-Sleep -Milliseconds 1200
& $exe try "$d\x.db" 2500      # 期望：等满 ~2500ms 后 LockedError
$h.WaitForExit()

# .NET 探针
cd ..\netprobe; dotnet build -c Release
.\bin\Release\net8.0\netprobe.exe insert-scaling $env:TEMP\ckvnet
.\bin\Release\net8.0\netprobe.exe reader-latency  $env:TEMP\ckvnet
.\bin\Release\net8.0\netprobe.exe memprobe       $env:TEMP\ckvnet
.\bin\Release\net8.0\netprobe.exe snapshot-cow   $env:TEMP\ckvnet
.\bin\Release\net8.0\netprobe.exe scancost       $env:TEMP\ckvnet
```

### 10.3 锁矩阵完整复现（P0-3 的核心证据）

```powershell
$netexe = "temp\netprobe\bin\Release\net8.0\netprobe.exe"
$rustexe = "temp\probe\target\release\lockprobe.exe"

# .NET 持有 -> Rust 竞争（缺陷：立即失败，timeout 被忽略）
$db = "$env:TEMP\ckvx\x.db"
$h = Start-Process $netexe -ArgumentList @("hold","$db","6000") -PassThru -NoNewWindow `
       -RedirectStandardOutput "$env:TEMP\ckvx\h.txt"
Start-Sleep -Milliseconds 1200
& $rustexe try $db 2500     # 实测：OPEN_ERR elapsed_ms=0 os error 32
& $netexe  try $db 2500     # 对照：OPEN_ERR elapsed_ms=2506 LockedError
$h.WaitForExit()

# .NET 持有且已 Dispose -> Rust 竞争（正常）
& $rustexe try "$env:TEMP\ckvy\z.db" 2500   # 实测：OPEN_OK elapsed_ms=0
```

---

## 附：评审边界声明

- 本次评审**未修改任何被 git 跟踪的文件**，未执行 commit / push。
- 所有探针代码位于 `.gitignore` 覆盖的 `temp/` 下，仓库工作区保持干净（`git status --short` 为空）。
- 本文对规范与代码不一致之处均为**记录与建议**，任何涉及磁盘格式、默认值或 API 签名的变更，按 `KV-DB-DESIGN-SPEC.md` §5.1.1.1 第 3 条须走 ADR 级决策，五语言需同步落地。
- 功能缺口（TTL、流式迭代、迁移路径、压缩、加密、多表 schema）的补齐顺序建议置于阶段三之后——它们收窄适用场景，但不阻断阶段一/二所列问题的修复。