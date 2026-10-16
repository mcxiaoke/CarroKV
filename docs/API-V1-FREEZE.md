# CKV API v1.0 冻结契约（Freeze Baseline）

- **状态**：Frozen — 后续任何签名/语义变更必须走 v2.0
- **生效日期**：2026-10-16
- **适用范围**：Rust / Go / .NET 三语言，以及后续 Python / Node.js / Java / Dart / Kotlin 移植
- **上位文档**：[KV-DB-DESIGN-SPEC.md](KV-DB-DESIGN-SPEC.md) v3.2（磁盘格式不变，本文只冻结**进程内 API 层**）

---

## 1. 冻结原则

1. **字节级磁盘格式不变**：32B 文件头 + 28B 帧头 + 贪心装箱 + TxID 单调，v3.2 已冻结，本文不触碰。
2. **错误体系同名同义**：基类 `DbError` + 8 个派生错误，见 §3。
3. **返回值为"是否命中"的语义三语言一致**，错误传播风格按语言习惯（Rust `Result` / Go `(T, error)` / .NET 异常），但**错误类型必须一一对应**。
4. **Watcher 事件四值 ABI 稳定**：`Put=1 / Delete=2 / DropNamespace=3 / Clear=4`，严禁重排。
5. **本文新增的 6 个 API 与 15 项一致性修复，是启动新语言移植的前置条件**。

---

## 2. 新增 API（6 个）

### 2.1 `Namespaces()` — 枚举命名空间

```
Rust:  pub fn namespaces(&self) -> Vec<String>
Go:    func (db *DB) Namespaces() []string
.NET:  public List<string> Namespaces()
```

- 按 **UTF-8 无符号字节序升序** 返回所有命名空间名。
- 默认命名空间返回 `""`（空串）。
- **空命名空间也列出**：`clear_namespace` 保留命名空间自身，因此"键已清空但 ns 仍在"必须可见。
- 非 UTF-8 字节构成的命名空间名（只能由外部文件引入）**跳过**，与 `keys()` 统一（见 §3.7）。
- 快照上同样提供：`Snapshot::namespaces()` / `Snapshot.Namespaces()`。

### 2.2 `Unwatch()` — 注销监听

```
Rust:  pub fn watch<F>(&self, cb: F) -> WatcherHandle
       // WatcherHandle::unwatch(&self)
Go:    func (db *DB) Watch(cb KeyChangeCallback) *Watcher
       // (*Watcher).Unwatch()
.NET:  public WatcherHandle Watch(Action<KeyChangeEvent> callback)
       // WatcherHandle : IDisposable，Dispose 时注销
```

- `watch()` 的返回类型从 `()` 改为句柄，**这是签名变更**，故必须在新语言移植前冻结。
- **注销一律显式**：只有调用 `unwatch()` / `Unwatch()` / `Dispose()` 才移除回调；
  单纯丢弃句柄**不**注销（Rust 刻意不实现 `Drop`），因此既有的 `db.watch(cb);`
  写法在 v1.0 中行为不变。
- 语言差异声明：Go 无可靠的 GC finalizer 资源清理通道，故不提供"丢弃即注销"；
  Rust/.NET 若要该语义，由调用方在 `Drop`/`Dispose` 自己的订阅者时调用 `unwatch`。
- 注销是幂等的；对已注销的句柄再次调用无副作用。
- 派发时使用**调用时刻的监听器快照**，与现有锁外派发铁律不变。

### 2.3 `Flush()` — 显式落盘

```
Rust:  pub fn flush(&self) -> Result<()>
Go:    func (db *DB) Flush() error
.NET:  public void Flush()
```

- 语义与 `compact()` 完全相同（按确定性排序重写为紧凑快照，`TxID = currentMaxTxID + 1`）。
- `compact()` **保留为 `Flush()` 的别名**，不删除（兼容既有调用方）。
- 只读模式下与其它写操作一致地返回 `LockedError` / 抛 `LockedError`。

### 2.4 `GetFileSize()` 与 `Stats()`

```
Rust:  pub fn file_size(&self) -> u64
       pub fn stats(&self) -> DbStats
Go:    func (db *DB) FileSize() int64
       func (db *DB) Stats() DbStats
.NET:  public long FileSize()
       public DbStats Stats()

DbStats { FileSize: u64, NamespaceCount: usize, TotalEntries: usize, CurrentMaxTxId: u64 }
```

- `FileSize()`：数据库文件当前物理字节数；文件不存在（尚未落盘的新库）返回 `0`。
- `Stats()`：纯内存统计，不触发磁盘 I/O、不修改任何状态。
- 快照上不提供（快照是时间点视图，文件大小无意义）。

