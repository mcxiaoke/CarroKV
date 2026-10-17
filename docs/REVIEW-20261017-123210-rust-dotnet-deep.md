# CKV 生产就绪度深度评审报告（Rust / .NET 主线）

- **评审日期**：2026-10-17 12:32:10（本机时间）
- **评审范围**：项目文档（README、KV-DB-DESIGN-SPEC v3.2、API-V1-FREEZE）+ Rust 实现（3,774 行源码 + 26 个测试文件）+ .NET 实现（3,819 行源码 + 22 个测试类）。Go / Dart / Kotlin 按要求未审。
- **评审方式**：全量通读核心源码；`cargo test --release` 与 `dotnet test` 实测；与 `temp/github/` 下 redb / fjall / RocksDB / FASTER / Garnet / Lightning.NET(LMDB) 源码及公开基准对比；网络检索生产成熟度信号。
- **结论**：**不可投入生产。** 工程质量在"个人/早期开源项目"中属上乘，但距同类优秀嵌入式 KV 的平均水准仍有明确差距，且存在 **1 个本报告新发现的数据丢失级竞态（P0-5）** 与若干跨语言一致性缺陷。
- **与并行报告的关系**：`docs/REVIEW-20261017-122633-production-readiness.md`（并行会话产出）已覆盖 4 个 P0（默认模式 O(N) 写、写锁阻塞读、Rust 跨语言锁超时失效、Rust close() 不释放锁）与 7 个 P1。**本报告不重复其论证**（相关条目仅交叉验证），聚焦：① 新发现的 P0-5 后台刷盘竞态；② .NET 实现独立深挖结论；③ 同类库对标基线的量化定位；④ 修复优先级建议。两报告结论一致：**均判定不可投入生产。**

---

## 一、总体结论

| 维度 | 判定 | 一句话依据 |
|---|---|---|
| **功能** | 同类中等偏下 | CRUD/范围扫描/快照/watcher/batch 齐备且跨语言互通是独家优势；缺流式迭代器（P1）、TTL、压缩、加密、ACID 事务；value 硬限 16MB |
| **可靠性** | 核心机制达标，提交路径有洞 | 崩溃安全设计哲学（原子轮转 + 双代备份 + 拒绝静默丢数据）、全文件 CRC32 校验达到 redb/fjall 水准；但 Deferred 模式后台刷盘竞态可丢失已确认写入（本报告 P0-5），跨语言锁语义分叉（并行报告 P0-3/P0-4） |
| **性能** | 远低于平均水准（架构性） | 读性能达标（内存有序结构，纳秒级）；默认模式每写全量序列化 + 2 次 rename + fsync，吞吐随库大小衰减（98MB 库 3 ops/s），写放大 54x~11,485x；redb CoW 仅 1.7~4x，LSM 10~30x |
| **可生产性** | ❌ 否 | P0 未清、零 CI、零 fuzz、pre-1.0、零第三方采用、文档与实现多处漂移 |

**边界清晰的可用组件**：在"读多写少、总量 < 2MB、配置/凭证/偏好、单进程"场景内，默认模式（SnapshotAtomic + Immediate）是可工作的，其 253 ops/s 与 LMDB 每写 fsync 口径（66~97 ops/s）、SQLite（80~948 ops/s）同量级——**慢的是模式本身而非实现低效**。问题在于 README 把它定位为"工业级"通用引擎，而默认配置撑不起这个定位。

---

## 二、文档评审

### 2.1 做得极好的部分（达到并超过同类平均水准）

1. **字节级磁盘格式规范**（KV-DB-DESIGN-SPEC v3.2，421 行）：文件头/帧/条目布局、CRC 覆盖范围、TxID 单调性铁律、装箱算法、黄金测试向量全部定死。redb 有 design.md，fjall 有 format 文档，但**五语言字节级确定性 + 黄金向量复现**是 CKV 独有纪律。
2. **API v1.0 冻结契约**（300 行）：18 项一致性修复逐条记录在案，含破坏性变更与兼容策略；错误体系 6 语言同名同义。
3. **设计决策可追溯**：代码注释大量引用决策编号（C1~C18、D1~D2、G1），且能在 git 历史中找到对应修复提交。这是罕见的工程纪律。

