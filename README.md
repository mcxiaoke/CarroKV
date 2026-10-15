# CKV (Cross-language Key-Value Database)

**CKV** 是一个轻量级、跨语言、纯单文件嵌入式 Key-Value 数据库引擎，严格遵循 [CKV v3.2 二进制设计规范](docs/KV-DB-DESIGN-SPEC.md)。

它专为桌面客户端、移动端、边缘计算以及轻量微服务打造，提供极佳的开发体验、纳秒级读取性能与工业级的抗断电数据韧性。

---

## 核心设计理念

1. **零 Native 动态库依赖（Zero Native Dependency）**
   - 不依赖 RocksDB、SQLite、LMDB 等厚重的 C/C++ 共享库（无需携带 `.dll` / `.so` / `.dylib`）。
   - 各语言均为 100% 纯托管或标准工具链原生构建，彻底杜绝 P/Invoke、CGO 或 FFI 导致的崩溃与跨平台分发难题。
2. **纳秒级内存读取性能**
   - 数据常驻内存有序结构（BTreeMap / 红黑树 / 有序字典）。
   - 只读操作无系统调用、无磁盘 I/O、无数据反序列化，单次点查仅需数十纳秒。
3. **字节级跨语言确定性与互通性**
   - 统一内联查表 IEEE 802.3 CRC32 算法，统一 0..255 无符号 UTF-8 字节序。
   - 同一份 `.db` 文件可在 **Rust**、**Go**、**.NET** 之间自由热插拔无损互读互写。
4. **两代原子轮转自愈模型（Crash-Safe & Self-Healing）**
   - 写入采用 `.tmp -> .bak -> .db` 极速原子覆盖轮转，写放大为 0%。
   - 启动自动检测并晋升未竟临时文件；遭遇断电坏块时毫秒级自动从 `.bak` 备份恢复，杜绝静默清空数据。
5. **死锁防御的数据变更监听器（Watcher）**
   - 支持实时订阅键值的新增、修改、删除与命名空间清空。
   - 核心铁律：内部在持有写锁期间收集事件，在**写锁完全释放后**派发给监听回调，保证回调中重入读操作绝对安全、永不发生死锁。

---

## 多语言实现矩阵

| 语言 | 目录 | 平台 / 运行时支持 | 状态 | 说明文档 |
| :--- | :--- | :--- | :--- | :--- |
| **.NET / C#** | `src/dotnet/` | `.NET Standard 2.0+`, `.NET 8.0+`, Mono, Unity | ✅ 已就绪 (100% 托管) | [👉 .NET 使用与 API 文档](src/dotnet/README.md) |
| **Rust** | `src/rust/` | `rustc 1.70+` (Windows, Linux, macOS) | ✅ 已就绪 (标准库轻量实现) | *(待 API 稳定后补充)* |
| **Go** | `src/go/` | `Go 1.21+` (Windows, Linux, macOS) | ✅ 已就绪 (标准库纯 Go) | *(待 API 稳定后补充)* |
| **Python** | `src/python/` | Python 3.9+ | 📋 规划中 | - |
| **Node.js** | `src/nodejs/` | Node 18+, Bun, Deno | 📋 规划中 | - |
| **Java** | `src/java/` | JDK 17+ | 📋 规划中 | - |

---

## 核心功能一览

### 1. 常见数据类型强类型支持
CKV 对常见应用数据类型提供原生编码与强类型安全拦截：
- **Bytes** (`0x00`): 二进制原始字节流
- **String** (`0x01`): UTF-8 字符串（严格进行 Unicode 编码校验）
- **Int64** (`0x02`): 64 位带符号小端补码整数
- **Float64** (`0x03`): 64 位 IEEE 754 双精度浮点数（内置 NaN 规范化归一）
- **Bool** (`0x04`): 单字节布尔值

### 2. 实用 API (P0)
- `contains_key(ns, key)`: O(1) 高效判断键是否存在，避免反序列化与多余的内存拷贝。
- `count(ns)`: 获取指定命名空间下的有效条目总数。
- `clear_namespace(ns)`: 快速清空指定命名空间下的所有数据并触发原子快照落盘。
- `increment_i64(ns, key, delta)`: 原子的 64 位整数累加/递减器。若键不存在自动初始化为 `delta`；若存在且为 `int64` 则累加；若类型不匹配抛出 `TypeMismatchError` 强类型拦截。

### 3. 事务批处理 (Batch)
支持纯内存暂存区（Staging Area）批处理。在批处理回调中执行的写操作在提交前均停留在暂存区，若发生异常或主动抛错则**全额丢弃回滚**；成功后原子合入主状态并落盘，最后向监听器派发事件。

### 4. 字典序范围扫描 (ScanRange)
所有数据按 **无符号 UTF-8 字节序** 绝对升序存储，支持高效的 `keys(ns, prefix)` 前缀匹配与 `scanRange(ns, startKey, endKey)` 区间迭代。

---

## 项目导航

- [CKV 核心设计规范 (v3.2)](docs/KV-DB-DESIGN-SPEC.md)：详细规定磁盘帧格式、锁模型、确定性贪心装箱算法、黄金测试向量与跨语言行为准则。
- [.NET 说明文档与完整 API 指南](src/dotnet/README.md)：针对 .NET 开发者的快速上手指南、代码示例与异常体系说明。
- [变更记录 (docs/CHANGES-20261015.md)](docs/CHANGES-20261015.md)：记录重要的代码演进与跨语言工程实践。
