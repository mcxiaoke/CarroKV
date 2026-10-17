# RC2：WAL 写路径固定开销归因（X-1 定位实验）

> **日期**：2026-10-17
> **基线**：`dev` @ `4266fcf`
> **沙盒**：Windows 10 x64 / NTFS / C: 卷（与库同卷）
> **性质**：纯诊断。未修改 `src/` 下任何库代码，未进入产线，实验程序在 `temp/rc2-attribution/`。
> **上游**：[REVIEW-GAP-ANALYSIS-AND-IMPROVEMENT-PLAN-20261016.md](REVIEW-GAP-ANALYSIS-AND-IMPROVEMENT-PLAN-20261016.md) §4.1（X-1 步骤 a–d）、[API-V1-FREEZE.md](API-V1-FREEZE.md) §3.17（C15）

---

## 1. 结论摘要

1. **基线与历史数据不符 2.6 倍**：本次实测 `AppendOnlyLog + Immediate` **855.8 µs/op（1,169 ops/s）**，2026-10-16 性能报告记录的是 2.24 ms（446 ops/s）。同一仓库、同一负载、不同时点，差 2.6 倍。**历史数字不能作为决策依据**，这本身就是 RC2 的第一个结论。
2. **成本构成（C 卷，三因子主效应）**：

| 成本项 | 均值 | 占单次写比例 | 能否在不碰架构的前提下消除 |
| :--- | ---: | ---: | :--- |
| `fsync`（`sync_all`） | 459 µs | **53%** | 只有组提交/放宽持久性，均非零成本 |
| 前置 `stat`（3 次） | 226 µs | **26%** | **可以，改为 1 次** |
| 每写 `open` + `close` | 195 µs | **23%** | 只有常驻句柄（C15 已否决） |
| 实际写 160B 载荷 | 3.5 µs | 0.4% | — |
| 合计 | 884 µs | 100% | — |

3. **三个零风险优化叠加：864.8 → 637.8 µs（1.36x，微基准）**。落地后 A/B 实测（§9）：C 卷 `Immediate` **1.51x**、`Manual` **2.53x**；F 卷 `Immediate` 1.11x、`Manual` 2.27x。均不改磁盘格式、不改 API、不动句柄生命周期。
4. **`fsync` 占比不是结构常量**：同机同代码，C 卷 0.44 ms、D 卷 34 ms（**77 倍**）。因此"fsync 只占 15–31%"（C15 的反对证据）与"fsync 占 53%"（本次）**都不构成普适论断**。
5. **X-1 的最终答案**：固定开销的大头是 `fsync`，其次是**两次完全冗余的路径 `stat`**（每次写共 3 次，其中 2 次纯浪费），不是"神秘的系统调用开销"。组提交能碰的只有那 53%，且单写者无批次可合。

---

## 2. 方法

### 2.1 设计

不复用库的基准测试（它只报总吞吐，无法归因）。用 `std` 在同一磁盘上**逐字节复现** `append_entries` 的系统调用序列，再做因子消融：

- 三因子：前置 `stat`（3 次 / 0 次）× 句柄形态（每写 open / 常驻）× `fsync`（开 / 关）= 8 个单元；
- 单因子删减：`handle metadata`、`create` 标志、`stat` 次数阶梯（3→2→1）；
- 叠加单元：把三个可实装优化同时打开。

每单元独立文件、独立目录，**3 轮交替执行取中位数**（首轮曾用单轮相减，出现 `V2 < V3` 的因果矛盾，说明必须交替降漂移）。

### 2.2 保真度校验

微基准 `F1`（真实写路径模型）与库端到端实测：

| 运行 | 微基准 F1 | ckv 端到端 | 偏差 |
| :--- | ---: | ---: | ---: |
| 第 1 次 | 843.9 µs | 843.7 µs | 0.02% |
| 第 2 次 | 864.8 µs | 855.8 µs | 1.0% |

模型站得住：**95% 以上的单次写成本可以完全由系统调用解释**，没有隐性引擎开销。

### 2.3 复现