### 2.2 文档缺陷（P2 级，但影响生产信任）

| # | 问题 | 证据 |
|---|---|---|
| D-1 | README 引用不存在的文档 | README:107 指向 `docs/CHANGES-20261015.md`，该文件已在 commit 3dcb1de 被删除；README:37/105 声称 Rust 有 API 文档（实际"待补充"） |
| D-2 | 规范 §4.1.2 仍是**已被废弃的旧锁协议**（32 字节 PID+UUID+CreateTimeMs + steal 机制），实现已改为内核锁（C9，lock.rs:8-27 有说明但规范未更新） | KV-DB-DESIGN-SPEC.md:179-199 vs src/rust/src/lock.rs:8-27 |
| D-3 | 规范 §4.2 自愈算法仍写"损坏主库自动回退 .bak"，实现已改为默认拒绝（D1 决策，API-V1-FREEZE §3.15 有记录但规范正文未同步） | KV-DB-DESIGN-SPEC.md:216-224 |
| D-4 | MSRV 声明错误 | README:37 与规范 §1.1 声称 `rustc 1.70+`；实际 `File::try_lock` 需 **1.89+**（本机 1.96.0 编译通过，1.70 无法编译） |
| D-5 | .NET README 三处失实 | src/dotnet/README.md:16 描述已废弃的 32 字节锁文件协议；:15 声称自动 .bak 自愈（默认已关闭）；:259-269 异常体系表未反映 Obsolete 别名层 |
| D-6 | C16 契约本身有缺陷：规定了"短读锁克隆 → 锁外写 .tmp → 短写锁轮转"两阶段，但**未要求轮转前校验代际**——P0-5 竞态由此被写进规范 | API-V1-FREEZE.md:246-258 |

---

## 三、Rust 实现深挖

### 3.1 架构（db.rs 2,623 行 + format.rs 516 行 + lock.rs 107 行）

- **内存模型**：`BTreeMap<ns, Arc<BTreeMap<key, (type, value)>>>`，全量驻留内存；快照为外层 Map 浅拷（Arc/ns），命名空间级 CoW。
- **双引擎**：`SnapshotAtomic`（默认，每写全量序列化 + `.tmp→.bak→.db` 轮转）与 `AppendOnlyLog`（帧追加 + 阈值/死比率双触发自动压实，压实失败指数退避重试，D2）。
- **锁**：进程内 `parking_lot::RwLock`；跨进程 `<db>.lock` 内核锁（flock/LockFileEx）连接生命周期持有（C9）；`(maxTxID, fileSize)` 账本检测外部篡改。
- **恢复**：.tmp 晋升 / D1 默认拒开 + opt-in .bak 回退 / torn-tail 与介质损坏三分类（全零尾部判为未写尾部，其余判介质损坏拒绝截断）。
- **watcher**：写锁内收集、锁外派发（无死锁重入），显式 unwatch。

### 3.2 测试实测（本报告执行）

`cargo test --release`：**98 通过 / 0 失败 / 1 ignored**（26 个测试文件 + 2 个 doc-test）。覆盖：数据类型、黄金向量（3 个）、故障注入（6 个）、写原子性回归（4 个）、WAL 模式与压实（11 个）、快照隔离（3 个）、批处理（9 个）、并发压力（1 个）、锁协议（4+1）、API 冻结（8 个）、互操作（3 个）。.NET 侧 `dotnet test`：**99 通过 / 0 失败**（并行会话执行）。两语言用例高度镜像。

### 3.3 本报告新发现：P0-5 Deferred 后台刷盘竞态导致已确认写入丢失

**这是本报告独有的发现（并行报告未覆盖），且是提交路径上唯一的数据丢失级竞态。**

**根因**（`db.rs:404-479` `bg_flush_step` vs `db.rs:1086-1103` `flush()` / `db.rs:2430-2456` `commit_flush`）：

