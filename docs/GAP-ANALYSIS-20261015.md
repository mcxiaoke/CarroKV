# CKV 现状缺口、问题清单与阶段二（Append-Only WAL）必要性评估

> 评估日期：2026-10-15
> 评估范围：`src/rust`、`src/go`、`src/dotnet` 全部源码（三语言共约 12,300 行）+ 实跑探针验证
> 口径：未在 README 矩阵中标注"已就绪"的语言（Python / Node.js / Java 等）**不计入缺口**
> 配套文档：`docs/KV-DB-DESIGN-SPEC.md`（v3.2）、`docs/PHASE2-APPEND-LOG-COMPACTION-DESIGN.md`、
> `docs/REVIEW-20261015.md`、`docs/REVIEW-20261015-1802.md`

---

## 一、总体结论

| 维度 | 结论 |
| :--- | :--- |
| 磁盘二进制格式 v3.2 | ✅ 最稳的资产，可冻结 |
| 崩溃恢复模型（阶段一） | ✅ 极简可证，是全项目最值钱的部分 |
| 进程内 API 契约 | ⚠️ 前两轮 review 的 P0/P1 已修完，但仍有 3 个**实测确认的同根因 bug** |
| 真实功能缺口 | ⚠️ 发布工程项（LICENSE/CI/共享测试资产）+ 5 个 API 缺口 |
| **阶段二是否有必要现在做** | ❌ **不建议**。收益与定位重叠度低，可靠性实质下降，工作量是最大放大器 |

---

## 二、还缺哪些"真实需要"的功能

### 2.1 P0 —— 缺了无法交付 / 会拖垮后续多语言

| # | 缺口 | 现状 | 为什么真实需要 |
| :-- | :--- | :--- | :--- |
| F1 | **跨语言共享黄金测试资产 `testdata/*.db`** | 黄金向量以十六进制硬编码在各语言测试里（Rust `golden_test.rs`、Go `golden_test.go`、.NET `GoldenVectorsTest.cs` 各存一份） | 后续 4 种语言每家都要复制粘贴一遍向量。这是多语言工作量里最容易消除的重复项，抽成文件后新语言直接读盘校验 |
| F2 | **LICENSE** | 缺失 | 开源发布的法律前提 |
| F3 | **CI（`.github/workflows`）** | 缺失 | 三语言测试无看门狗；后续 7 种语言没有 CI 几乎不可能保持契约一致 |
| F4 | **Rust / Go 的 README 与 API 文档** | 仅 .NET 有（`src/dotnet/README.md`） | README 矩阵里"待 API 稳定后补充"，API 已稳定 |
| F5 | **包元数据**（`Cargo.toml` license/repo、`go.mod` 模块路径、NuGet `PackageId`） | `go.mod` 仍是 `module ckv`，不是可发布 import path | 发布硬门槛 |

### 2.2 P1 —— API 真实缺口（都应在冻结契约前决定，否则新语言各写各的）

| # | 缺口 | 说明 |
| :-- | :--- | :--- |
| F6 | **`Namespaces()` 枚举命名空间** | 有一等公民的 namespace 概念，却无法列出当前有哪些 ns。管理界面/诊断必备 |
| F7 | **`Unwatch` / 注销监听** | 当前 `watch()` 注册后无法移除。长生命周期 DB + 短生命周期订阅者（页面/组件）必然泄漏，.NET 上更会回调到已释放对象 |
| F8 | **显式 `Flush()`** | 目前只有"每次写立即全量落盘"一种模式，没有任何手动/批量落盘出口 |
| F9 | **真·懒迭代器** | `scanRange` / `keys` 三语言都先把结果全量 materialize（Rust 的 `iter_prefix` 也是先收集完再迭代）。大 ns 下直接爆内存 |
| F10 | **`Stats()` / `GetFileSize()`** | 阶段二设计文档 §4.2 已经引用了 `GetFileSize()`，但三语言都没实现 |
| F11 | **`Backup(path)` / 导出导入** | 现在只能靠 `.bak` 隐式轮转，用户无法主动做一份可带走的备份 |

