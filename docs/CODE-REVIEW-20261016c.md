# CKV 代码审查报告

| 项目 | 内容 |
|---|---|
| 审查日期 | 2026-10-16 |
| 审查范围 | `src/rust`、`src/dotnet`（重点），`src/go`、`src/kotlin`、`src/dart`（同架构对照） |
| 重点内容 | 2026-10-16 合入的 WAL（AppendOnlyLog）相关代码：架构设计、写路径、崩溃恢复、压实、锁协议 |
| 审查方法 | 通读全部核心源码 + 设计文档对照 + 构建/运行测试 + 编写临时复现程序验证缺陷（验证后已删除） |
| 测试基线 | Rust 249 个测试全部通过；Go 测试全部通过；.NET 测试全部通过 |

---

## 一、总体评价

### 1.1 架构概览

CKV 是一个"全内存驻留 + 单文件持久化"的轻量 KV 库，v3.2 二进制格式下有两套存储引擎：

```
                         进程内
  API (put/get/delete/ns/batch/watch/snapshot)
        │
        ▼
  全局单例 Registry（同一 path 只构造一次）
        │
        ▼
  DbInner（读写锁保护）
    ├── data: BTreeMap<ns_bytes, BTreeMap<key_bytes, (vtype, value)>>
    ├── current_max_tx_id / last_known_disk_state
    └── 写路径
          ├── SnapshotAtomic：begin_flush → 改内存 → commit_flush（全量快照 .tmp → fsync → 轮转 db→bak、tmp→db）
          └── AppendOnlyLog：begin_flush → 追加帧 → fsync（Immediate）→ 超阈值时自动 commit_flush（快照轮转）
                         进程间
  FileLock：db.lock 文件（32B：PID + UUID + 时间戳），僵死判定 + 重命名窃取 + UUID 防 ABA
```

崩溃恢复三件套（`init_and_recover`）：

1. `.tmp` 存在且主库缺失 → 校验通过后提升 `.tmp`，否则删除；
2. 主库加载；WAL 模式允许"残尾截断"自愈；
3. 主库失败 → `.bak` 回退；主库缺失且 `.bak` 存在 → 复制恢复。

### 1.2 做得好的地方

- **磁盘格式严谨**：文件头 CRC、帧头 CRC、负载 CRC、帧魔数、TxID 单调、OpCount/Payload 上限，全链路可校验；
- **确定性序列化**：ns 字典序 → key 字节序，排序结果跨平台一致，`.bak` 轮转、`Backup`、互操作都依赖这一点，设计正确；
- **两阶段写协议**：所有可能拒绝写入的校验（锁、外部改动检测）都在改内存之前完成，失败时内存零变更；
- **CoW 快照 + 引用计数**：快照与写者互不阻塞，快照计数为 0 时才原地修改；
- **锁协议的防 ABA 思路**：UUID 比对、窃取走重命名、释放前比对，方向正确；
- **测试密度高**：Rust 249 个测试覆盖了格式、故障注入、WAL、互操作等，且五语言同构便于对照。

### 1.3 总体结论

架构方向是健康的，**但今天（2026-10-16）合入的 WAL 自动压实路径在三个语言中存在确定性数据丢失或功能失效问题，且现有测试没有覆盖这些路径**。这些问题属于"测试全绿但功能错误"的类型，建议在任何使用 WAL 引擎的发布前修复。

问题按严重程度分为：

- **P0（数据丢失/功能不可用）3 类**：自动压实丢写入、中间坏块整库截断、Rust 残留 B4 轮转窗口；
- **P1（锁协议/并发缺陷）5 类**：锁句柄即关、释放 TOCTOU、open 截断不持锁、.NET 注册表跨平台错误、嵌套锁；
- **P2（健壮性/性能）7 类**：死字节统计缺失、POSIX 无目录 fsync、恢复不校验定长类型、Clear 逐键墓碑、逐写开关文件、重复 open 静默忽略选项、Drop/close 细节；
- **跨语言一致性偏差**：默认值、空 key、错误命名、Dart 锁协议、文档漂移。

---

## 二、P0 严重问题

### P0-1 WAL 自动压实丢失"触发压实的本次写入"（Rust / Go / .NET / Dart）

**问题位置**

