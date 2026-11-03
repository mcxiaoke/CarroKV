# .NET 与 Dart 实现深度审查报告

- 审查时间：2026-10-18 17:27:55 (UTC+8)
- 审查范围：`src/dotnet/src/Ckv/*`、`src/dart/lib/src/*`（不含测试与基准工程）
- 审查重点：IO / 数据读写正确性、边缘情况、性能、多线程与并发
- 审查方式：全量通读源码 + 针对性可复现探针（探针位于 `temp/review-probe/`，未改动任何库代码）
- 基线：`.NET` 现有测试 140/140 通过，`Dart` 现有测试 95/95 通过 —— **下列问题全部不在现有测试覆盖范围内**

---

## 0. 结论摘要

| 级别 | 数量 | 概要 |
| :--- | ---: | :--- |
| **P0** | 2 | Dart：`batch(delete(超长 key))` 可直接把数据库写成永久不可读；.NET/Dart：`clearNamespace` 与并发写存在"墓碑缺失"竞态，重开后键复活 |
| **P1** | 7 | 每次写对整个命名空间做 O(n) 克隆（AOL"与规模无关"的承诺不成立）；.NET 命名空间比较器在孤立代理项上自相矛盾导致数据串号；`open()` 路径校验缺失（10 秒空转 + 误导错误 + 幽灵库）；整库读入内存导致打开即 3 倍内存；`Keys(prefix)` 全表扫描；后台刷盘静默吞异常 |
| **P2** | 9 | 事件循环阻塞、Dart 跨 isolate 不可用、解码端跨语言分叉、读路径重复解码、`Stats()` 触发磁盘 IO、锁退避不符规范、比较器分配、终结器行为、只读路径的无谓 IO |

**总体评价**：崩溃安全性设计（两代原子轮转、`.bak` 语义、Torn-tail 分类、TxID 单调性、锁协议）相当扎实且两语言高度对齐；**主要短板在"内存数据结构的写放大"和"少数边缘路径的输入校验与竞态"**，而不是磁盘格式或恢复逻辑。

---

## 1. P0 —— 严重问题

### P0-1（Dart）`batch(writer.delete(超长 key))` 会把数据库写成永久不可读

**位置**：`src/dart/lib/src/storage.dart:410-416`（`BatchWriter.delete`）

```dart
BatchWriter delete(String? ns, String key) {
  final normNs = BinaryHelper.normalizeNamespace(ns);
  final keyBytes = Uint8List.fromList(utf8.encode(key));
  ops.add(BatchOp(Constants.opDelete, normNs, keyBytes, ...));   // ← 没有任何长度校验
  return this;
}
```

`BatchWriter` 的其余每个写入口（`putBytes/putString/putInt64/putFloat64/putBool`）都会调用 `validateLengths`，**唯独 `delete` 漏掉了**。而 .NET 的对等实现 `BatchWriter.Delete`（`Db.cs:3563-3572`）是校验的：

```csharp
Limits.ValidateKeyAndValueLengths(keyBytes, Array.Empty<byte>());
```

于是形成跨语言行为分叉，随后在 `AppendOnlyLog` 模式下炸掉：Dart 的 `RawOpEntry.encodeTo`（`format.dart:83-95`）用 `writeUint16LE(hdr, 4, key.length)` 写 `KeyLen`，对 >65535 的长度**静默截断**，条目实际字节数与声明长度不一致 → 载荷 CRC 能对上（CRC 算的就是实际写出去的字节），但条目解码时 `pIdx != pEnd` → `CorruptedError: Malformed entry payload`。

**实测（Dart，`temp/review-probe/dart-probe`）**：

```
=== [4] Dart batch(delete(hugeKey)) in AppendOnlyLog ===
  batch(delete(hugeKey)): returned normally          <-- 没有抛任何错
  in-memory get(ok) = value
  reopen: FAILED -> CorruptedError: Malformed entry payload at offset 77
  ==> the database file is now permanently unreadable
  control (SnapshotAtomic) batch(delete(hugeKey)): returned normally
  control: putString(hugeKey) correctly threw KeyTooLargeError
```

危害等级定为 P0 的理由：