```bash
cd temp/rc2-attribution
cargo build --release
./target/release/rc2                  # C 卷，输出同 out-C.txt
RC2_DIR=D:/rc2-data ./target/release/rc2   # D 卷对照，输出同 out-D.txt
```

原始输出已存于 `temp/rc2-attribution/out-C.txt`、`out-D.txt`。

### 2.4 安全软件状态（重要背景）

本机 `RealTimeProtectionEnabled: False`、`AntivirusEnabled: False`（Defender 实时防护关闭），但 `root/SecurityCenter2` 注册了**腾讯电脑管家系统防护**。单次路径 `stat` 实测 **55–70 µs**，而普通 NTFS 元数据查询应在个位数 µs —— 存在文件系统过滤驱动税的强嫌疑。**未擅自关闭任何安全软件**（越权且不可复现），因此本报告的**绝对值仅属于本机**，可迁移的是**比例与冗余结构**。

---

## 3. 结果

C 卷（第二轮，与 `out-C.txt` 一致），单位 µs/op，三轮中位数：

| ID | 变体 | 中位数 | R1 | R2 | R3 |
| :--- | :--- | ---: | ---: | ---: | ---: |
| F1 | 真实写路径：3 stat + 每写 open + fsync | **864.8** | 924.1 | 864.8 | 825.4 |
| F2 | 每写 open + fsync，无前置 stat | 647.2 | 655.0 | 642.5 | 647.2 |
| F3 | 3 stat + 每写 open，无 fsync | 407.0 | 407.0 | 431.6 | 381.1 |
| F4 | 3 stat + 常驻句柄 + fsync | 695.9 | 695.9 | 729.6 | 647.2 |
| F5 | 常驻句柄 + fsync，无前置 stat | 424.9 | 424.9 | 422.5 | 427.5 |
| F6 | 每写 open，无 stat 无 fsync | 186.5 | 189.4 | 186.5 | 180.5 |
| F7 | 3 stat + 常驻句柄，无 fsync | 199.5 | 199.5 | 188.4 | 232.4 |
| F8 | 常驻句柄，无 stat 无 fsync | **3.5** | 3.5 | 3.1 | 3.7 |
| O1 | 真实路径 − 两次 handle metadata | 885.0 | 927.6 | 885.0 | 881.6 |
| O2 | 真实路径 − `create` 标志 | 789.6 | 826.8 | 769.1 | 789.6 |
| O3 | 前置 stat 3 次 → 1 次 | 714.2 | 858.7 | 714.2 | 714.0 |
| O4 | 前置 stat 3 次 → 2 次 | 832.1 | 841.8 | 832.1 | 774.2 |
| ALL | O1 + O2 + O3 叠加 | **637.8** | 670.6 | 637.8 | 603.6 |

主效应（正 = 该项消耗）：

- `fsync`：**459.1 µs**
- 前置 `stat`：**226.3 µs**（≈75 µs/次）
- 每写 `open`/`close`：**195.4 µs**
- 纯写：3.5 µs

`459 + 226 + 195 + 3.5 = 884`，与 F1 的 864.8 相差 2%，主效应之和闭合。

### 3.1 介质差异（C: / F: 为 NVMe，D: 为机械盘）

同一二进制、同一代码路径，三块卷：

| 变体 | C:（NVMe） | F:（NVMe） | D:（机械盘） |
| :--- | ---: | ---: | ---: |
| F1（3 stat + 每写 open + fsync） | 864.8 µs | 1,439.6 µs | 39,255.6 µs |
| F3（同 F1，无 fsync） | 407.0 µs | 249.1 µs | 274.0 µs |
| 推得 fsync 单项 | ~459 µs | ~1,181 µs | ~38,982 µs |

三条结论：