- Rust：`db.rs::append_entries`（约 L1645–1717）+ `apply_mutation`（约 L1720–1742）
- Go：`db.go::appendEntries`（约 L1685–1773）
- .NET：`Db.cs::AppendEntries`（L1079–1188）
- Dart：`db.dart::_appendEntries`（约 L355–420）

**时序分析**

WAL 写路径的执行顺序是：

```
append_entries(entries):
  1. begin_flush（拿锁、磁盘预检）
  2. 追加 delta 帧（本次写入的日志）
  3. 判定 new_file_size >= 阈值
       └─ 是 → begin_flush + commit_flush   ← 压实快照来自"当前内存"
  4. 返回
apply_mutation 在 append_entries 返回后才调用 apply_mem   ← 本次写入此时才进内存
```

压实快照在步骤 3 序列化内存状态，而**本次写入在步骤 4 之后才落入内存**。于是：

- 新 `.db`（快照）不含本次写入；
- 含本次写入的旧 `.db` 已被轮转为 `.bak`；
- 本次写入的 delta 不会再被重放（后续写入只追加新的 delta）。

**复现验证（Rust，临时程序，已删除）**

阈值设为 2048B，连续写入直到越线：

```
after crossing write n=35, in-process get = Some("value_number_35_...")
after reopen: key=k35 got=None expected=Some("value_number_35_...")
```

写入对调用者返回成功，但重新打开后该 key 丢失。默认阈值 4MB 时，用户会观察到"恰好跨越 4MB 边界的那次写在重启后消失"。

**.NET / Go 上的表现更糟（同时叠加 P1-5 嵌套锁）**

- **.NET**：压实在外层锁未释放、外层文件句柄（`FileShare.ReadWrite`，无 Delete 共享）未关闭时触发，轮转 `db→bak` 时 `File.Move` 抛出 `UnauthorizedAccessException`（Access denied），异常上抛给调用者——**超过阈值后每次写入都先自旋等锁超时（默认 10s）再失败**；
- **Go**：同样的嵌套 + 句柄（Go `syscall.Open` 只给 `FILE_SHARE_READ|WRITE`，无 `DELETE`），轮转 rename 失败；但 `commitFlush` 的错误被 `_ =` 静默吞掉（`db.go` L1767–1769），调用者看到成功，**每次越线写停顿一个完整锁超时（默认 10s），并遗留 `.tmp` 文件**（实测：`repro.db` 2654B + `repro.db.tmp` 1782B，无 `.bak`）。

**修复建议**

1. 把"内存应用"与"压实"的顺序倒过来：先 `apply_mem`，再判定阈值并压实；最小改法是将自动压实逻辑从 `append_entries` 移到 `apply_mutation` 的 `apply_mem` 之后：

```rust
// apply_mutation（WAL）
self.append_entries(entries)?;   // 只负责追加日志，不再内部压实
apply_mem(self);                 // 本次写入先进入内存
self.maybe_auto_compact()?;      // 用"含本次写入"的内存做快照
```

2. 压实必须在独立的锁获取周期内完成（锁已释放、追加句柄已关闭），参考 Rust 现有 L1707–1714 的释放顺序，但同样要保证 `apply_mem` 先于压实；
3. 压实失败必须上抛错误（Go 不能吞），否则会出现"写入成功但功能已失效"；
4. 补回归测试（见第六节）。

---

### P0-2 中间坏块被当作断电残尾，整库静默截断（全部 5 个实现）

**问题位置**

- Rust：`format.rs::load_with_recovery`（约 L259–420）
- Go：`format.go::LoadAllFramesWithRecovery`（约 L275–390）
- .NET：`Db.cs::ParseDatabaseBytes`（L489–722）
- Kotlin / Dart：`FrameLoader` 等价实现

**规范要求（设计规格 §3.4 / 恢复协议）**

遇到坏帧时必须做"病因鉴别"：

```
坏帧 → 从下一偏移开始扫描 FrameMagic 并尝试校验后续帧
  ├─ 找到合法后续帧 → 中间坏块（bit rot / 中间损坏）→ 抛 CorruptedError 拒开
  └─ 未找到合法后续帧 → 断电残尾 → 截断到坏帧之前，继续
```

**实际实现**

所有实现都是"遇到第一个坏帧即截断并返回"，没有任何向后扫描：