### 2.3 明确不需要（建议写进"非目标"）

- TTL / 键过期：配置与本地状态场景基本不需要，且会污染磁盘格式与快照语义。
- 压缩 / 加密：与"零依赖、纳秒级读"定位冲突。
- mmap：规范 §1.1 已明确排除。

---

## 三、现存问题（含本次实测确认项）

### 3.1 Bug 类 —— 三语言同病，同一根因

**根因：三语言的写路径都是「先改内存 → 再 flush → flush 内部才做只读/I/O 校验」，且 flush 失败不回滚。**

#### B1（实测·Go）：只读实例执行写操作会污染内存

```
PROBE-A put on readonly err = LockedError: Database is opened in read-only mode
PROBE-A after failed write, get = HACKED     ← 写"失败"了，但内存已被改
PROBE-A count = 1
```

- Rust：`src/rust/src/db.rs:229-232`（`ns_map.insert` 先执行，`inner.flush_snapshot()?` 才判 `read_only`）
- Go：`src/go/db.go:201-202`（`db.putInternal` 先执行，`db.flushSnapshot()` 才判 `ReadOnly`）
- .NET：`src/dotnet/src/Ckv/Db.cs:980-982`（`store.Put` 先执行，`FlushSnapshot()` 才抛 `InvalidOperationException`）

后果：调用方拿到错误以为写入失败，但后续读（甚至同一个实例的其他读线程）拿到的是新值；进程退出时不落盘，于是"内存里是新值、磁盘上是旧值、调用方以为没写成"。

#### B2（同源）：落盘失败不回滚内存

`ConcurrentModificationError`（外部进程改过磁盘）、磁盘满、`rename` 重试耗尽 —— 任何 flush 失败路径都会留下"内存已改、磁盘未改"的状态，且没有任何告警/回滚。这是 `REVIEW-20261015-1802.md` 的 P2-9 的实质化，比表格里记的更严重。

#### B3（实测·Go）：batch 派发"幻影事件"

```
PROBE-E batch delete nonexistent => events=1 [{2  nonexistent 0}]   ← 删了个不存在的键，仍发 Delete 事件
PROBE-F delete nonexistent       => deleted=false events=0           ← 单条 delete 就不发
```

同一语义两条路径行为相反。`write_batch`（Rust `db.rs:619-630`）与 .NET `Batch`（`Db.cs:1375-1388`）同样是**无条件 push 事件**，三语言一致地不一致。

### 3.2 可靠性 / 一致性类

| # | 问题 | 涉及 | 说明 |
| :-- | :--- | :--- | :--- |
| B4 | **.NET 的 `.bak` 轮转非原子** | .NET | `Db.cs:706-707`：`File.Delete(bakPath)` **之后**才 `File.Move(_path, bakPath)`，中间存在"两代备份都不存在"的窗口；失败退化为 `File.Copy`。Rust/Go 用的是覆盖式 `rename`（原子替换，无窗口）。违反规范 §4.2 |
| B5 | **.NET 替换主库用 `Delete + Move`** | .NET | `Db.cs:717-718` 同样非原子，失败退化为 `File.Copy(tmpPath, _path, true)` 全量拷贝（非原子写） |
| B6 | **.NET 在主库存在时不清理残留 `.db.tmp`** | .NET | `InitAndRecover` 只在 `!dbExists` 分支检查 tmp（Db.cs:192-212）。Rust `db.rs:1093-1100` / Go `db.go:868-874` 都会清理。.NET 会永久遗留垃圾 tmp |
| B7 | **写路径全程持写锁做序列化 + fsync** | 三语言 | benchmark 实测：98MB 库单键写 367ms，期间所有读阻塞；混沌测试读 P99 = 41ms、Max = 165ms |
| B8 | 快照计数释放语义分叉 | 三语言 | Rust 用 `Arc` 引用计数（最优）；Go 有 `SetFinalizer` 兜底；.NET 忘记 `Dispose` 则 `_snapshotCount` 永久泄漏 → 写路径永久退化为克隆（性能降级，非错误） |
| B9 | `Snapshot()` 取锁级别不一致 | Go | Go `db.go:1248` 取**写锁**，Rust/.NET 取读锁 |
| B10 | 同进程双开行为分叉 | Go | Go 无实例注册表，可同进程开两个实例（实测 `PROBE-B` 复现）；Rust/.NET 返回单例 |
| B11 | Watcher 回调异常处理分叉 | .NET | .NET `Db.cs:130` `try{}catch{}` 静默吞掉；Rust/Go panic 向上传播 |