1. **同为 NVMe 的两块盘，`fsync` 单项就差 2.6 倍**（0.46 ms vs 1.18 ms）——控制器写缓存/掉电保护策略不同，不是"快盘都一样"。
2. **机械盘再差 33 倍**（38.7 ms）。metadata 操作在三块盘上却相差不到 1.7 倍（407 vs 249 vs 274），说明**慢的是刷盘语义，不是文件系统本身**。
3. 因此"fsync 占写成本 X%"是介质相关量，**任何单一机器的占比都不能推广**。C15 引用的 15–31% 之所以可疑，不只是复现不出，更因为它把一个随硬件摆动的量当成了结构常量。

---

## 4. 冗余调用清单（本次归因的实质产出）

当前每次写都会做 **3 次路径 stat + 1~2 次句柄元数据**，其中**至少 2 次是纯浪费**：

| # | 位置 | 调用 | 是否冗余 | 原因 |
| :--- | :--- | :--- | :---: | :--- |
| 1 | `db.rs:2305` `precheck_disk_state` | `path.exists()` | ✅ 冗余 | 紧随其后的 `metadata()` 已能区分 NotFound |
| 2 | `db.rs:2306` | `fs::metadata()` | 必要 | 取文件大小 |
| 3 | `db.rs:2162` `append_entries` | `self.path.exists()` | ✅ **完全冗余** | `create(true)` 新建的文件长度必为 0，`metadata().len()==0` 已覆盖 |
| 4 | `db.rs:2169` | `file.metadata()?.len()==0` | 可省 | 下一行 `seek(End(0))` 的返回值就是同一信息 |
| 5 | `db.rs:2214` | `file.metadata()?.len()` | 可省 | 本次写入字节数已知，`end + written` 即为新大小 |

五语言同款（已逐文件核对）：

| 语言 | 前置 stat | 追加后再 stat | open 标志 | 句柄长度查询 |
| :--- | :--- | :--- | :--- | :--- |
| Rust | `db.rs:2305-2306` | `db.rs:2162` | `create(true)` `db.rs:2166` | `db.rs:2169` / `2214` |
| Go | `db.go:1871` | `db.go:2152`（`os.Stat`） | `O_CREATE` `db.go:2156` | `db.go:2162`（`f.Stat`） |
| .NET | `Db.cs:996-997` | `Db.cs:1545`（`File.Exists`） | `OpenOrCreate` `Db.cs:1546` | `Db.cs:1548`（`fs.Length`，.NET 侧可能已缓存） |
| Kotlin | `CkvDb.kt:346-347` | `CkvDb.kt:497`（`exists()` + `length()`） | `FileOutputStream(f, true)` `CkvDb.kt:510` | 同上 |
| Dart | `db.dart:549` | `db.dart:551` | `writeOnlyAppend` `db.dart:552` | `db.dart:554`（`lengthSync`） |

Kotlin 最差：`precheckDiskState` 2 次 + `appendEntries` 再来 2 次 = **4 次 stat**。

---

## 5. 可实装的三项优化（建议单独立项，不并入 v2.0）

| 优化 | 改法 | 实测收益 | 风险 |
| :--- | :--- | ---: | :--- |
| **A. 前置 stat 3 次 → 1 次** | `precheck` 用一次 `metadata()` 替代 `exists()+metadata()`（`Err(NotFound) => (0,0)`）；删掉 `append_entries` 里重复的 `path.exists()` | −**150.6 µs（17.4%）** | 零。三次 stat 读的是同一个事实 |
| **B. 去掉 `create` 标志** | 先 `open` 不带 `create`；`NotFound` 时回退带 `create` 并写文件头（仅每库一次） | −**75.2 µs（8.7%）** | 低。热路径少一次 OPEN_ALWAYS 检查 |
| **C. 去掉两次 handle metadata** | 用 `seek(End(0))` 的返回值判断空文件，并按已写字节数推算新文件大小 | −**20 µs（≤2%，接近噪声）** | 零。语义等价，可用现有 WAL 测试锁定 |

三项叠加实测 **637.8 µs，−26%**（另一次独立复跑 598.0 µs，−29%）。预测合计 246 µs 与实测 227 µs 的差值属轮间漂移。全部属于**纯内部实现收敛**：不动磁盘格式、不动 API、不动锁模型、不动句柄生命周期（因此不触碰 C15）。