### 2.5 `Backup(dest)` — 主动备份

```
Rust:  pub fn backup(&self, dest: &Path) -> Result<()>
Go:    func (db *DB) Backup(dest string) error
.NET:  public void Backup(string dest)
```

- 把当前内存状态序列化为一个独立的 `.db` 文件写入 `dest`，格式与正常库完全一致（可被任意语言直接打开）。
- 通过 `dest + ".tmp"` + fsync + 原子 rename 完成，**不触碰源库的 `.bak`**。
- **只读模式下允许执行**：备份不修改源库。
- 目标路径已存在则覆盖。

### 2.6 `iter_prefix(ns, prefix)` — 前缀迭代器（缓冲式）

```
Rust:  pub fn iter_prefix(&self, ns: &str, prefix: Option<&str>) -> Result<RangeIter>
Go:    func (db *DB) IterPrefix(ns string, prefix ...string) ([]Entry, error)   // 见下方说明
.NET:  public List<Entry> IterPrefix(string? ns, string? prefix = null)
```

- 按 UTF-8 无符号字节序逐个产出匹配前缀的条目。
- **明确为缓冲式语义**：内部一次性收集匹配条目后逐个返回。这是三语言一致的刻意选择——
  真空惰性迭代需要让迭代器持有存储指针，会把 CoW 生命周期管理泄漏到公共 API。
  超大命名空间请用 `scan_range` 分页。
- 命名统一为 `iter_prefix` / `IterPrefix`（Rust 原为 `iter_prefix`，Go/.NET 新增）。

---

## 3. 一致性修复契约（15 项）

### 3.1 错误体系命名对齐规范 §5.1.2（.NET）

| 规范名（新） | .NET 旧名 | 关系 |
| :--- | :--- | :--- |
| `DbError` | `CkvException` | `CkvException : DbError`（旧基类保留） |
| `CorruptedError` | `CorruptedDatabaseException` | `CorruptedError : CorruptedDatabaseException` |
| `LockedError` | `DatabaseLockedException` | `LockedError : DatabaseLockedException` |
| `ConcurrentModificationError` | `ConcurrentModificationException` | 同上 |
| `InvalidUtf8Error` | `InvalidUtf8Exception` | 同上 |
| `KeyTooLargeError` | `KeyTooLargeException` | 同上 |
| `InvalidNamespaceError` | `InvalidNamespaceException` | 同上 |
| `UnsupportedVersionError` | `UnsupportedVersionException` | 同上 |
| `TypeMismatchError` | `TypeMismatchException` | 同上 |

**继承链方向是刻意的（关键）**：旧名位于新名**之上**，即

```
Exception → DbError → CkvException → CorruptedDatabaseException → CorruptedError
```

这样既有的 `catch (CkvException)` 与 `catch (CorruptedDatabaseException)` 在抛出新名
实例时**仍然命中**，实现真正的源码兼容（C# 中 `catch` 只能捕获自身类型或其子类，
若把旧名做成新名的子类则完全捕获不到，兼容性为零）。

- 旧名保留 `[Obsolete]`，一个弃用周期后删除。
- **例外声明（写入规范）**：C# 以 `Error` 结尾的类型名违反 FxCop/CAxxxx 命名惯例，
  本项目显式接受此偏离，以保证 6 语言错误名逐字一致。
- 测试注意：xUnit 的 `Assert.Throws<T>` 要求**精确类型匹配**，验证基类需用
  `Assert.ThrowsAny<T>`。

### 3.2 `Snapshot()` 取锁级别（Go）

`DB.Snapshot()` 由写锁改为**读锁**。快照创建只读 `namespaces` map 与快照计数，
不需要独占。Rust（`inner.read()`）与 .NET（`EnterReadLock`）本就是读锁。

### 3.3 只读模式写操作的错误类型（.NET）

由 `InvalidOperationException` 改为 **`LockedError`**，与 Rust `DbError::Locked` /
Go `LockedError` 一致。`InvalidOperationException` 仅保留给真正的参数/状态误用。

### 3.4 Value 超限的错误类型（.NET）

Value 超过 16 MB 时由通用 `CkvException` 改为 **`KeyTooLargeError`**。
（Key 超限原本就是 `KeyTooLargeError`，两侧原本不对称。）

### 3.5 `BatchWriter` 长度校验（.NET）