```rust
// Rust 现状（简化）
} else {
    // 任何坏帧（magic/CRC/单调/长度/entry 解码失败）
    if allow_tail_truncation {
        truncate(file, offset);        // 不看后面是否还有合法帧
        return Ok(frames);
    }
}
```

由于文件严格追加、从不原地改写，"坏帧之后还有合法帧"在断电场景下不可能发生——它只可能来自介质位翻转（bit rot）等中间损坏。当前实现把这种情况一律按残尾处理，**坏帧之后的全部数据（即使完全合法）被一次性截断**。

**复现验证（Rust，临时程序，已删除）**

健康文件含 k1/k2/k3 三帧；把第一帧负载内 1 个字节翻转（k2/k3 仍完全合法）后打开：

```
OPEN SUCCEEDED on middle corruption; file size now = 32 (healthy = 158)
k1=None k2=None k3=None
```

整个数据库被截到仅剩 32B 文件头，三个 key 全部丢失，仅产生一条 warning。

**修复建议**

1. 在截断分支前实现规格 §3.4 的诊断扫描：从 `offset + 1`（对齐到帧边界）开始搜索 `FrameMagic`，对候选位置做帧头 CRC + 负载 CRC 校验，只要存在一个合法后续帧就按中间坏块 `CorruptedError` 拒开；
2. 只有确认"坏帧之后再无合法帧"才执行截断，并把"截断了多少字节"通过 `onWarning` 明确上报；
3. 把"中间坏块拒开 / 残尾截断"两种情况各加一组测试（构造方式：健康文件 → 翻转首帧字节 vs 直接截掉末尾若干字节）。

---

### P0-3 Rust 仍残留 B4 类"先删后 rename"的两代备份消失窗口

**问题位置**：Rust `db.rs::rename_with_retry`（约 L1960–1990）及拷贝兜底。

**背景**：.NET 的 B4 修复记录明确指出：旧实现 `File.Delete(bak)` + `File.Move(db, bak)` 存在"两代备份同时不存在"的窗口，应改为单次覆盖式原子替换。.NET 已改为 `AtomicReplace`，Go 用 `os.Rename`（POSIX 与 Windows 上均覆盖目标），但 **Rust 仍是旧写法**：

```rust
fn rename_with_retry(src: &Path, dst: &Path) -> Result<()> {
    if dst.exists() {
        let _ = fs::remove_file(dst);   // ← 先删旧备份
    }
    fs::rename(src, dst)?;              // ← 两步之间旧备份已不在
    ...
}
```

- `std::fs::rename` 在 POSIX 上本就原子覆盖目标，删除是多余的；
- 在 Windows 上 Rust std 的 rename 使用 `MOVEFILE_REPLACE_EXISTING`，同样可以覆盖；
- 该函数用于 `commit_flush` 的 `db→bak` 轮转：删旧 `.bak` 与重命名之间，`.bak` 这一代备份不存在，破坏了两代模型的设计意图；也用于 `Backup`（目标已存在时）。

**修复建议**

1. 直接使用 `std::fs::rename(src, dst)` 覆盖目标，去掉 `remove_file`；若遇到 Windows 上极少数目录/属性差异，退化为"先 rename 到唯一临时名再处理"，但绝不先删目标；
2. 拷贝兜底路径同样应先写 `dst.tmp`、校验后再覆盖，避免"目标已删、拷贝失败"；
3. 与 .NET B4 修复对齐后，补一条"轮转期间 `.bak` 始终存在"的断言测试。

---

## 三、P1 锁协议与并发问题

### P1-1 锁句柄获取后立即关闭，"活性检测"实际失效（Rust / Go / .NET）

设计文档（§5.1 / 锁协议）描述的跨进程互斥在 Windows 上依赖："持锁者存活时，重命名其锁文件必然失败（WinError 32）"。但三个实现都在**写完锁文件后立即关闭句柄**：

- Rust：`lock.rs::acquire` 中 `file` 在 `Ok` 块结束即 drop（L61–68）；
- Go：`lock.go` 等价，create 后 close；
- .NET：`LockFile.Acquire` 中 `using (var fs = ...)` 块结束即 Dispose（L36–47）。

句柄关闭后，锁文件只是一个普通文件，"重命名失败"的硬约束不再成立。此时锁的唯一信号是时间戳：

