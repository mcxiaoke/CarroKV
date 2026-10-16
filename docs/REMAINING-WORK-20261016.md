# CKV 剩余工作清单

> 生成日期：2026-10-16 09:29
> 前置状态：写路径原子性缺陷（B1~B6）已修复；API v1.0 冻结契约已建立。
> 三语言测试 Rust 59 / Go 42 / .NET 60 全绿，工作区干净。
> 本文是 [GAP-ANALYSIS-20261015.md](GAP-ANALYSIS-20261015.md) 的可执行拆解。

---

## 进度更新（2026-10-16 12:22）

**已完成（见 [API-V1-FREEZE.md](API-V1-FREEZE.md)、[PHASE2-SYNCMODE-AND-WAL-SUMMARY-20261016.md](PHASE2-SYNCMODE-AND-WAL-SUMMARY-20261016.md) 与 [CHANGES-20261016.md](CHANGES-20261016.md)）**

- ✅ **第 3 节 15 项一致性修复**（N1/N2/N3 + P2-a ~ P2-l）—— 全部完成，三语言同构测试覆盖
- ✅ **第 4 节 6 个新 API**（F7 `Namespaces` / F8 `Unwatch` / F9 `Flush` /
  F10 `GetFileSize`+`Stats` / F11 `Backup` / F12 `IterPrefix`）—— 全部完成并冻结
- ✅ **F5 共享黄金向量资产** —— `tools/gen_testdata.py`（独立第三方生成器）+ `testdata/*.db`
  + 三语言 testdata 测试（解析外部文件 + 本地重生成字节比对）
- ✅ **阶段二 SyncMode 延迟落盘与写合并** —— 三语言支持 `Immediate`、`Deferred` (50ms后台写合并)、`Manual`
- ✅ **阶段二 AppendOnlyLog (WAL) 增量日志追加写引擎** —— 三语言支持增量 Frame 追加、断电坏尾自愈物理截断 (Tail Truncation) 与 Compaction 紧凑压实重写
- ✅ **全模式性能基准评测矩阵与实证报告** —— WAF 降低 98.8%（54x~107x+ $\to$ 1.28x），物理强持久化吞吐提升 47%~76%，延迟压低至 2.1~2.2ms，Compaction 压实缩减 99.9%

**当前正进行**

- 🚀 **第 7 步多语言生态拓展**：正式启动 **Dart 移植**（Flutter/移动端）与 **Kotlin 移植**（Android/KMP）

**待定/后续**

- ⏳ D1 LICENSE / D2 仓库组织名
- ⏳ F1~F4、F6：LICENSE、CI、go.mod 路径、包元数据、Rust/Go README

---

## 0. 当前状态

**可以放心不动、已可冻结的部分**

- 磁盘二进制格式 v3.2（32B 头 + 28B 帧头 + 贪心装箱 + TxID 单调 + `NsLen=0` 默认命名空间）
- 内联 CRC32（三语言向量一致）
- CoW 快照读无锁、Watcher 锁外派发铁律
- 两代原子轮转与 `.bak` 自愈链（.NET 本轮已补齐原子性）
- 写路径的「先校验后落盘」两段式结构

**新发现（本轮代码核对确认，此前 review 未记录）**

| # | 问题 | 位置 |
| :-- | :--- | :--- |
| N1 | .NET 错误基类叫 `CkvException`、损坏错误叫 `CorruptedDatabaseException`，规范 §5.1.2 要求 `DbError` / `CorruptedError` | `src/dotnet/src/Ckv/Errors.cs:5,11` |
| N2 | Go `DB.Snapshot()` 取**写锁**，Rust/.NET 取读锁；快照创建被不必要地串行化 | `src/go/db.go:1307` |
| N3 | .NET 只读实例写操作抛 `InvalidOperationException`，Rust/Go 抛 `LockedError`（本轮 B1 修复未统一此项） | `src/dotnet/src/Ckv/Db.cs` `BeginFlush` |

---

## 1. 需要你拍板的三个决策（都阻塞后续）