```rust
// bg_flush_step：两阶段，中间不持锁
let plan = { let inner = inner_arc.read(); Some((.., inner.data.clone(), start_tx)) };  // T0: 读锁内克隆 S0
// ↓ 锁外序列化到 {path}.tmp（约毫秒~百毫秒）
let final_max_tx_id = DbInner::serialize_snapshot_to_tmp(&tmp_path, &data, start_tx)?;  // T3
let mut inner = inner_arc.write();                 // T4: 才拿写锁
rename(.db → .bak); rename(.tmp → .db);            // 轮转，无任何代际校验
inner.is_dirty = false;                            // 无条件清脏标志
```

**竞态时序**（SnapshotAtomic + Deferred，后台 flush 与显式 `flush()`/`compact()` 交错）：

| 时刻 | 事件 | 磁盘 | 内存 |
|---|---|---|---|
| T0 | 后台线程读锁内克隆数据 S0（tx=100） | gen@tx99 | S0@100 |
| T1 | 写线程 W 提交（Deferred：仅内存，tx=101，`is_dirty=true`） | gen@tx99 | **S1@101** |
| T2 | 用户线程调 `flush()`：持写锁序列化 **S1** → 轮转 → 返回成功 | **S1@101** | S1@101 |
| T3 | 后台线程把 **S0** 写入 `.tmp`（T2 已消费旧 .tmp，此处创建新文件） | S1@101 | S1@101 |
| T4 | 后台线程拿写锁：`.db`(S1)→`.bak`、`.tmp`(S0)→`.db`，`is_dirty=false`，`last_known_disk_state` 更新为 S0 的值 | **S0@101** | S1@101 |

**后果**：
1. **已确认写入静默丢失**：T2 的 `flush()` 已向调用方确认持久化，磁盘却被回滚到 S0；内存是 S1 但 `is_dirty=false`，后续 `flush()` 在 `db.rs:1089` 短路、`close()` 兜底同样短路——S1 永不重新落盘，进程退出即丢。
2. **回滚痕迹被掩盖**：`last_known_disk_state` 被更新为陈旧文件的值，外部篡改检测失效。
3. **次生形态**：两个线程同时以截断模式写同一 `.tmp`，后打开者直接 `IoError`（API 契约受损）；极端交错下混合内容的 `.tmp` 被轮转上位，下次打开 CRC 拒开（可用性事件，`.bak` 仍保有上一好代）。
4. **`.bak` 里反而是更新的 S1**——人工可恢复，但引擎层面表现为"健康库 + 陈旧数据"，是最难诊断的一类故障。

**为何测试没发现**：`adaptive_compaction_test.rs:23` 甚至特意把 `sync_interval_ms` 设为 1 小时来"never let the background flush interfere"——作者感知到了干扰并在测试中绕开，而非修复竞争。无任何测试覆盖"后台 flush × 显式 flush"并发。

**触发条件**（收窄影响面）：仅 `SyncMode::Deferred`（后台线程仅在此模式启动）+ `SnapshotAtomic`；默认配置（Immediate）不受影响。但 README:81 恰好推荐 Deferred 用于 1MB~100MB 场景，且"写一批 + flush 确认"是该模式的标准用法。

**跨语言同款**：.NET `FlushIfDirty`（Db.cs:323-421）与 `CommitFlush`（Db.cs:1191-1235）共享同一 `.tmp` 路径、无代际校验即轮转并清 `_isDirty`，与 Rust 逐行同构；.NET 亦无专用 flush 互斥（无 SemaphoreSlim/版本号）。**这是规范级缺陷（C16 契约未要求代际校验），必须五语言同批修复。**

**修复方案**（按优先级）：
1. 所有提交路径（`bg_flush_step` 轮转段、`flush`、`close`、`compact`、自动压实）共用一把专用 flush 互斥锁（Rust `Mutex` / .NET `SemaphoreSlim`，不复用读写锁以免把持锁时间扩展到 fsync）；
2. 规划阶段捕获脏代际（每次成功提交 `dirty_generation += 1`）；写锁内轮转前校验：代际已推进或 `!is_dirty` → 删除本次 `.tmp` 直接返回，**绝不轮转陈旧快照、绝不清除他人的 dirty 标志**；
3. 或后台临时文件改唯一路径（`{path}.tmp.{pid}.{seq}`）消除文件级冲突，再配合 2 的代际校验；
4. 补确定性时序回归测试（可注入延时 hook）：Deferred + SnapshotAtomic 下"后台 flush 进行中 → 写入 → 显式 flush → 等后台轮转 → 重开验证数据不丢"。

