# ADR-20261017: 进程内共享实例所有权模型与生命周期决策

> **状态**：已接受 (Accepted)  
> **日期**：2026-10-17  
> **决策者**：CarroKV 核心维护团队  
> **相关议题**：P0-4（任一借用者 Dispose 销毁共享单例）、P1-12（关闭后读未硬化）、P1-13（Dispose 销毁读写锁）、P1-18（终结器防护缺失）、§7.4（备份范式与静态 BackupFile）

---

## 1. 背景与问题陈述

在真实桌面应用 **StickyNotesDesktop**（WPF 便签应用，.NET 8）从 SQLite 迁移到 CarroKV 的实际运行中，发生了一起致命的线上启动崩溃（见审查报告 §1.1.1 现场证据）：

1. 应用启动时触发后台异步日常冷备份：`using var db = CkvDb.Open(path, opts); db.Backup(dest);`；
2. 由于 CarroKV 设计了同进程同路径单例注册表（`Registry`），`Open()` 返回了主业务正在使用的同一个共享实例；
3. 备份完成后退出 `using` 作用域，触发 `db.Dispose()`，把全局共享实例**就地物理关闭**（执行了最终刷盘、释放了跨进程文件锁，并调用了 `_rwLock.Dispose()`）；
4. 仅 **2ms** 后，主线程调用 `db.ScanRange(...)` 恢复桌面便签，访问已被销毁的 `ReaderWriterLockSlim`，立即抛出 `ObjectDisposedException`，应用启动中止。

在 .NET 规范中，**“凡是实现了 `IDisposable` 的类型，调用工厂方法（如 `Open()`）的调用方就有责任使用 `using var db = ...` 负责生命周期释放”**。这是 .NET 开发者的标准心智习惯。

然而，CarroKV 的原实现只引入了弱引用单例，解决的是“GC 可回收”，**却完全没有所有权模型与引用计数**。API 暴露 `CkvDb : IDisposable` 强烈暗示调用方可以 `using`，二者产生剧烈冲突，属于典型的“所有权陷阱（Ownership Trap）”。

---

## 2. 同类优秀库的调研与事实证据

通过对 `temp/github` 下同类优秀库的源码调研，各成熟系统在多模块打开与生命周期上的处理范式如下：