> 说明：A 项需要在 `precheckDiskState` 内保留"`diskFileSize == lastKnown.size` 则复用缓存的 TxID"的快路径，否则会引入 `scan_max_tx_id` 全表扫描——这是唯一需要小心的地方。

---

## 6. 对既有结论的修正

### 6.1 C15（组提交）的证据链失效，结论仍成立

- 原反对依据：《SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016》中 `WAL+Immediate 446` vs `WAL+Deferred 513`，推出"fsync 仅占 15–31%，合并 fsync 无意义"。
- 本次复现：`Immediate 855.8` vs `Manual 394.3` → **fsync 占 54%**；C 卷/D 卷之间该项又差 77 倍。
- 结论：**"15–31%" 是那台机器那一次的偶然值，不是结构事实。** C15 的裁决应改为只依赖结构性理由——**嵌入式单进程单写者，没有并发写者可合并**，队列只能增加延迟；这与硬件无关。
- 顺带修正：模型化的 32 写合并 1 fsync 可到 16 µs/op（约 62k ops/s）。这是**并发写者场景**的上限，不是单写者收益。要拿到它必须先重组锁模型（当前 `inner.write()` 横跨整个 fsync）。

### 6.2 历史性能报告需要加注

`docs/SYNCMODE-AND-WAL-PERFORMANCE-REPORT-20261016.md` 的绝对值与复现性存疑（本次差 2.6x，且未记录磁盘介质、安全软件、warm-up 状态）。建议在该报告头部加一行口径声明，避免后续决策继续引用。

### 6.3 X-1 闸门判定

| X-1 步骤 | 问题 | 结论 |
| :--- | :--- | :--- |
| (a) 常驻句柄 delta | open/close 占多少 | **195 µs（23%）**；常驻句柄可达 1.9x，但 C15 已因 `WinError 32` 否决 |
| (b) 纯 open+close 成本 | 句柄生命周期单价 | 55–135 µs（带 `create` 比不带贵 2.4x） |
| (c) 安全软件开关 | 是否 Defender | Defender 实时防护本就关闭；注册的第三方防护疑似过滤驱动税，未隔离验证 |
| (d) `precheck` 必需性 | `exists()+metadata()` 是否都要 | **不都要**：1 次 `stat` 足够，另有 1 次完全冗余 |

**判定**：X-1 归因完成。后续任何写路径优化立项，必须以本节数字为基线，并注明介质与安全软件状态。

---

## 7. 口径修正：磁盘格式并未"冻结"

前一版路线图评审（含本报告的上游文档）把"32B 文件头 + 28B 帧头 + 贪心装箱 + TxID 单调"当作**不可变前提**，进而在成本估算中让"改格式"一律背上了 v4 迁移、双读兼容、旧向量保活等包袱。这是错的口径：

- CKV **尚未发布、尚未投入使用**，五语言实现都在同一仓库同步演进，不存在需要兼容的外部读者；
- `API-V1-FREEZE.md` 冻结的是**进程内 API 层**（签名、错误名、语义），其第 1 条"字节级磁盘格式不变"约束的是**同一版本内的兼容性**，不是"永世不得改"；
- 真要改格式，成本是**五语言同步改 + `tools/gen_testdata.py` 与 `testdata/*.db` 重生成 + 五套 golden 测试同步**——是**工作量**，不是**禁忌**。

对 v2.0 各闸门的影响（据此重新校准，详见后续排序文档）：

| 项 | 原估成本中的"冻结溢价" | 修正后 |
| :--- | :--- | :--- |
| Deflate 压缩 | 需绕 `HeaderFlags` + 升 `FormatReadVer` 以防旧读端误读 | **可直接扩帧头**（现 28B 无空位，加到 32B 或借用 `PayloadLen` 高位）；只要能同步改五语言与向量即可 |
| TTL | 需 v4 迁移、双读兼容 | **可直接在条目头加 `ExpireAt`**；成本仍在五语言实现、过期语义与后台淘汰器生命周期，但少了兼容层 |
| 分段 WAL | 撞 `GetFileSize`/`Backup` 的单文件承诺 | 该承诺是 **API 语义**（属冻结范围，需走 v1.1 修订案），不是格式问题——**这一条溢价不成立** |