| # | 决策 | 为什么阻塞 |
| :-- | :--- | :--- |
| D1 | **LICENSE 类型**（MIT / Apache-2.0 / BSD-3-Clause） | 开源发布的法律前提；也决定 CI 里要不要跑 license-check |
| D2 | **仓库组织名**（`go.mod` 的 module path、`Cargo.toml` 的 `repository`、README 里的相对/绝对链接都依赖它） | 现在 `go.mod` 是 `module ckv`，不是可发布 import path |
| D3 | **SyncMode 延迟落盘现在做还是延后** | 它是阶段二的低成本替代方案；现在做会在冻结前再开一次 API，延后则高频写场景继续用 350ms 级延迟 |

---

## 2. P0 发布工程（缺了无法发布）

| # | 事项 | 涉及 | 说明 |
| :-- | :--- | :--- | :--- |
| F1 | **LICENSE** | 根目录 | 依赖 D1 |
| F2 | **CI** | `.github/workflows/` | 三语言 `test` 矩阵（`cargo test` + `cargo clippy` + `go test`/`go vet` + `dotnet test` 双目标）。后续 4 种语言没有 CI 几乎不可能保持契约一致 |
| F3 | **go.mod 模块路径** | `src/go/go.mod` | 依赖 D2。建议 `github.com/<org>/ckv/go`，用 `go/vX.Y.Z` tag 发布子目录模块 |
| F4 | **Cargo.toml / NuGet 元数据** | `src/rust/Cargo.toml`、`src/dotnet/src/Ckv/Ckv.csproj` | 补 `license`/`repository`/`keywords`/`PackageId`/`PackageLicenseExpression` |
| F5 | **黄金向量抽成 `testdata/*.db` 共享资产** | 新增 `testdata/` + 三语言各加一个"读文件"测试 | 现在向量以十六进制硬编码在三处测试里，每种新语言都要复制粘贴一遍。这是多语言重复劳动里最容易消除的一项 |
| F6 | **Rust / Go README + API 文档** | `src/rust/README.md`、`src/go/README.md` | 只有 .NET 有。需写明 §4 列出的已知限制与语义 |

---

## 3. P1 API 缺口（**必须在新语言移植前决定，否则又要做一次破坏性修复**）

这是「KeyChangeOp 三语言三种形态」的重演风险点：现在不加，4 种新语言会各写各的形态，然后 v2 再统一一次。

| # | API | 为什么真实需要 | 设计要点 |
| :-- | :--- | :--- | :--- |
| F7 | `Namespaces()` | 有一等公民的 namespace 概念却无法列出；管理界面/诊断必备 | 按 UTF-8 字节序返回；空命名空间（键已清空但 ns 仍在）是否列出需定义 |
| F8 | `Unwatch()` | `watch()` 注册后无法移除。长生命周期 DB + 短生命周期订阅者必然泄漏，.NET 上会回调到已释放对象 | 需返回句柄或 id；建议返回 `WatcherHandle` 并带 `Drop`/`Dispose` 自动注销 |
| F9 | `Flush()` | 现在只有"每次写立即全量落盘"一种模式，没有手动/批量落盘出口 | 与 D3 的 SyncMode 天然耦合，建议一起设计 |
| F10 | `GetFileSize()` / `Stats()` | 阶段二设计文档 §4.2 已引用但三语言都没实现；观测与调优需要 | 返回文件字节数 + 存活条目数 + `currentMaxTxID` |
| F11 | `Backup(path)` / 导出导入 | 只能靠 `.bak` 隐式轮转，用户无法主动做可带走的备份 | 基于现有 `compact()` 的确定性序列化，实现成本低 |
| F12 | 真·懒迭代器 | `scanRange`/`keys` 三语言都全量 materialize，大 ns 直接爆内存 | Rust 的 `iter_prefix` 目前也是先收集完再迭代，名不副实 |

---

## 4. P2 一致性 / 语义分叉（新语言移植前应定，成本低）