### 3.4 独立验证并行报告的两个 P0（代码证据确凿）

- **P0-3（Rust 跨语言锁超时失效）**：`lock.rs:38-50` 的 `open(&lock_path)?` 在退避循环**之外**，.NET 侧以 `FileShare.None` 持有该文件时，Windows 上任何 `CreateFile` 均失败（sharing violation，os error 32），Rust 直接抛 `DbError::Io`——`lock_timeout_ms` 被绕过且错误类型违反冻结契约 §1.3。代码路径确认属实。修复：.NET 侧锁文件改为 `FileShare.Read|Write|Delete`（互斥交给 LockFileEx/flock），Rust 侧把 `open` 也纳入退避重试并按错误类型分流。
- **P0-4（Rust close() 不释放跨进程锁）**：`db.rs:483-525` `close()` 确无 `self.lock` 操作，锁仅在 `Drop for DbInner`（db.rs:231-236 注册表清理 + FileLock 析构）释放；规范 §5.2.2 与 .NET `Dispose()`（Db.cs:2552-2554）均释放。属实。修复：`close()` 显式 `self.lock.take()` 释放并置空。

### 3.5 Rust 其他发现

| 级别 | 问题 | 证据 |
|---|---|---|
| P2 | `WatcherHandle` 文档自相矛盾：结构体注释说"drop 不取消订阅"（db.rs:45-51），`watch()` 文档说"dropping it removes the callback"（db.rs:954-958） | db.rs:45-58 vs 954-958 |
| P2 | 重复文档注释（increment_i64、watch 各重复一遍） | db.rs:752-757, 954-958 |
| P2 | 读路径每次 `get_*` 克隆 value（`val.clone()`），大 value 高频读有分配压力 | db.rs:545, 689 等 |
| P2 | 每次写克隆整个 watcher Vec（无 watcher 时为空 Vec 克隆，廉价；多 watcher 时 O(w)） | db.rs:630 |
| P2 | 快照序列化先全量克隆进 `Vec<RawOpEntry>` 再逐帧编码，峰值 2 份数据 + 64MB 帧缓冲 | db.rs:2375-2386 |
| P3 | `increment_i64` 静默 wrapping 溢出（与 .NET 一致，已文档化，可接受但应在 API 文档高亮） | db.rs:778 |
| P3 | `namespaces()` 每次全量分配 `Vec<String>`（O(ns)） | db.rs:984-991 |
| P3 | 同路径二次 open 请求 read_only=true 会静默拿到已存在的读写实例（仅 on_warning 提示） | db.rs:280-299 |

---

## 四、.NET 实现深挖（子代理独立审查 + 本报告抽查）

### 4.1 架构与一致性

`SortedDictionary<string, NamespaceStore>`（UTF-8 无符号字节序比较器）+ 每命名空间有序 `List<KeyItem>` 二分查找；`ReaderWriterLockSlim` 进程内锁 + `LockFile` 跨进程内核锁；快照为命名空间级 CoW（`_snapshotCount > 0` 时写路径克隆 List，快照读完全无锁，协议推敲正确）。

**与 Rust 的一致性极高**（子代理逐条核对）：CRC32（同多项式/同初末值）、全部格式常量、编解码语义（wrapping 增量、bool 非零真、NaN 规范化、namespace 归一化）、崩溃恢复三分支、torn-tail/介质损坏分类、压实策略（64 起翻倍至 4096、2MB/50%）全部对齐；错误体系用 `[Obsolete]` 别名继承新类做源码兼容，设计负责。`dotnet test`：**99/99 通过**。

### 4.2 .NET 侧问题（除 P0-5 同款外）