- 持锁进程若发生长停顿（GC、swap、I/O 卡顿、大快照序列化）超过 `lockTimeoutMs`，锁会被新进程**在其写操作进行中窃取**，两个进程同时写同一数据库（split-brain），且无任何报错；
- 窃取前也没有做 PID 活性检查（POSIX `kill(pid,0)`、Windows `OpenProcess`），无法区分"进程已死"与"进程活着只是慢"。

**修复建议**（纵深防御，建议全做）

1. **POSIX**：创建后**保持 fd 打开**，在该 fd 上加 `fcntl(F_SETLK, F_WRLCK)`（或 `flock`）；进程崩溃时内核自动释放锁，从根本上消灭"僵死判定误判"，且开销为零；
2. **Windows**：保持锁文件句柄打开（`FileShare.None` 或只读共享），崩溃时句柄自动释放；
3. 窃取前增加 PID 活性探测作为第三道信号；
4. 明确文档：`lockTimeoutMs` 必须大于"单次最长 flush 时长"，并在阈值被设置得过小时给出 warning；
5. 单独评估"锁等待超时"与"僵死判定阈值"是否应拆成两个参数（当前一个参数双用途）。

### P1-2 锁释放存在 TOCTOU（读 UUID 与 unlink 之间）

Rust `release`（`lock.rs` L145–163）、.NET / Go 等价：

```rust
// 1. 读文件，比对 UUID 一致
// 2. remove_file(path)        ← 1、2 之间锁若被窃取并重建成新文件，
//                               unlink 会误删"新主人的锁"
```

**修复建议**：保持 fd 打开后，POSIX 可直接基于 fd（`F_GETLK`/`fstat` 配合唯一文件）或释放时把锁 `rename` 到一个唯一名字再删除（绝不 unlink 共享路径）；Windows 基于保持的句柄释放。UUID 比对必须保留，但它只解决"读到的时刻"，不解决"删除的时刻"。

### P1-3 打开时的残尾截断不持锁

`open → init_and_recover → load_with_recovery` 的截断自愈动作不经过 `FileLock`。考虑崩溃后两进程同时打开：

- 进程 A 恢复扫描发现坏帧在偏移 X，准备截断；
- 进程 B 恢复后立即追加（追加时持锁、seek 到文件末尾——注意是"垃圾字节之后"的末尾）；
- 进程 A 的截断执行，把 B 刚追加的帧连同垃圾一起截掉。

**修复建议**：WAL 打开的"残尾截断 healing"步骤放在 `FileLock` 内进行（获取锁 → 重读尾部 → 截断 → 释放），与追加路径使用同一把锁；只读打开不做截断时，可改为"内存中忽略残尾"而非直接报错（便于只读工具检查崩溃库）。

### P1-4 .NET 注册表键使用 `OrdinalIgnoreCase`，在 Linux 上错误合并不同文件

`Db.cs` 单例注册表：

```csharp
new ConcurrentDictionary<string, WeakReference<CkvDb>>(StringComparer.OrdinalIgnoreCase);
```

- Windows 文件系统不区分大小写，`OrdinalIgnoreCase` 合理；
- **Linux/macOS 路径区分大小写**，`/data/Foo.db` 与 `/data/foo.db` 是两个不同文件，会被错误地映射到同一个单例，后打开者拿到前一个库的实例。

**修复建议**：使用平台相关比较器（Windows 用 `OrdinalIgnoreCase`，其他平台用 `Ordinal`）。另外 .NET 的 `Path.GetFullPath` 不解析符号链接（Rust 用 `fs::canonicalize`），经 symlink 的同一文件会产生两个单例，建议改为 `RealPath` 等价逻辑（.NET 可用 `File.ResolveLinkTarget` 或直接读 realpath）。

### P1-5 Go / .NET 自动压实的"嵌套锁"与"句柄未关"

见 P0-1 中已详述的机制，这里归纳为独立缺陷：

- 自动压实的 `beginFlush` 在外层锁未释放时执行（Go 用 `defer Release` 要到函数末尾；.NET 在 `finally` 才 Dispose），只能等锁超时后**把自己的锁当僵死锁窃取**，默认每次压实白白停顿 10s；
- 外层追加句柄未关闭：Go 的 `syscall.Open` 不含 `FILE_SHARE_DELETE`、.NET 的 `FileShare.ReadWrite` 不含 Delete，Windows 上轮转 rename/move 必失败；
- Rust 正确地在压实前显式 `lock.release()`（L1707），且 std 在 Windows 上默认带 `FILE_SHARE_DELETE`，所以 Rust 没有嵌套锁/句柄问题（但仍有 P0-1 数据丢失）。