1. **静默**：调用返回成功，没有任何异常或告警。
2. **不可自愈**：AOL 模式从不产生 `.bak`，`autoRecoverFromBackup` 也无从谈起。
3. **全量丢失**：一个数据库里全部数据一次性变砖。
4. **跨语言不一致**：同一段调用代码在 Rust/Go/.NET 上抛 `KeyTooLargeError`，在 Dart 上毁库。

**触发条件宽泛**：key 只是"UTF-8 字节长度 > 65535"（约 6.5 万个字符），不需要任何特殊构造。

**修复方向**：把 `BatchWriter.delete` 与其它 put 方法对齐，改为
`validateLengths(keyBytes, Uint8List(0));`。
另外建议在 `RawOpEntry.encodeTo` / `.NET` 的 `AppendEntries` 里对 `key.length > 65535` 与 `ns.length > 255` 做一次**兜底断言**，把"上游漏校验"从"损坏数据库"降级为"抛异常"（纵深防御）。

---

### P0-2（.NET / Dart）`clearNamespace` 与并发写之间的墓碑缺失竞态 → 键"复活"

**位置（.NET）**：`Db.cs:3030-3065`（`ClearNamespaceInternal`）与 `Db.cs:3166-3205`（`BatchInternal` 的 clear 展开）

```csharp
_rwLock.EnterReadLock();
try {
    ...
    foreach (var item in store.Items)                       // ← ① 在读锁内采集“当前有哪些键”
        entries.Add(new WalOpEntry(OpDelete, ..., item.Key, ...));
}
finally { _rwLock.ExitReadLock(); }                        // ← ② 释放读锁

return ExecuteMutation(entries, new[]{normNs},
    _ => { _namespaces[normNs] = new NamespaceStore(); ... },// ← ③ 在写锁内清空整块内存
    ...);
```

①与③之间存在一个**没有任何同步的窗口**。落在这个窗口里的并发 `put`：

* 内存侧：③ 把整个命名空间换成一个空 store —— 该键被删除；
* 日志侧：`entries` 是在①采集的，里面**没有**这个键的 `OpDelete`。

`ExecuteMutation` 的 AOL 分支（`Db.cs:2283-2298`）照单全收地追加 `entries` 后结束 —— **内存与日志就此分叉**。同一进程内后续读会返回"不存在"，重开数据库时重放日志却把它带了回来。

Dart 的 `batch()` 里是同一段逻辑（`db.dart:1737-1763`），只是 Dart 单 isolate、无法在函数中间插入执行，所以窗口为零 —— 但这也说明：**Dart 的安全是"恰好没有并发"带来的，不是设计上保证的**，一旦将来引入 `Isolate` 或异步化写入，同样的 bug 立刻成立。

**实测（.NET，AOL 模式）**：

```
=== [8] AOL: ClearNamespace() / batch(clearNamespace) racing concurrent puts ===
  batch(clear) attempt 0: memory count=1, reopened count=2 -> 1 key(s) RESURRECTED after reopen
```

（第一版更朴素的探针在 40 次尝试内第 6 次即命中：`memory count=1, reopened count=2`。
`clearNamespace()` 非 batch 入口未在有限次尝试内命中，但代码路径完全同构。）

**修复方向**：把"采集墓碑集合"与"清空内存"放进**同一个写锁临界区**，即不要在 `ExecuteMutation` 之外预生成 `entries`，而是改成 `applyMem` 阶段在写锁内用当时的 `store.Items` 生成 WAL 条目（与 `IncrementInt64` 已有的 `entriesFactory(result)` 机制一致，`Db.cs:2286`）。Dart 侧建议把同样的"同临界区"结构固定下来，避免后续被无意改坏。

---

## 2. P1 —— 重要问题

### P1-1（.NET / Dart）每次写都整块克隆命名空间 → AOL 的"与规模无关"承诺不成立

**位置（.NET）**：`Db.cs:2162-2173`（Snapshot 分支）与 `Db.cs:2275-2279`（AOL 分支）：

```csharp
foreach (var ns in affectedNamespaces)
    rollbackStores[ns] = _namespaces.TryGetValue(ns, out var existing) ? existing.Clone() : null;
```

AOL 分支同样克隆。`NamespaceStore.Clone()`（`Db.cs:94-100`）= `Items.AddRange(Items)`，即**每次写都 O(命名空间条目数)**。