`BatchWriter.PutBytes/PutString/PutInt64/PutFloat64/PutBool` 与 `Delete` 在**构造期**
即校验 Key ≤ 65535、Value ≤ 16 MB，抛 `KeyTooLargeError`。
Rust `Batch::put_*` / Go `BatchWriter.*` 已如此，.NET 补齐。

### 3.6 Watcher 回调异常传播（.NET）

去掉 `DispatchEvents` 中的 `try { callback(ev); } catch { }`，**异常向上传播**。
Rust（panic）/ Go（panic）本就不吞。这是刻意的行为统一：静默吞异常会让订阅方的
bug 永远不可见。文档化：**Watcher 回调中的异常会中断后续监听器的派发并传播给写操作调用方**。

### 3.7 非 UTF-8 Key 在 `keys()` 中的行为（三语言）

统一为 **跳过**（Rust 现状）。

- 背景：公共 API 只接受 `string` key，非 UTF-8 字节构成的 key 只能由外部构造的文件引入。
- Go 原样返回字节串（调用方拿到无法安全 round-trip 的 string）、
  .NET 替换为 U+FFFD（调用方拿到的 key 与磁盘不符，`get` 必然失败）。
- 两者都会造成"列出来的 key 读不回来"，跳过是唯一自洽的选择。
- `Namespaces()` 同样适用（§2.1）。

### 3.8 快照关闭后的读语义（三语言）

统一为 **返回"不存在"，不抛错**。

| 语言 | 旧行为 | 新行为 |
| :--- | :--- | :--- |
| Rust | 无 `Close`（Drop 释放） | 保持无 `Close`；文档化 Drop 即释放 |
| Go | `Close()` 后静默返回空 | 保持 |
| .NET | `Dispose()` 后抛 `ObjectDisposedException` | 改为返回空/null |

理由：快照是只读视图，"关闭后查询"返回空比抛错更温和，且三语言可一致。

### 3.9 Go `ScanRange` 签名统一

`Snapshot.ScanRange` 由 `(ns string, startKey, endKey string)` 改为
`(ns string, startKey, endKey *string)`，与 `DB.ScanRange` 一致。
`nil` 表示无界，空串 `""` 是有效的边界键（注意与旧语义的区别，写入规范）。

### 3.10 Go `renameWithRetry` 只对共享冲突重试

由"对所有错误重试 5 次"改为**仅对 Windows `WinError 5 / 32 / 33` 重试**，
其余错误立即返回，与 Rust `rename_with_retry` 一致。重试序列保持 5/15/30/60/120ms。

### 3.11 Rust `canonicalize_path` 规范化

`"t.db"` 与 `"./t.db"` 必须生成同一注册表键。
根因：文件不存在时 `Path::new("t.db").parent()` 为 `Some("")`，`canonicalize("")` 失败后
回退为空串，拼出相对路径；而 `"./t.db"` 的 parent 是 `"."`，可 canonicalize 成绝对路径。
修复：parent 为空或相对时统一以 `"."` 为基准 canonicalize。

### 3.12 同路径重复 `Open` 的行为（Go）

统一为 **返回同一实例（进程内单例）**，`options` 以首次打开为准。

- Rust / .NET 已如此（Weak 注册表，Drop/Dispose 自动清理）。
- Go 新增注册表：`sync.Map` 存 `*DB`，`Close()` 时移除。
- **语言差异声明（写入规范）**：Go 无标准库弱引用，注册表条目依赖显式 `Close()` 释放；
  未 `Close` 的同路径再次 `Open` 会返回旧实例。Rust/.NET 由 `Drop`/`Dispose` 自动清理。

### 3.13 Rust `lock_timeout_ms` 类型

由 `u64` 改为 **`u32`**，对齐规范 §5.1.1 与 Go/.NET。

### 3.14 `increment_i64` 溢出语义（文档化）

三语言均为补码绕回（Rust `wrapping_add` / Go `int64` 自然回绕 / .NET `unchecked`）。
**行为已一致，仅补文档**，不生成 `OverflowError`。

### 3.15 readOnly 打开时的 `.bak` 恢复（**D1-9 修订**，2026-10-16）

**旧行为（已作废）**：`readOnly=true` 打开损坏主库时会执行一次 `copy` 从 `.bak` 恢复并**写盘**，
原口径是"只读的准确边界是'不修改用户的逻辑数据'，不是'零磁盘写入'"。

**新行为（依据 `DECISION-20261016-wal-recovery-and-compaction.md` D1-9）**：