**修复建议**：三语言统一为"先释放锁、关闭追加句柄 → 再独立获取锁压实"（Rust 的锁时序），并且压实放在 `apply_mem` 之后；Go 端还要停止吞掉 `commitFlush` 错误。

---

## 四、P2 健壮性与性能问题

### P2-1 死字节未统计，`compaction_dead_ratio` 是死代码

Rust/Go/.NET 的 `Options` 都有 `compaction_dead_ratio`（默认 0.3），但**没有任何代码读取它**；同时也没有 `dead_bytes` 计数：

- 自动压实只看"文件大小 ≥ 阈值"：当存活数据自然增长到 4MB（没有任何垃圾）时也触发全量重写，产生无谓的写放大尖峰；
- 反过来，一个小库大量覆写/删除、垃圾占比极高但文件始终不到 4MB，就永远不会压实，WAL 无限膨胀。

**修复建议**：

1. 维护 `dead_bytes`：覆写时按"旧条目的编码长度（10B 头 + ns/key/value）"累加；删除按条目编码长度累加；`clear/drop_namespace` 按仓内全部条目编码长度累加；压实后归零；
2. 触发条件改为双条件：`file_size ≥ threshold && dead_bytes / file_size ≥ dead_ratio`；
3. 对始终很小的库可加"时间/操作数"兜底周期压实，避免长期不回收。

### P2-2 POSIX 上缺少目录 fsync

设计规格 §4.2 第 4 步要求：轮转 rename 完成后 fsync 父目录，否则断电后 rename 可能丢失（目录项是元数据）。Rust、Go 均未实现该步骤（Windows 按规范跳过）。

**修复建议**：Rust/Go 在轮转完成后以只读方式打开父目录并 `fsync`（Linux/macOS），失败降级为 warning；这是保证"轮转后状态持久"的必要动作。

### P2-3 恢复时不校验定长值类型的长度

`OP_PUT` 解码入库时不校验 value 长度与类型是否匹配（如 int64 不是 8B、bool 不是 1B、float64 不是 8B）。逻辑非法文件可通过 CRC 校验进入内存，错误延迟到用户读取该 key 时才暴露。

**修复建议**：在恢复解码时对定长类型做长度校验（不匹配即 Corrupted），并对 `value_type` 保留值（>0x04）报错。

### P2-4 `ClearNamespace` 逐键墓碑，设计文档的 0xFD/0xFE/0xFF 未落地

- `PHASE2-APPEND-LOG-COMPACTION-DESIGN` 设计了 ClearNamespace 等专用值类型标记，但实现是"每个 key 生成一个 `OP_DELETE`"；
- 对大命名空间的 clear 会产生巨大的帧，且磁盘上没有"清空"语义标记；
- 设计文档与 v3.2 帧格式在这几个编码上本身存在冲突，需要先对齐文档再决定实现。

**修复建议**：要么补充帧级语义操作码（`OP_CLEAR_NAMESPACE`）并在重放时识别，要么明确文档"Clear 以逐键墓碑表达"为最终方案并更新设计文档。

### P2-5 每次写都开/关文件、获取/释放锁，无组提交

WAL 路径每次写都 `OpenOptions::open`（设计文档原本要求"常驻追加句柄"），并伴随一次锁获取/释放等元数据操作，高并发小写吞吐受此限制。

**修复建议**：

1. 在持锁/写操作期间复用常驻追加句柄（锁重试验时再重开），Windows 上可借助 `FILE_SHARE_READ` + 锁文件独占；
2. 引入**组提交（group commit）**：并发写在一个帧里合并、一次 fsync 服务多笔写，Immediate 模式下吞吐提升明显；
3. 减少写路径上的克隆：`put_internal` 中 value 同时进入 `RawOpEntry` 与内存树，可在 WAL 追加后转移所有权，避免重复分配。

### P2-6 重复 open 时第二次的 options 被静默忽略

同一 path 已存在单例时，`open(path, opts)` 直接返回既有实例，新的 `readOnly / syncMode / threshold / lockTimeout` 全部静默失效，且无提示。