**位置（Dart）**：`db.dart:1110-1136`（`_getOrCreateStoreForWrite`）—— `clonedInTx != null` 时**无条件**克隆（三种写分支都可能回滚，源码注释自己也承认了这一点）。`NamespaceStore.clone()`（`storage.dart:45-47`）更贵：浅拷贝列表之外，构造函数还会再走一遍 `for` 累加 `liveBytes`。

这与 `Options.cs:30-46` / `options.dart:46-63` 中白纸黑字的承诺直接冲突：

> "AppendOnlyLog 稳态约 0.18 ms/写**且与规模无关**"

**实测 —— .NET，AOL + Deferred（去掉 fsync 干扰，单命名空间）**：

```
  pre=      0  p50=0.1842 ms  (1086 puts/s)
  pre=   5000  p50=0.1876 ms  (1066 puts/s)
  pre=  20000  p50=0.2101 ms  ( 952 puts/s)
  pre=  50000  p50=0.2222 ms  ( 900 puts/s)
  pre= 150000  p50=0.3730 ms  ( 536 puts/s)   <-- 吞吐腰斩
```

**实测 —— .NET，AOL + Immediate（每写分配量，100 次 put）**：

```
  pre=      0   6.5 KB/put
  pre=   5000   45.6 KB/put
  pre=  20000  162.8 KB/put
  pre=  50000  397.2 KB/put
  pre= 150000 1178.5 KB/put   <-- 每次写产生 ~1.2 MB 垃圾
```

**实测 —— Dart，AOL + Immediate / Deferred**：

```
  Immediate:  pre=0 1.010 ms → pre=150000 4.806 ms     (慢 4.8x)
  Deferred :  pre=0 378 puts/s → pre=150000 47 puts/s   (慢 8.0x)
```

Dart 比 .NET 更差，因为它的克隆多走一遍 `liveBytes` 累加，且 List<object> 的 GC 成本更高。

**结论**：在 5 万～15 万条目的单命名空间上，AOL 写吞吐随库规模线性退化，内存分配随规模线性增长；GC 压力会成为多线程场景下的主要抖动来源。这不是"慢一点"，而是把 `AppendOnlyLog` 的核心卖点抵消掉了。

**修复方向**：把"回滚点 = 整块克隆"改成 **undo-log**（按 key 记录旧值/旧存在性，失败时逐条反向回放），稳态写成本即可回到与规模无关。源码注释 `db.dart:1119-1124` 已经指出了这条路径，属于已知未做的重构。

---

### P1-2（.NET）`Utf8StringComparer` 的 `Compare` 与 `Equals` 自相矛盾 → 不同命名空间互相串号

**位置**：`src/dotnet/src/Ckv/BinaryHelper.cs:141-170`

```csharp
public int Compare(string? x, string? y) { ... return Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(...); }
public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
```

.NET 的 `Encoding.UTF8.GetBytes` 对**孤立代理项（lone surrogate）**默认做替换（U+FFFD）。于是两个不同的字符串 `"\uD800"` 与 `"\uD801"`：

* `Equals` → `false`（是两个不同的 string）；
* `Compare` → `0`（都编码成 `EF BF BD`）。

比较器的一致性被破坏。而 `_namespaces` 用的正是这个比较器（`Db.cs:162-163`），`SortedDictionary` 判定"同一个键"依据的是 `Compare` 而非 `Equals` —— **两个命名空间塌缩成一个**。

**实测**：

```
  UTF8.GetBytes(A) = EF-BF-BD
  UTF8.GetBytes(B) = EF-BF-BD
  put under A then B. namespaces() = [U+D800]
  getString(A,"k") = value-under-B      <-- 写进 A 的数据被 B 覆盖
  getString(B,"k") = value-under-B
  count(A)=1  count(B)=1
  after reopen: getString(A,"k") = value-under-B;  count(A)=1 count(B)=1
```

危害：写入 `A` 的值**被 `B` 静默覆盖且不可恢复**，重开后依然如此。输入来源并不罕见：`Encoding.Unicode.GetString(任意字节)`、`Substring` 从代理对中间切开、JSON 的 `"\uD800"` 转义等。U+FFFD 与孤立代理项同样互相混淆。

Dart 不受影响（Dart 字符串字面量里的孤立代理项在解析期已被归一为 U+FFFD），因此这是 .NET 独有缺陷。