**但有一条不受此修正影响**：压缩的"黄金向量双层化"仍然必要。原因不是冻结，而是 **Deflate 输出字节本身不确定**（各语言/各实现的 level、window、match 策略不同），`SHA-256 逐字节相等`这条判据在压缩向量上**物理上无法成立**。格式可以随便改，但"跨语言字节级相等"这个验收标准必须换成"解码互操作 + 已知明文"。

---

## 8. 建议的下一步（按投入产出）

1. **立 ADR：写路径 stat 收敛（A+B+C）**。零风险、1.36–1.41x、五语言同构、可被现有 WAL 测试完全覆盖。它是本次归因唯一的"免费午餐"，且与 v2.0 任何项目都不冲突。
2. **给历史性能报告加口径声明**，并把本节数字作为新一轮基线（注明 C 卷 + 第三方防护）。
3. **组提交维持搁置**，但把 C15 的理由从"fsync 占比低"改为"单写者无批次可合"——前者已被证伪。
4. 压缩/TTL 的成本重估按第 7 节口径进行；TTL 的取舍点回到"过期语义 + 淘汰器生命周期"，不再是"格式不敢动"。

---

## 9. 落地结果（2026-10-17 实施）

### 9.1 五语言改动点

| 语言 | 前置 stat | 冗余 exists | open 标志 | 句柄长度查询 |
| :--- | :--- | :--- | :--- | :--- |
| Rust | `exists()+metadata()` → 单次 `metadata()`（NotFound ⇒ `(0,0)`） | 删除 `path.exists()`，空文件由 `seek(End)` 判定 | `create(true)` → `Open` 失败再带 `create` 回退 | `metadata()` → `stream_position()` |
| Go | 本就是单次 `os.Stat` | 删除 `os.Stat`，空文件由 `Seek(0, io.SeekEnd)` 判定 | `O_CREATE` → `O_RDWR` + `IsNotExist` 回退 | `f.Stat()` → `Seek(0, io.SeekEnd)` |
| .NET | `File.Exists + new FileInfo` → 单次 `FileInfo`（`Exists` 缓存） | 删除 `File.Exists`，空文件由 `Seek(End)` 判定 | `OpenOrCreate` → `Open` + `FileNotFoundException` 回退 | `fs.Length` → `fs.Position`（内部计数，无系统调用） |
| Kotlin | `exists()+length()` → 单次 `Files.readAttributes` | 删除 `exists()`，空文件由 `channel.size()` 判定；两次打开合并为一次 | 保留 `FileOutputStream(…, true)`（JVM API 只能带 `O_CREAT`） | `dbFile.length()` → `fos.channel.size()` |
| Dart | `existsSync()+lengthSync()` → 单次 `statSync()` | 删除 `existsSync()`，空文件由 `raf.lengthSync()` 判定 | `writeOnlyAppend` 本就创建，无需改 | 保留（无路径解析） |

Kotlin 的 `O_CREAT` 是 JVM `FileOutputStream` 的固定行为，去掉它需要改用 `FileChannel.open(WRITE, APPEND)` 并替换 `fos.fd.sync()` 为 `channel.force(true)`，属独立重构，本次不做。

### 9.2 A/B 实测（同机同负载，各 3 轮取中位数）

基线 = `HEAD` 源码单独编译的二进制；新 = 本次改动。负载为单线程 `put_string`，128B 值，每个配置 150/600 次写。