**修复建议**：要么在文档中固化"首个 open 的选项为准"并在关键选项冲突时返回 warning/错误，要么对只读等正交选项做实例级兼容处理；五个实现行为应统一。

### P2-7 Drop / Dispose / Timer 生命周期细节

- **Rust**：`Drop for DbInner` 中执行阻塞 I/O（`begin_flush/commit_flush`、锁获取），若锁正被窃取会阻塞；析构期间的错误只能被吞。建议文档强调"应显式 `close()`，Drop 仅为兜底"；
- **.NET**：`Dispose` 静默吞掉最终 flush 的错误，用户可能以为已持久化；建议至少经 `onWarning` 上报；
- **.NET / Kotlin / Dart**：同步 Timer 在构造函数中创建、在 `InitAndRecover` 完成之前就可能触发；当前因 `isDirty=false` 实际无害，但建议在恢复完成后再启动定时器。

---

## 五、跨语言一致性偏差

| 维度 | Rust | Go | .NET | Kotlin | Dart |
|---|---|---|---|---|---|
| 默认 sync_interval_ms | 100 | 100 | 100 | 100 | **50** |
| 默认 compaction_threshold | 4MB | 4MB | 4MB | **无该选项** | **0（关闭）** |
| 默认 lock_timeout_ms | 10000 | 10000 | 10000 | **5000** | **5000** |
| 自动压实 | 有（含 P0-1） | 有（失效，P0-1） | 有（失败，P0-1） | 无 | 有（默认关闭） |
| 空 key | 允许 | 允许 | 允许 | **拒绝** | 允许 |
| 磁盘新于内存 | 取 max 合并 | 取 max 合并 | 取 max 合并 | **抛 TransactionConflict** | 取 max 合并 |
| 错误命名 | `KeyTooLarge`/`ConcurrentModification` | 同 Rust | 同 Rust | **`KeyTooLong`/`TransactionConflict`** | 接近 Rust |
| 锁协议 | 32B 统一格式 | 32B 统一格式 | 32B 统一格式 | 32B 统一格式 | **完全不同**（见下） |

**Dart 锁协议需要专项对齐**：

- 用 `FileMode.write` 打开锁文件，该模式**会截断已存在的文件**（没有 create-new 排他创建），真正的互斥只靠随后的 `lockSync`（OS 锁）；
- 文件内容是文本（PID + ISO 时间），不是 32B 统一格式；
- 没有僵死判定、没有 `force_unlock`、没有 UUID/ABA 处理，释放时无条件删除；
- 优点是 OS 锁在进程崩溃时自动释放，崩溃检测反而可靠；但它与其他四种实现无法互操作，也无法表达"强制解锁/等待超时"语义。建议二选一：对齐 32B 协议，或明确文档"Dart 采用内核锁方案"并补齐 force/timeout 语义。

**Kotlin 其他偏差**：

- `clear(ns)` 直接别名 `dropNamespace`，语义上不保留命名空间，与其他实现不一致；
- `increment` 用普通 `+` 而非 Rust 的 `wrapping_add`，长计数器有溢出 panic 风险；
- 部分写路径在内存修改**之后**才 `beginFlush`（如 increment 的 Snapshot Immediate 分支），违反"校验先于修改"的两阶段协议。

**文档漂移**：`README.md` 语言矩阵仍写"Python/Node.js/Java 计划中"，未列出实际存在的 Dart/Kotlin，且实现语言清单与目录结构不符；建议更新。

---

## 六、改进路线图

### 第一阶段：发布前必须修复（P0）

1. **P0-1**：调整写路径顺序为"追加日志 → 应用内存 → 独立锁压实"；压实失败上抛；Go 取消错误吞没；
2. **P0-2**：实现 §3.4 中间坏块/残尾的鉴别扫描；
3. **P0-3**：Rust 去掉"先删后 rename"，改为原子覆盖；
4. 为以上三项补回归测试（见第七节）。

### 第二阶段：锁加固（P1）

5. POSIX fd + `fcntl/flock`、Windows 保持句柄、PID 活性探测（P1-1）；
6. 释放基于 fd/唯一 rename，消除 TOCTOU（P1-2）；
7. open 残尾截断纳入锁保护（P1-3）；
8. .NET 注册表比较器平台化、路径规范化（P1-4）；
9. 消除嵌套锁，统一三语言锁/句柄时序（P1-5）。