**修复方向**（三选一，建议 1+3）：
1. 让 `Equals` 与 `Compare` 用同一套字节比较：`Equals(x,y) => Compare(x,y) == 0;`（保证比较器自洽）；
2. 更严格：`NormalizeNamespace` 与所有 key 入口校验字符串是否为合法 UTF-16（`char.IsSurrogate` 配对检查），非法直接抛 `InvalidNamespaceError` / `KeyTooLargeError`；
3. 无论如何，禁止把孤立代理项静默写成 U+FFFD —— 至少要能在 `put` 时就拒绝。

---

### P1-3（.NET / Dart）`open()` 的错误归因：目录缺失 → 空转 `lockTimeoutMs` 后报"被占用"

**位置**：`src/dotnet/src/Ckv/LockFile.cs:59-83`；`src/dart/lib/src/lock.dart:40-63`

两边的取锁循环把**所有**打开失败都当成"别人正持有这把锁"，一路重试到超时：

* .NET：`catch (IOException) when (...)` / `catch (UnauthorizedAccessException) when (...)` → 10 ms 固定间隔重试 → 超时抛 `LockedError("... locked: timed out ...")"`
* Dart：`catch (_) { }` → `sleep(50ms)` → 超时抛 `LockedError("... another connection is using this database")`

但 `DirectoryNotFoundException` / `PathAccessException` 也继承自这些类型。**实测**：

```
=== .NET: parent directory missing ===
  after 1018 ms -> LockedError: Database '...\no\such\dir\x.db' locked: timed out waiting for
                  the file lock '...\no\such\dir\x.db.lock' after 1000ms
=== Dart: parent directory missing ===
  missing parent dir after 1062 ms -> LockedError: Failed to acquire the file lock on
  "...\no/such/dir/x.db.lock" within 1000ms: another connection is using this database
```

一个路径拼写错误 = 默认配置下 **10 秒无响应 + 一条完全指错方向的错误信息**，排查成本极高。

**修复方向**：把 `DirectoryNotFoundException` / Dart 的 `PathException(reason == "directory does not exist")` / `PathAccessException` 从重试集合里剔除，直接抛 `IoError`，消息里带上原始异常。

---

### P1-4（.NET / Dart）`open()` 不校验路径类型：目录路径会"打开"出一个幽灵数据库

**实测（.NET）**：

```
=== [10b] Open() on a path that is a directory ===
  opened (unexpected)
```

**实测（Dart）**：

```
  path is a DIRECTORY: open() returned an empty database (count=0)
    first write threw IoError: Immediate flush failed: PathAccessException:
      Cannot rename file to '...\adir', path = '...\adir.stage' (OS Error: 拒绝访问。errno = 5)
```

原因：`File.Exists(dir)` / `File(dbPath).existsSync()` 对目录返回 false → `InitAndRecover` 认为"全新空库" → 构造成功。真正的失败推迟到第一次写，而且错误信息是 rename 的 errno。同时 `replaceFileAtomicallyWithRetry` 的 `.stage` 兜底路径会在磁盘上**留下垃圾文件**。

**修复方向**：`Open`/`CkvDb.open` 入口对路径做一次 `File.Exists && isDirectory` 判定，直接抛 `IoError("path is a directory")`。

---

### P1-5（.NET / Dart）打开时整库读入内存 + 解析中间结构 → 打开内存约为文件的 3 倍

**位置**：`.NET Db.cs:904` `File.ReadAllBytes(dbPath)`；`Dart db.dart:509` `File(dbPath).readAsBytesSync()`

随后 `ParseDatabaseBytes` / `loadAllWithRecovery` 还会把**每一个条目**物化成 `LoadEntry` / `_LoadEntry`（含 `byte[] Key` + `byte[] Val` 各自独立分配），最后再组装成 `NamespaceStore`。

**实测（.NET，12.65 MB AOL 库 / 60000 条目）**：

```
  file=12.65 MB  managed-heap delta on Open=34.8 MB  open took 73 ms
=== Dart ===
  file=12.65 MB  open took 88 ms  entries=60000
```