### 3.3 已确认没问题的部分（不要动）

磁盘帧格式、贪心装箱、TxID 单调、`NsLen=0` 默认命名空间与 `"default"` 保留别名、内联 CRC32（向量 `0xCBF43926` / 空库 `0x0C44C937`）、CoW 快照读无锁、Watcher 锁外派发铁律、两代原子轮转与 `.bak` 自愈链（实测 `PROBE-H`：连续写 3 次后 `.bak` 正确保留上一代 `"2"`）、B1 清空后再写（实测 `PROBE-G` 已修好）。

---

## 四、阶段二（Append-Only WAL + Compaction）必要性评估

### 4.1 结论

**不建议现在做，也不建议在后续 4 种语言落地之前做。**

理由分三方面：

### 4.2 可靠性：会实质下降

1. **把"极简可证"的恢复模型换成"带副作用的修复"模型。**
   阶段一的核心价值不只是格式，而是**加载器只有两种结果（成功 / 回退 `.bak`），且从不在启动时修改正式文件**。规范 §3.4 专门写了"一刀切原则"来守住这点。阶段二引入**尾部截断**（启动时 `SetLength` 改正式文件）与**向后 `FrameMagic` 重同步**，等于让引擎在加载路径上带写副作用 —— 误判的代价是静默丢数据，而不是拒绝打开。

2. **放弃"平时不持有 `.db` 句柄"这一工程优势。**
   规范 §2 的对照表明确把"平时不持有句柄，天然免疫 Windows 句柄占用"列为阶段一优势。阶段二常驻追加写句柄后，杀软扫描、OneDrive/Dropbox 同步、备份工具都会撞文件锁 —— 而项目的主要目标平台正是 Windows 桌面端。

3. **Compaction 崩溃矩阵未定义完整。**
   设计文档 §3.3-C 只列了 2 条断电分支，没定义**"轮转 `.bak` 之后、原子替换之前断电"**：此时 `.db` 是旧内容、`.bak` 是新的、`.compact.tmp` 是新的、且 TxID 可能倒退。该分支必须补齐，否则是未定义行为。

4. **断电残尾从"不可能"变成"常态"。**
   每次启动都可能走截断路径，而这条路径无法靠黄金向量覆盖，只能靠断电 fault injection。三语言 × 多种断电时机 × 两种引擎模式，测试成本陡增。

### 4.3 工作量：是最大的放大器

- 新增的**不是格式**（格式已统一），而是**容错分支**：尾部截断、向后重同步、compaction 四步切换窗口、`EngineMode` 双模式共存、`.compact.tmp` 晋升。
- 这些恰恰是最难保证跨语言一致的部分 —— 项目已经在同类型的地方漂移过两次：两轮 review 记录的 P0-5（KeyChangeOp 三语言三种形态）、P0-6（delete 返回值）、P0-7（batch.clear_namespace 缺失）**全是事件语义 / 返回值 / 枚举数值**这类"小分叉"。
- 每加一种语言，这些分支要重新实现 + 重新对齐一遍。7 种语言 × 6+ 种断电时机 × 2 种模式，测试矩阵翻倍再翻倍。
- 现有 3 种语言还要同步改造，等于把刚刚冻结的 API 又打开一次。

### 4.4 收益：与定位重叠度低