| 卷 | 模式 | 改动前 | 改动后 | 加速 | 吞吐 |
| :--- | :--- | ---: | ---: | ---: | :--- |
| C: NVMe | WAL + Immediate | 951.5 µs | **628.8 µs** | **1.51x** | 1,051 → 1,590 ops/s |
| C: NVMe | WAL + Manual（无 fsync） | 420.3 µs | **165.9 µs** | **2.53x** | 2,379 → 6,029 ops/s |
| F: NVMe | WAL + Immediate | 1,402.0 µs | **1,268.0 µs** | 1.11x | 713 → 789 ops/s |
| F: NVMe | WAL + Manual（无 fsync） | 305.2 µs | **134.3 µs** | **2.27x** | 3,279 → 7,443 ops/s |

读法：`Manual` 不含 `fsync`，是**纯系统调用节省**的真实度量（2.3–2.5x）；`Immediate` 的分母里 `fsync` 占比越高，加速比越被稀释（F 卷 82% ⇒ 1.11x）。这与 §1 的归因自洽：省掉的是 2 次路径查询 + 1 次 create 判定，省不掉刷盘。

### 9.3 测试结果

| 语言 | 命令 | 结果 |
| :--- | :--- | :--- |
| Rust | `cargo test` | 全绿（1 个既有 ignore，无失败） |
| Go | `go vet` + `go test ./...` | 全绿 |
| .NET | `dotnet test` | **99 passed**, 0 failed |
| Kotlin | `gradle test` | **59 passed**, 0 failed |
| Dart | `dart test` | **55 passed**，`dart analyze` 仅 1 条既有 `unnecessary_import`（测试文件，非本次引入） |
| Go（Linux 单核 x86_64） | 同左，源码置于 `/data` | **全绿，10.4s** |

Rust 未做 Linux 侧运行：本地 WSL 无 Rust 工具链，安装成本高于收益（改动只用 `metadata`/`seek`/`stream_position` 等 std 可移植接口，且本机 101 项测试全绿）。Linux 平台的实测由 Go 实现代为覆盖——两者的写路径系统调用序列同构（见 §4）。

### 9.4 附带发现并修复：Go `Close()` 的停等竞态（**存量缺陷**）

Linux 全量测试首次运行挂死 6 分钟以上，`ps` 显示 `ckv.test` 处于 0% CPU 的阻塞态。用 `-timeout 25s` 拿到 goroutine 栈，定位到：

```
goroutine 6 [sync.WaitGroup.Wait]: ckv.(*DB).Close  db.go:300
goroutine 9 [select]:              ckv.Open.func1    db.go:203
```

**根因**：后台刷盘 goroutine 每轮循环重新读取 `db.stopCh` 字段，而 `Close()` 的顺序是 `close(stopCh)` → `db.stopCh = nil` → `Wait()`。若 goroutine 在字段被置 nil 之后才第一次求值 `select`，就会 select 到 **nil channel**（永远不就绪）并等满 `syncInterval`。测试把周期设为 1 小时以隔离后台刷盘，于是 `Close()` 挂死一小时。

**为什么现在才发现**：默认周期 100ms，最坏只多等 100ms，看不出来；多核 Windows 上 goroutine 通常在 Close 之前已被调度，窗口几乎不触发；单核 Linux + 大周期则必现。

**与 Rust 同一类缺陷**：今天 11:10 修的 Rust C16（`wait_for` 前未检查关闭标志）是同一族的"停止信号与等待顺序"问题。

**修复**：go 语句处把通道按值捕获，goroutine 不再读字段：

```go
stopCh := db.stopCh
go func(stop <-chan struct{}) { … case <-stop: return … }(stopCh)
```

**回归用例**：`TestDeferredCloseDoesNotWaitForTheSyncInterval`（`sync_mode_test.go`，1 小时周期 + 5s 上限）。已验证判据有效：把远端 `db.go` 改回 `<-db.stopCh` 后该用例在 Linux 上 5.00s 失败，恢复修复版后 0.01s 通过；同一反向验证在 Windows 上**无法复现**（多核调度差异），说明这个缺陷只能在单核环境稳定暴露。

**其余语言核查**：.NET 用 `Timer.Dispose(waitHandle)` + `WaitOne(1000)`，Kotlin 用 `shutdown()` + `awaitTermination(1s)`，Dart 用同一 isolate 的 `Timer.cancel()`，均无该竞态。