| 级别 | 问题 | 证据 |
|---|---|---|
| P1 | 轮转兜底 `File.Copy(src,dst,true)` 非原子（先截断目标再拷贝，窗口期崩溃留半成品 .db）；Rust 同场景已改为 stage+rename | Db.cs:1337-1355 vs db.rs:2523-2545 |
| P1 | `Snapshot` 无 finalizer：调用方未 Dispose → `_snapshotCount` 永久 >0 → 此后每次写都全量克隆 NamespaceStore，静默永久降级且无告警 | Db.cs:1981-2013 |
| P1 | Linux 上 .NET 目录 fsync 不可用（`File.OpenHandle(dir)` 抛 UnauthorizedAccessException），而 Rust 有——跨语言掉电可靠性分叉 | Db.cs:1280-1330 vs db.rs:2560-2567 |
| P2 | `Utf8StringComparer` 每次比较/哈希都分配 UTF-8 字节数组，SortedDictionary 每次操作 O(log N) 次比较 | BinaryHelper.cs:150-168 |
| P2 | 后台 `FlushIfDirty` 在读锁内做全量序列化（含 CRC32），大库下阻塞写者；Rust 至少把序列化放在了锁外 | Db.cs:342-343 |
| P2 | `Dispose` 的 `catch {}` 吞掉最终 flush 全部异常（磁盘满时静默丢数据无信号）；Rust close() 至少上抛 | Db.cs:2534-2537 |
| P2 | 进程内注册表 `TryRemove` 按键不按值移除，极端竞态下破坏单例保护 | Db.cs:2556-2559 |
| P3 | README 三处失实（锁文件协议、.bak 自愈、异常体系表） | src/dotnet/README.md:15,16,259-269 |

---

## 五、与同类优秀 KV 数据库对标

### 5.1 对标基线（temp/github 本地源码 + 公开数据，检索日 2026-10-17）

| 库 | 定位 | 版本/成熟度 | 生产证据 |
|---|---|---|---|
| redb | Rust 嵌入式，CoW B+tree MVCC | 4.3.0（2026-09） | 1,183 crate 依赖，月下载 ~200 万，Ordinals 索引器 |
| fjall | Rust 嵌入式 LSM | 3.1.12（2026-10） | 198 crate，月下载 ~22 万 |
| RocksDB | C++ LSM 标杆 | 11.0.4 | Meta/字节/腾讯/百度/LinkedIn/TiKV/CockroachDB |
| LMDB | C B+tree mmap | 长期维护 | OpenLDAP、Meilisearch，十余年 |
| FASTER | C# 哈希+log | 2.6.5（2024-05） | README 明示停更，引导 Garnet |
| Garnet | 服务型（Redis 兼容） | 2.1.x | 微软一线团队 + VLDB'26 论文（**不同场景**，仅数量级参照） |

### 5.2 功能对标

| 维度 | CKV | redb | fjall | LMDB | RocksDB |
|---|---|---|---|---|---|
| 范围/前缀扫描 | ✅（但全量物化，见 P1） | ✅ 迭代器 | ✅ 含反向迭代 | ✅ cursor | ✅ |
| 事务 | batch=内存暂存原子合入（无 ACID/savepoint） | **完整 ACID serializable + savepoint** | **serializable** | **完整 ACID** | 悲观/乐观 |
| 快照/MVCC | ✅ 命名空间级 CoW 无锁 | ✅ MVCC | ✅ | ✅ 读事务 | ✅ |
| watcher/change feed | ✅ **进程内回调（同类少见）** | ❌ | ❌ | ❌ | ❌ |
| 压缩 | ❌ | ❌ | ✅ LZ4 | ❌ | ✅ ZSTD 等 |
| 加密 | ❌ | ❌ | ❌ | ✅（fork） | ❌ |
| TTL | ❌ | ❌ | ❌ | ❌ | ✅ |
| 多进程并发写 | ❌（设计明确不支持，.lock 仅互斥） | ⚠️ experimental | ❌ | ✅ | ✅ |
| 最大 value | **16MB 硬限** | 3GiB | 大（blob） | 大 | 大 |
| 数据校验 | **CRC32 全文件** | **XXH3-128 Merkle 全数据** | block+全文件 checksum | SHA256（fork） | 块级 CRC32c |
| 跨语言字节互通 | ✅ **独家（五语言）** | ❌ | ❌ | ❌ | ❌ |

**功能结论**：CKV ≈ "redb 减去多表/ACID，加上 watcher 和跨语言互通"。缺 TTL/压缩/加密属"低于 RocksDB、持平 redb/fjall/LMDB"；watcher 与五语言单文件互通是 redb/fjall/LMDB 均不提供的真实差异化。16MB value 硬限对"配置/凭证"场景无碍，但堵死 blob 场景。