即约 **2.8 倍文件大小**的托管堆增量，且解析期间瞬时更高（Dart 的 `parsed` 列表与最终 store 同时存在）。规格 §2 给 `AppendOnlyLog` 规划的适用区间是 **1 MB – 100 MB**；在 100 MB 上，光打开就要吃掉数百 MB 常驻内存，移动端（Flutter / MAUI）上会直接 OOM。

配套问题：残尾自愈时还会把整库再写一份 `<db>.corrupt.<ts>`（`Db.cs:914-919` / `db.dart:523-526`），等于再吃一份磁盘。

**修复方向**：改为流式解析（按帧 `Read` 到可复用的缓冲，逐帧直接 replay 进 store，不物化全量 `LoadEntry` 列表）。这是本轮审查里改动量最大、收益也最大的一项。

---

### P1-6（.NET / Dart）`Keys(ns, prefix)` / `IterPrefix(ns, prefix)` 是全命名空间 O(n) 扫描

**位置**：`.NET Db.cs:3073-3100`、`Db.cs:2489-2516`；`Dart db.dart:1399-1415`、`db.dart:1417-1440`

底层 `NamespaceStore.Items` 本来就是**有序**的，二分查找 `FindIndex` 也在同一个有序数组上工作，但前缀扫描完全没用上这个性质 —— 从头遍历整块数组逐个 `StartsWith`。`scanRange` 做对了（用 `FindIndex` 定起点 + 遇 `endKey` 提前 break），`keys` / `iterPrefix` 没做。

**实测（.NET，150000 条目，1 命中前缀）**：

```
  Keys(prefix) returned 1 row(s) in 2.6 ms (first call) / 1.4 ms (warm)
  GetString of the same key (binary search): 0.31 ms
```

对于"按用户 ID 前缀取 20 个键"这种最常见的调用形态，代价与前缀命中数**无关**，只与命名空间总量有关。

**修复方向**：`keys` / `iterPrefix` 用 `FindIndex(prefix)` 定位起点（配合 `prefix + 0xFF` 上界，或逐项比较公共前缀长度），命中数 k 时复杂度降到 O(log n + k)。`scanRange` 的写法可直接复用。

---

### P1-7（.NET / Dart）后台刷盘与定时刷盘**完全静默**地吞掉所有异常

**位置**：`.NET Db.cs:538-541`；`Dart db.dart:625-630`

```csharp
catch
{
    // Background flush retry on next interval
}
```

```dart
void _flushIfDirty() {
  if (_isDisposed || _options.readOnly || !_isDirty) return;
  try { flush(); } catch (_) {}          // ← 没有任何 onWarning
}
```

`Deferred` 模式下后台刷盘是**唯一的**自动落盘路径。磁盘满、权限变更、文件名被占用…… 用户拿不到任何信号，数据只在 `close()` 时才通过 `onFinalFlushFailed` 报一次 —— 而如果进程被强杀，这一次也不会发生。

这与本库其它地方"失败必须上报"的既定原则（`onFinalFlushFailed`、`onCompactionFailed`、`onTailTruncated`、`onDirSyncUnavailable`）不一致。

**修复方向**：把这两处改为 `QueueWarning("onBackgroundFlushFailed", ...)`，并做失败计数/退避，避免刷盘每 tick 失败一次就把告警刷屏。

---

## 3. P2 —— 建议改进