| # | 问题 | 语言 | 处理方式 |
| :-- | :--- | :--- | :--- |
| N1 | 错误基类与损坏错误命名偏离规范 | .NET | 加别名类或改文档声明 C# 惯例，需定一种 |
| N2 | `Snapshot()` 取写锁 | Go | 改读锁（`begin` 段只需读 `namespaces` map 与计数） |
| N3 | 只读写操作错误类型 | .NET | 与 N1 一起定为 `LockedError` 或保持异常并文档化 |
| P2-a | `.NET` Value 超 16MB 抛通用 `CkvException` 而非 `KeyTooLargeException` | .NET | 改具体类型 |
| P2-b | `.NET` Batch 构造不校验 key/value 长度（Rust/Go 的 builder 都校验） | .NET | 补校验 |
| P2-c | 非 UTF-8 key 的 `keys()`：Rust 静默跳过 / Go 原样返回 / .NET 替换 U+FFFD | 三方 | 定义统一行为并文档化 |
| P2-d | Watcher 回调异常：.NET `try{}catch{}` 静默吞 / Rust/Go panic 传播 | .NET | 统一（建议都不吞，交由调用方） |
| P2-e | Snapshot 关闭语义：Go `Close()` 后读静默返空 / .NET 抛 `ObjectDisposedException` / Rust 无 Close | 三方 | 文档化 |
| P2-f | Go `DB.ScanRange(*string)` vs `Snapshot.ScanRange(string)` | Go | 统一签名 |
| P2-g | `renameWithRetry` 对所有错误无脑重试 5 次（含权限不足等永久错误）；Rust 仅对 WinError 5/32/33 重试 | Go | 对齐 Rust |
| P2-h | Rust `canonicalize_path` 对 `"t.db"` 与 `"./t.db"` 生成不同注册表键，同进程双实例互不知晓 | Rust | 补规范化 |
| P2-i | 同路径重复 open：Rust/.NET 返回单例（第二次 options 被忽略）/ Go 允许双开 | 三方 | 文档化或统一 |
| P2-j | `lock_timeout_ms` 类型 Rust 用 `u64`，规范/Go/.NET 为 `uint32` | Rust | 改类型 |
| P2-k | `increment_i64` 溢出按补码绕回（三语言行为一致但未写入文档） | 三方 | 文档化 |
| P2-l | readOnly 打开时 `.bak` 恢复仍会写磁盘 | 三方 | 文档化"只读"边界 |

---

## 5. P3 增强（替代阶段二）

| # | 事项 | 涉及 |
| :-- | :--- | :--- |
| F13 | **`SyncMode` = Immediate / Deferred / Manual + 写合并** | 三语言各加一个选项、一个后台 flush 定时器；不动磁盘格式、不动恢复算法、不加 OpCode、不常驻句柄 |

详见 GAP-ANALYSIS §4.5。**这是高频写问题的正解**，成本约为阶段二的十分之一，且可靠性损失是显式可选的。

---

## 6. 建议执行顺序

| 顺序 | 事项 | 阻塞关系 |
| :-- | :--- | :--- |
| 1 | **D1/D2/D3 决策** | 阻塞 2、3、5 |
| 2 | F1~F4 + F6（LICENSE、CI、模块路径、包元数据、README） | 不阻塞代码 |
| 3 | N1~N3 + P2-a~P2-l（一致性分叉，共 16 项，均为小改动） | **阻塞新语言移植** |
| 4 | F7~F12（六个新 API，**必须与 3 一起冻结进契约**） | **阻塞新语言移植** |
| 5 | F5（`testdata/` 共享向量资产） | 新语言移植的验收基础设施 |
| 6 | 冻结 API v1.0，写统一 API 文档（含全部"已知限制/待文档化"语义） | — |
| 7 | 启动 Python / Node / Java 移植，以冻结契约为验收标准 | — |
| 8 | F13（SyncMode） | 可独立于 7 并行 |
| — | **阶段二 Append-Only WAL** | **暂缓**，见 GAP-ANALYSIS §4.6 的裁剪版条件 |

---

## 7. 明确不做（写进"非目标"）

- TTL / 键过期、压缩、加密、mmap —— 与"零依赖、纳秒级读"定位冲突
- 多进程并发读写 —— 规范 §1.1 已排除，靠"检测到即报错"兜底
- Python / Node / Java / Dart / Kotlin 本身 —— 按你的要求不计入缺口