### 5.3 性能对标（关键：口径）

| 口径 | 同类数量级 | CKV 实测 | 判定 |
|---|---|---|---|
| 纯内存读 | 10^7~10^8 ops/s（FASTER/Garnet） | 未公布（内存 BTreeMap 预计 10^6~10^7） | 预计达标，**但无公开数据** |
| 持久化引擎随机读 | 10^5~10^6 ops/s（LMDB/redb） | 预计同量级 | 预计达标 |
| **每写 fsync 持久化写** | **10^2 ops/s**（LMDB 66~97、SQLite 80~948） | 253 ops/s（默认模式） | **正常区间——慢的是模式不是实现** |
| 批量/非持久化写 | 10^5~10^7 ops/s | AOL 模式 68 万 ops/s | 与 fjall/RocksDB batch 差 3~4 个数量级 |
| 写放大 | LSM 10~30x；redb CoW 1.7~4x（文件膨胀） | **快照模式 54x~11,485x（随库大小线性增长）** | **差 1~2 个数量级且随数据量恶化** |
| 写持锁期间的读延迟 | RocksDB/LMDB 读写不互斥 | 读 p99 24ms（Rust）/ max 111ms（.NET，5MB 库单次写） | 架构性差距 |

**核心洞察**：CKV 默认模式的 253 ops/s 单看并不异常（LMDB 同口径 66~97）；真正落后的是 (a) 写放大随库大小线性增长（redb CoW 恒定 ~2-4x），(b) AOL 批量写与 LSM 差 3-4 个数量级，(c) 写锁阻塞读导致并发场景读尾延迟由写路径决定。这三者都是**架构决定，调参无法弥补**。

### 5.4 可靠性对标

- **达到平均水准**：全文件 CRC32 校验覆盖率（redb XXH3 Merkle/fjall 全文件 checksum/RocksDB 块 CRC32c 同级）；"损坏即拒开、拒绝静默清空"的哲学与 redb/RocksDB 一致；原子轮转 + 双代备份的崩溃安全设计清晰。
- **低于平均水准**：无 fuzzing（redb 有 fuzz/ 目录）、无 soak test（SQLite 发布前 ~2.5 亿测试）、无 CI（redb 四平台 CI；本仓库无 .github/）、无崩溃点枚举测试（SQLite 有 power-loss 专项；CKV 的故障注入是手工构造场景）。**这是 redb 4 年、fjall 3 年积累的核心差距——可靠的是设计，未经验证的是时间。**
- **版本与采用**：pre-1.0（0.1.0）、零第三方生产采用。redb 1.0 于 2023 年发布并承诺格式稳定。

---

## 六、生产就绪判定与路线

### 6.1 判定：**不可投入生产**

必须先行修复的阻断项（合并并行报告与本报告，按修复顺序）：

| 优先级 | 问题 | 类型 | 工作量量级 |
|---|---|---|---|
| 1 | **P0-5 后台刷盘竞态**（本报告）：flush 互斥锁 + 代际校验 + 回归测试，五语言同批 | 数据丢失 | 中（规范+5 语言+测试） |
| 2 | P0-3/P0-4 跨语言锁（.NET 锁文件共享模式、Rust open 重试与错误类型、Rust close() 释放锁） | 功能不可用+契约违反 | 小~中 |
| 3 | P0-1/P0-2 写路径架构（默认模式 O(N) 写、写锁阻塞读）：至少把 AOL 模式的"锁外序列化"做对，并在 README 把默认模式适用边界从"工业级"改写为"<2MB 读多写少" | 性能/定位 | 大（架构） |
| 4 | P1 批次：.NET 轮转兜底原子化、.NET Snapshot 终结器、流式迭代器、序列化流式化、目录 fsync 跨语言对齐、二进制解析器 fuzz | 正确性/功能 | 中~大 |
| 5 | 工程基础设施：CI（至少 Win/Linux/macOS × Rust/.NET）、cargo-fuzz、包元数据与 MSRV 修正、文档漂移清理（D-1~D-6） | 可生产性 | 中 |