1. **Microsoft FASTER / Garnet (.NET / C#)**：
   - 采用**分层模型（Core vs Session）**：底层的 `FasterKV : IDisposable` 作为长期持有者；上层业务通过 `NewSession()` 创建独立的轻量会话句柄 `ClientSession : IDisposable`。
   - 上层使用自然的 `using var session = db.NewSession()`，退出时只释放该会话的局部上下文，绝不关闭底层引擎；底层引擎在容器停止或应用退出时由 Owner 单独释放。
2. **LMDB / Lightning.NET (.NET / C#)**：
   - LMDB 规范严禁单进程重复打开同一环境。Lightning.NET 不在内部搞静默弱引用，由应用层统一管理单例；
   - `Dispose()` 采用内部原子标记防御，保证**绝对幂等**；实现了终结器检测漏释放。
3. **Hive_ce (Dart)**：
   - 采用类似 CarroKV 原设计的全局单例表 `_boxes[name]`，无引用计数。
   - `close()` 直接关闭底层存储，官方文档显式警告：*"Be careful, this closes all instances of this box..."*。
   - 社区常年出现“某个 UI 组件 dispose 时 close 了 Box，导致全局其他组件报 `Box has already been closed`”的典型问题，证明了裸单例模式的缺陷。
4. **redb / fjall (Rust)**：
   - 采用 `pub struct Keyspace(Arc<KeyspaceInner>)` 引用计数句柄包装模型。
   - 任何克隆操作只是递增 `Arc` 计数，每个句柄的 Drop 自动递减；最后一个句柄被释放时，底层引擎才真正关闭并落盘。

---

## 3. 核心决策冲突：`close()` 幂等语义 vs 引用计数的取舍

CarroKV 的基线规范（`API-V1-FREEZE.md:261`）承诺：`close()` 本身幂等。

然而，在引用计数语义中：
- 每次 `Open` 计数 +1；
- 每次 `Dispose` 计数 -1；
- 若同一个调用者重复调用 `db.Dispose()`（例如显式调了一次，又外包了一层 `using`），如果计数再次 -1，就会**偷扣并发其他模块的配额**，导致底层库被提前误杀！
- 反之，如果同一个裸对象只有一个 `_isDisposed` 状态标志，一旦模块 B 调了 `Dispose()` 置为 true，模块 A 手里的同一个对象也立刻变成 `_isDisposed == true`，引用计数完全失效。

### 决策方案评估

- **方案 A（纯裸单例 + 引用计数）**：返回同一对象指针，内部记 `_refCount`。无法兼顾单句柄重复 `Dispose()` 的幂等性与跨模块隔离，已否决。
- **方案 B（显式区分所有权）**：`Open()` 仅 Owner 可调，另增 `GetShared()` 返回禁止 Dispose 的包装。破坏 C# 惯用模式，对既有代码侵入大，已否决。
- **方案 C（句柄独立包装模型 / Handle Wrapper Model）**：**【采纳】**
  - 将底层物理持久化引擎抽象为共享的 `DbCore`（或核心状态机）；
  - 对外暴露的 `CkvDb` 作为轻量句柄门面（Facade Handle），实现 `IDisposable`；
  - 每次调用 `CkvDb.Open(path, options)`：
    - 命中注册表时，获取活跃的 `DbCore`，并原子递增其核心引用计数；
    - 每次构造并返回一个新的轻量 `CkvDb` 句柄实例：`new CkvDb(sharedCore)`；
  - **解耦幂等性与引用计数**：
    1. **句柄级独立幂等**：每个 `CkvDb` 拥有私有的 `private int _disposed = 0` 单次闩锁。该句柄重复调用 `Dispose()` 100 次，也只会在首次触发时扣减一次底层 `DbCore` 计数，后续调用均为无害 No-op；
    2. **内核级引用计数安全**：底层 `DbCore` 的引用计数严格等于活跃存活的 `CkvDb` 句柄数；仅当最后一个句柄被释放（计数归零）时，才执行最终刷盘与释放跨进程文件锁；
    3. **零类型破坏**：调用方看到的公开类型依然是 `CkvDb`，原有的所有方法、签名、泛型和测试完全保持兼容。

---

## 4. 关闭后读写统一契约（P1-12 统一）

针对规范两处历史矛盾（`API-V1-FREEZE.md:261` 曾允许读操作反映旧快照 vs 现场崩溃抛 `ObjectDisposedException`）：

1. **写操作**：句柄已 Disposed，立即抛出 `LockedError("Database is closed: writes are refused after Dispose()")`（跨语言统一）；
2. **读操作**：句柄已 Disposed，**立即抛出异常（.NET 抛 `ObjectDisposedException` 或 `LockedError`）**。
   - **理由**：若在关闭后读操作静默返回 `null`，调用方会误以为“该键不存在”，从而导致业务误写默认值或误判数据丢失；明确抛错符合 .NET / Go 最佳实践，能第一时间暴露已关闭后误用的 Bug。
3. **读写锁物理保护（P1-13）**：
   - 彻底删除 `Db.Dispose()` 中对 `_rwLock.Dispose()` 的调用。
   - `ReaderWriterLockSlim` 是托管并发原语，在多线程并发可能正在使用或等待时销毁会触发未定义行为；不显式销毁 RWLS 零开销且杜绝撕裂。

---

## 5. 终结器与未正常释放防护（P1-18 落地）

1. **`CkvDb` / `DbCore` 终结器**：
   - 若实例未被显式 `Dispose()` 且处于脏状态（`_isDirty && !_options.ReadOnly`）时被 GC 垃圾回收，在终结器中通过 `OnWarning` 回调抛出 `"instance was garbage-collected without Dispose; pending writes lost"` 告警；
   - 终结器内严禁进行磁盘 I/O 或等待锁，仅发出告警并打点。
2. **`LockFile` 终结器与 SafeHandle**：
   - `LockFile` 增加终结器 `~LockFile() => Dispose(false);`，内部由 `SafeFileHandle` 确保在实例发生 GC 时可靠归还跨进程内核排他锁，杜绝遗留句柄造成同名库其他进程打开 10 秒超时。

---

## 6. 备份 API 人体工学（§7.4 落地）

1. **在线备份**：`db.Backup(dest)` 作为官方唯一推荐写法，在已有连接上执行原子快照导出；
2. **离线与脚本备份便捷接口**：新增静态方法 `CkvDb.BackupFile(sourcePath, destPath, options = null)`：
   - 内部优先复用当前进程注册表中的活跃实例，安全导出且**绝不 Dispose**；
   - 若进程内无活跃连接，则内部独占打开后导出并安全释放。
   - 彻底消除调用方自行编写 `using var db = CkvDb.Open(src); db.Backup(dst);` 踩坑的可能。

---

## 7. 跨语言对齐规划

- **.NET**：批次二首发完整实现（条目 14~17）；
- **Go / Dart / Kotlin**：
  - Dart 与 Kotlin 同构跟进句柄包装与引用计数（批次三）；
  - Go 侧在 `*DB` 外增加会话包装或显式 `Close()` 配对计数（批次三）；
  - Rust 维持 `Arc<RwLock<DbInner>>` 固有天然安全机制。