- `readOnly=true` 打开损坏主库 → 抛 `CorruptedError`，**且不修改 `.db` / `.bak` 数据文件**；
- `autoRecoverFromBackup` 在只读模式下**不生效**（即使显式设为 `true`）；
- 非只读模式下该开关**默认 `false`**（D1-1），仅在显式开启时回退 `.bak`；
- 锁文件仍可写（LMDB 文档同样明确"只读模式也需要写锁文件"）。

> **API 状态说明**：CKV v1.0 **尚未正式发布**，本修订属**发布前基线调整**，
> 不触发 v2.0 冻结流程；五语言需同步对齐（见 `DECISION` 文档头部 API 约束）。

### 3.16 后台刷盘短锁机制与显式 `close()` 契约（C16）

**短锁落盘**：
在 `SyncModeDeferred` 模式下，后台工作线程落盘执行两阶段处理：
1. **短读锁提取快照**：仅在极短的只读锁内克隆/提取当前内存状态与 TxID，立即释放读锁；
2. **锁外执行磁盘 I/O**：序列化写入 `.tmp` 文件与 `fsync` 磁盘同步完全在锁外进行，并发查询（`Get` / `Scan`）不受文件 I/O 阻塞；
3. **短写锁原子轮转**：写入并同步完成后，获取短写锁执行覆盖式重命名原子轮转（`.tmp` -> `.db`，上一代保留为 `.bak`）并刷新 `lastKnownDiskState`。

**显式 `close()` / `Dispose()` 强约束**：
- `Deferred` 与 `Manual` 同步模式下，写入操作只更新内存状态并置脏，由后台定时器或调用方显式刷盘；
- 应用程序退出前**必须显式调用 `close()` / `Dispose()`**。`close()` 负责立即唤醒并终止后台线程（通过 Condvar / Channel / WaitHandle 唤醒，避免等待整个 interval 挂死），并对残留的脏数据执行最终同步刷盘与未决压实重试；
- 严禁依赖运行时的隐式析构或 GC 终止器（例如移动端后台进程被系统直接杀掉或划卡退出，不会触发析构）。

### 3.17 追加写句柄生命周期与组提交削减（C15 决策）

- **削减组提交**：嵌入式单进程单写者无组提交收益（SQLite / LMDB / bbolt 均无此机制），正式削减；
- **维持按需轻量句柄**：每次写操作按需打开句柄并在操作结束（或 fsync）后随作用域关闭。此设计彻底规避了 Windows 平台下常驻打开句柄对 Compaction 重命名轮转造成的 `WinError 32` 共享冲突；经实测非强制 fsync 模式下单核吞吐已超 68 万 ops/s，无需引入复杂的常驻句柄生命周期状态机。

---

## 4. 兼容性影响汇总

| 变更 | 破坏性 | 处理 |
| :--- | :--- | :--- |
| `watch()` 返回值 `()` → 句柄 | 源码兼容（忽略返回值即可编译） | 无 |
| Go `Snapshot.ScanRange` 签名 `string` → `*string` | **破坏** | 同步修改调用方；移植尚未开始，成本为零 |
| .NET 异常类型改名 | 源码不兼容（catch 旧名失效） | `[Obsolete]` 别名类继承新类，`catch (旧名)` 仍可编译并生效 |
| Rust `lock_timeout_ms` `u64` → `u32` | 源码不兼容（字面量推断通常无感） | 同步修改 |
| Go 同路径重复 `Open` 返回单例 | **行为变更** | 文档化 + 测试锁定 |
| `compact()` → `Flush()` 别名 | 无 | 保留 |
| 新增 `autoRecoverFromBackup`（Rust / Go），三语言默认值 `true` → `false` | **行为变更**：损坏主库不再自动回退 `.bak`，改为抛 `CorruptedError`（D1） | 文档化 + 测试锁定新默认（`fault_injection_test`、`api_v1_freeze_test`） |
| readOnly 打开损坏主库由"回退 `.bak` 并写盘"改为"抛 `CorruptedError` 且不写盘" | **行为变更**（§3.15） | 文档化 + 测试锁定（D1-9） |
| C16 后台刷盘改用短锁 + 退出条件变量唤醒 | 无破坏性性能提升 | 文档化 + 强调应用退出须显式 close() |
| C15 削减组提交，确认维持按需轻量追加写句柄 | 无 | 文档化收敛 |

---

## 5. 验收标准（新语言移植的准入门槛）

1. 三语言全量测试通过，且每个本文条目都有对应用例。
2. 跨语言 interop 测试通过（新语言写出的文件被两种旧语言读，反之亦然）。
3. 黄金向量（`testdata/*.db`）在新语言上字节级复现。
4. 错误类型名与规范 §5.1.2 逐字一致。