### 6.2 分阶段放行建议

- **现阶段可用的窄场景**（默认模式，单进程，总量 <2MB，读多写少）：桌面/移动端配置与凭证存储。此时 253 ops/s 与 LMDB 同口径同量级，崩溃安全设计甚至优于多数同类。前提：接受"无 CI/无 fuzz 的新库"风险，并显式 `close()`。
- **达到"同类平均水准"的门槛**：P0-5 + 锁语义修复 + CI + fuzz + 一次真实用户场景 soak。预计这是 redb 1.0 前走过的路。
- **永久不追平的维度**（除非重写存储层）：每写 O(N) 的吞吐特性、无 ACID、无压缩。若目标场景超出 <2MB，应直接换 redb/fjall/LMDB/RocksDB，或用 CKV 的跨语言格式做它们之间的交换层——那才是 CKV 差异化价值的正确用法。

---

## 七、附：本报告执行记录

| 验证 | 结果 |
|---|---|
| `cargo test --release`（Rust 26 文件） | 98 通过 / 0 失败 / 1 ignored + 2 doc-test |
| `dotnet test`（.NET 22 类，子代理执行） | 99 通过 / 0 失败 |
| Rust 核心源码通读 | db.rs 2,623 / format.rs 516 / lock.rs 107 / options.rs 175 / types.rs 248 / errors.rs 47 全读 |
| .NET 核心源码通读 | 子代理 8 文件全读 + 22 测试类清点 |
| 同类库对标 | temp/github 7 库源码 + crates.io/GitHub/LMDB 官网/SQLite testing.html 网络检索 |
| git 历史考古 | 确认 2026-10-16 三轮独立评审的 P0（自动压实丢写/嵌套锁死锁/坏尾静默清空/轮转窗口）均已在当前代码修复，本轮 P0-5 为 C16 短锁刷新（2026-10-16 晚~17 引入）之后新暴露的竞态 |

**评审人备注**：本报告与并行报告 `docs/REVIEW-20261017-122633-production-readiness.md` 结论互证；P0-5 为本报告首先发现并给出复现时序与修复方案，建议按 §6.1 优先级 1 处理。

---

## 八、勘误（2026-10-17 13:10 实证更新）

本报告 §5.3 与结论中关于 `AppendOnlyLog` 模式性能的表述（"AOL 模式 68 万 ops/s"、"与 fjall/RocksDB batch 差 3~4 个数量级"）**直接引用了 README 与规范 §4.4 的声明，未做规模验证，现予更正**：

经 `temp/probe/src/bin/steady_state.rs` 实证（AOL + Deferred，每键 1KB，本机 Windows）：

| 存活规模 | 压实关闭（对照） | 默认阈值 4MB |
|---|---|---|
| 0.6MB | 5,478 ops/s | 5,991 ops/s |
| 6MB | 5,180 ops/s | **41 ops/s** |
| 20MB | 5,216 ops/s | **13 ops/s** |

**根因**：`maybe_compact_after_write`（`db.rs:2060`，.NET 同构于 `Db.cs:1441`）以"文件大小 ≥ 阈值"触发全库压实，而压实后文件恰等于存活大小——**存活数据 ≥ 4MB 后每一次写都触发全库重写**。项目自带测试（压实阈值全部设为 u64::MAX 量级以隔离变量）与矩阵基准（640KB 数据集）均未覆盖此场景，故缺陷逃逸；README"68 万 ops/s"实为 Manual 模式批量口径（结束一次 flush）。

**结论修正**：AOL 的追加写路径本身是 O(1) 且健康的（对照列）；性能悬崖来自压实触发条件缺陷，属可实现性修复（非架构重写），已列入 `docs/FIX-PLAN-20261017-124225.md` 的 FIX-21（P0）。本报告 §5.3"核心洞察"中"AOL 批量写与 LSM 引擎差 3~4 个数量级"的论断仅在"每写 fsync（Immediate）"口径下成立；批量/非持久化口径下 CKV 与 redb `Durability::None`（109 万 ops/s）同级。其余结论（P0-5 竞态、默认模式 O(N)、内存放大、跨语言锁）不受影响。