规范 §2 自己写死了阶段一适用 **"< 1MB–2MB、配置、账号凭证、轻量本地状态"**。
benchmark 里"改 8 字节要擦写 79.2MB（写放大 1000 万倍）"这个最刺眼的数字，来自 79MB / 98MB 库的压力测试 —— **那不是目标场景**。桌面与移动 App 的配置 / 状态存储，绝大多数是"启动读一次、偶尔写几次"。

### 4.5 推荐替代：`SyncMode` 写合并 / 延迟落盘

在**完全不动阶段一架构**的前提下解决 80% 的高频写问题：

```text
DbOptions.SyncMode = Immediate | Deferred | Manual
  Immediate : 现状（每次写立即全量落盘 + fsync）
  Deferred  : 写只改内存 + 标记 dirty，立即返回；
              由后台定时器（100~500ms 窗口）或显式 Flush() 合并落盘
  Manual    : 完全由调用方决定何时 Flush()
```

- **不动**磁盘格式、**不动**恢复算法、**不引入**截断/重同步、**不常驻**句柄、**不加**新 OpCode。
- 每语言改动量约十几行 + 一个后台线程/定时器。
- 高频写延迟降到接近阶段二；写放大从"每条 N 次全库"降到"每批 1 次全库"。
- 可靠性损失是**显式且可选**的（用户自己选 durability 级别），不是隐式的架构风险。

### 4.6 若将来真要做阶段二，建议的裁剪版

等 6~7 种语言全部按冻结契约落地、CI 跑通、且有真实用户提出"> 10MB 高频写"需求时再启动，并且：

- ✅ 只做 **append-only 追加 + 显式 `Compact()`**
- ❌ 不做自动 compaction（把触发时机交给调用方，去掉一整类后台竞态）
- ❌ 不做向后魔数重同步（坏库直接回退 `.bak` 即可，保持加载路径"纯校验"）
- ✅ 保留尾部截断，但**只在显式开启 AppendOnlyLog 模式时启用**，默认路径零改动

---

## 五、建议的执行顺序

| 顺序 | 事项 | 量级 |
| :-- | :--- | :--- |
| 1 | 修 B1/B2/B3（同根因：把只读校验与长度/类型校验提到改内存之前；flush 失败回滚内存快照） | 小，三语言各一处 |
| 2 | .NET 两代轮转改原子（覆盖式 rename 替代 Delete+Move）+ 补残留 `.db.tmp` 清理（B4/B5/B6） | 小 |
| 3 | 发布工程：LICENSE、CI、go.mod 路径、Cargo/NuGet 元数据、**黄金向量抽成 `testdata/*.db`**（F1-F5） | 中 |
| 4 | Rust / Go README + API 文档；补齐 B8-B11 的语义说明 | 中 |
| 5 | 补 `Namespaces()` / `Unwatch` / `Flush()` 并**一起冻结进契约**（F6-F8） | 小 |
| 6 | `SyncMode` 写合并（替代阶段二） | 中 |
| 7 | 启动 Python / Node / Java 移植，以冻结契约为验收标准 | — |
| — | **阶段二 Append-Only WAL** | **暂缓，见 §4.6** |

---

## 附：本次实测探针输出（Go，探针文件已删除）

```
PROBE-A put on readonly err = LockedError: Database is opened in read-only mode
PROBE-A after failed write, get = HACKED                                  ← B1
PROBE-B db1 write after db2 write err = ConcurrentModificationError        ← B10 / 预检正常
PROBE-C snapshot="v1" current="v2"                                         ← CoW 隔离正常
PROBE-D final = "v99"                                                      ← 快照未 Close 下写正常
PROBE-E batch delete nonexistent => events=1 [{2  nonexistent 0}]          ← B3
PROBE-F delete nonexistent       => deleted=false events=0                 ← B3 对照
PROBE-G clear-then-write err = <nil>                                       ← 已修复
PROBE-H bak value = "2" (expect "2")                                       ← 两代轮转正常
PROBE-I ns="b"/"a"/"😀"/"é"/"中" count=1                                   ← 非 ASCII ns 正常
PROBE-J key 70000 bytes err = KeyTooLargeError                             ← 长度校验正常
```