### 第三阶段：策略与性能（P2）

10. 死字节统计 + 双条件压实触发，让 `dead_ratio` 生效（P2-1）；
11. POSIX 目录 fsync（P2-2）；
12. 恢复时定长类型/保留值校验（P2-3）；
13. 明确 Clear 的磁盘表达并对齐文档（P2-4）；
14. 常驻句柄 + 组提交 + 写路径零拷贝（P2-5）；
15. 生命周期收尾：文档化重复 open 语义、Dispose 错误上报、Timer 恢复后启动（P2-6/P2-7）。

### 第四阶段：跨语言一致性

16. 统一默认值矩阵；Kotlin 空 key / clear / wrapping / 错误命名对齐；Dart 锁协议对齐或文档化；
17. 更新 README 与设计文档，消除"文档与实现不一致"；
18. 建立跨语言 golden 字节一致性 CI（同操作序列 → 五语言产出相同字节）。

---

## 七、建议补充的测试清单

| 编号 | 测试 | 覆盖缺陷 |
|---|---|---|
| T1 | 小阈值下连续写入，跨越阈值的那次写在**重开后仍存在**；压实后文件大小、key 全集断言 | P0-1 |
| T2 | 压实后立即重开，全量遍历所有 key（不只查最后一个） | P0-1 |
| T3 | 健康文件翻转首帧 1 字节（后有合法帧）→ **拒开**（Corrupted） | P0-2 |
| T4 | 健康文件截断尾部若干字节 → 截断自愈并保留其余数据 | P0-2 |
| T5 | 轮转过程中（可用文件锁/注入点）断言 `.bak` 始终存在 | P0-3 |
| T6 | 自动压实失败（如目标目录只读）时写操作返回错误，而非成功 | P0-1 / P1-5 |
| T7 | 压实后目录无残留 `.tmp`；重复触发压实不产生 tmp 堆积 | P0-1 / P1-5 |
| T8 | 两进程：持锁进程存活时第二进程不能窃取；持锁进程被杀死后能获取 | P1-1 |
| T9 | 模拟锁 owner 停顿 < timeout：不被窃取；崩溃后：被窃取 | P1-1 |
| T10 | open 截断与并发追加同时进行，追加帧不被截断 | P1-3 |
| T11 | Linux 上大小写不同路径分别打开得到不同实例 | P1-4 |
| T12 | 死字节统计：大量覆写/删除后按双条件触发；纯 live 增长不触发 | P2-1 |
| T13 | 恢复 int64=7B / bool=3B / 保留 vtype 的文件 → Corrupted | P2-3 |
| T14 | 五语言 golden：同一操作序列输出字节完全一致 | 一致性 |

**关键提醒**：现有压实测试全部走**显式 `Compact()`**（工作正常），而自动压实路径（阈值触发、且位于写路径内部）没有任何测试——P0-1 正是因此漏网。新增测试应直接驱动真实写入跨越阈值。

---

## 八、附：本次审查涉及的关键文件

| 语言 | 文件 |
|---|---|
| Rust | `src/rust/src/db.rs`（核心，写路径/恢复/压实）、`format.rs`（帧解析/恢复）、`lock.rs`（锁）、`options.rs`、`types.rs`、`errors.rs` |
| .NET | `src/dotnet/src/Ckv/Db.cs`（核心）、`LockFile.cs`、`Options.cs`、`BinaryHelper.cs`、`Constants.cs`、`Errors.cs`、`Types.cs` |
| Go | `src/go/db.go`、`format.go`、`lock.go`、`options.go` |
| Kotlin | `src/kotlin/src/main/kotlin/ckv/CkvDb.kt`、`Storage.kt`、`LockFile.kt`、`Options.kt` |
| Dart | `src/dart/lib/src/db.dart`、`storage.dart`、`format.dart`、`lock.dart`、`options.dart` |
| 设计文档 | `docs/KV-DB-DESIGN-SPEC.md`、`docs/PHASE2-APPEND-LOG-COMPACTION-DESIGN.md`、`docs/PHASE2-SYNCMODE-AND-WAL-SUMMARY-20261016.md`、`docs/CHANGES-20261016.md` |

---

*报告完。建议优先按第六节"第一阶段"修复，并在修复后用第七节 T1/T2/T3/T4/T6/T7 作为验收门槛。*