| # | 语言 | 问题 | 位置 / 说明 |
| :-- | :-- | :-- | :-- |
| P2-1 | Dart | **同步 API 阻塞事件循环**。`put*` 全程 `*Sync` 文件调用 + `flushSync()`；`replaceFileAtomicallyWithRetry` 退避用 `sleep()`，共享冲突时单次轮转最多阻塞 isolate **230 ms**。库的目标平台包含 Flutter，而 `Immediate` 模式下没有任何纯异步写路径可用。 | `db.dart:1911-1938`；建议提供 `putAsync` 或至少在 README 明确标注 UI 线程禁用。 |
| P2-2 | Dart | **跨 isolate / 跨进程不可用**。连接锁持有整个生命周期，同一个 DB 文件在同进程内只能有一个 isolate 打开。`compute()`、`Isolate.run` 这类常见后台计算写法会直接 `LockedError`。 | `db.dart:98-123`、`lock.dart`；文档需明确"单 isolate 独占"。 |
| P2-3 | .NET / Dart | **解码端的跨语言行为分叉**。同一串字节，两语言给出不同结果：<br>· bool：.NET `buf[0] != 0` 即 true（`BinaryHelper.cs:94-99`），Dart 对非 0/1 抛 `TypeMismatchError`（`binary_helper.dart:85-96`）。加载期只校验 `ValLen == 1`，**不校验字节取值**，所以外部生产者写入的 `Bool=0x02` 在 .NET 静默变 true、在 Dart 直接抛错。<br>· 定长解码长度不符时：.NET 抛 `CorruptedError`，Dart 抛 `TypeMismatchError`。 | 实测（同一份 72 字节构造文件，`temp/review-probe/craft_bool.py`）：<br>`.NET: open: OK / getBool("k") = True`<br>`Dart: open: OK / getBool("k") threw TypeMismatchError: Invalid boolean byte value 0x2`<br>建议：加载期补一条 `ValType==Bool && ValLen==1 && Value<=1` 的校验，让两端在**打开**时就一致地拒绝。 |
| P2-4 | Dart | `getString()` 每次读最多把同一个值**完整解码 3 遍**：`isValidUtf8()` 内部 `utf8.decode` 一遍、返回时 `utf8.decode` 再一遍；此外 `getBytes()` 已做过一次 `findIndex` + 全量拷贝，`getString` 又**再做一次 `findIndex`**。 | `db.dart:1326-1342`（`.NET db.cs:2597-2620` 同型但只多一遍校验扫描）；大字符串值的高频读场景可直接优化掉 2/3 的工作量。 |
| P2-5 | .NET | `Stats()` 的文档注释写"不触发磁盘 I/O"，实现里 `StatsInternal()` 却调用了 `FileSizeInternal()`（一次 stat）。 | `Db.cs:2387-2412` vs `Db.cs:2381-2385`。 |
| P2-6 | .NET / Dart | 锁退避是**固定间隔**（.NET 10 ms / Dart 50 ms），规范 §4.1.2 要求的是 10–100 ms 指数退避。10 s 预算下 .NET 会打 1000 次系统调用。 | `LockFile.cs:70`、`lock.dart:62` vs `KV-DB-DESIGN-SPEC.md:194`。 |
| P2-7 | .NET | `Utf8StringComparer.GetHashCode` 每次哈希都 `Encoding.UTF8.GetBytes` 分配一个新数组；`Compare` 每次比较分配两个。而该比较器挂在**每次写都要访问**的 `_namespaces` 上。 | `BinaryHelper.cs:145-168`；可缓存 UTF-8 字节或在字符串上做无损的 code-point 序比较。 |
| P2-8 | .NET | `CkvDb` 的 `~CkvDb()` 在终结器线程上做同步落盘 + 释放内核锁（`CloseRoot(fromFinalizer: true)`，2 s 超时）。若托管堆压力导致终结器延迟，`Open` 会持续拿到"看着活着、实际已死"的实例。`Snapshot` 有 `~Snapshot()` 兜底，但没有对应的超时/告警。 | `Db.cs:3302-3313`、`Db.cs:3346-3374`；建议文档化"必须 Dispose"并在 `onFinalizerLeak` 里带上超时信息。 |
| P2-9 | .NET / Dart | 只读打开时仍会把整个 `.bak` 读进内存再决定拒绝恢复；`_initAndRecover` 的分支③在检查 `autoRecoverFromBackup` **之前**就 `readAsBytesSync(bakPath)`。 | `db.dart:587-607`（.NET 同形：`Db.cs:986-1008`）。 |

---

## 4. 交叉验证数据（可复现）

探针源码：

* `temp/review-probe/dotnet-probe/`（`dotnet run -c Release -- <testIds>`）
* `temp/review-probe/dart-probe/`（`dart run bin/probe.dart <testIds>`）

### 4.1 .NET 汇总

```
[1] AOL put 分配量   pre=0..150000 → 6.5 KB/put .. 1178.5 KB/put（线性）
[6] AOL+Deferred    1086 → 536 puts/s（规模相关，违背文档承诺）
[3] Open 内存       12.65 MB 文件 → +34.8 MB 托管堆（2.8x）
[7] 孤立代理项      写 A 读 A 得到 B 的值；namespaces() 只剩 1 个
[8] clear 竞态      batch(clearNamespace) + 并发 put → 重开后键复活
[4] batch clear 竞态 第 6 次尝试即命中（另一组配置）
[10] 路径诊断      缺父目录 → 1 s 空转 + LockedError；目录路径 → 幽灵库
[11] Keys(prefix)   150k 条目 1 命中前缀 → 1.4 ms 全表扫描
[12] 有序插入      尾插 vs 中段插入差异很小（memmove 主导），非主要瓶颈
[13] 构造 Bool=0x02 open: OK / getBool("k") = True   （Dart 同文件：TypeMismatchError）
[5]  Clone() 成本   100k 项 0.29 ms / 781 KB；500k 项 0.99 ms / 3906 KB
```

### 4.2 Dart 汇总

```
[1] AOL Immediate   pre=0 1.010 ms → pre=150000 4.806 ms
[5] AOL Deferred    378 → 47 puts/s（慢 8x，比 .NET 更差）
[3] Open            12.65 MB / 60000 条目 → 88 ms
[4] batch delete    CorruptedError: Malformed entry payload at offset 77（库报废）
[6] 路径诊断        同 .NET
[7] 构造 Bool=0x02 open: OK / getBool("k") threw TypeMismatchError（与 .NET 分叉）
```

### 4.3 现有测试基线

```
dotnet test  →  已通过! 失败: 0，通过: 140
dart test    →  All tests passed! (95)
```

---

## 5. 建议补充的测试（当前全部缺失）

1. `batch` 内所有写入口的长度校验矩阵（key > 64 KB / value > 16 MB / ns > 255 B），**两种引擎模式各一份** —— 可直接挡住 P0-1。
2. `clearNamespace` / `batch(clearNamespace)` 与并发 `put` 的分叉断言：重开前后 `count()` 必须相等 —— 可挡住 P0-2。
3. 非法 UTF-16 输入（孤立代理项）的 API 级行为断言 —— 可挡住 P1-2。
4. `open()` 的路径前置校验：父目录缺失 / 路径是目录 / 无写权限，断言**不重试**且错误类型为 `IoError` —— 可挡住 P1-3/P1-4。
5. 大库 `open()` 的峰值内存断言（例如 64 MB 文件的托管堆增量上限），防止 P1-5 回归。
6. 写吞吐的规模不变性回归测试（AOL + Deferred，0 / 20k / 150k 三档，断言吞吐比 > 0.8）—— 可挡住 P1-1。
7. `Keys(ns, prefix)` 在 10 万条目命名空间上的耗时上界断言 —— 可挡住 P1-6。
8. Deferred 模式刷盘失败必须产生 `onWarning` —— 可挡住 P1-7。
9. **跨语言黄金向量**：把 `Bool=0x02`、定长类型长度不符等"解码分歧样本"做成 5 门语言共享的向量，统一在 `open()` 阶段报错 —— 可挡住 P2-3 类分叉。

---

## 6. 修复优先级建议

| 顺序 | 项 | 工作量 | 收益 |
| :--: | :-- | :-- | :-- |
| 1 | P0-1 Dart `BatchWriter.delete` 长度校验 + 编码层兜底断言 | 极小 | 消除"一次调用毁全库" |
| 2 | P0-2 clear 墓碑与内存清空合并进同一写锁临界区 | 中（.NET 改 `entriesFactory` 用法；Dart 结构对齐） | 消除静默分叉 |
| 3 | P1-1 回滚点改 undo-log | 中大，但代码注释已给出方案 | 兑现 AOL 性能承诺 |
| 4 | P1-2 `Utf8StringComparer` 自洽 + 拒绝孤立代理项 | 小 | 消除数据串号 |
| 5 | P1-3 / P1-4 `open()` 路径前置校验与错误归因 | 小 | 消除 10 秒挂死与误导 |
| 6 | P1-5 流式解析（去掉全量 `LoadEntry`） | 大 | 决定 100 MB 级场景可用性 |
| 7 | P1-6 `keys`/`iterPrefix` 走二分起点 | 小 | 前缀查询回到 O(log n + k) |
| 8 | P1-7 后台刷盘失败告警 | 极小 | 消除静默丢数据 |

---

*本报告仅做审查与验证，未修改任何库代码；`temp/review-probe/` 下为一次性探针工程，可随时删除。*