# CarroKV for Kotlin (JVM)

[English](README.md) | **简体中文**

**CarroKV for Kotlin** 是 CKV 嵌入式 Key-Value 数据库引擎的纯 Kotlin / JVM 原生实现，严格遵循 [CKV 二进制规范 v3.2](../../docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口规范](../../docs/API-SPEC.zh.md)。

专为 Android 客户端、Kotlin Multiplatform 桌面端以及后端微服务设计，不依赖任何 SQLite、RocksDB 或 C/C++ 共享库，无任何 JNI / FFI 崩溃风险。

---

## 安装引入 (JitPack)

### 1. 添加 JitPack 仓库
在 `settings.gradle.kts` 中添加 JitPack 仓库：

```kotlin
dependencyResolutionManagement {
    repositories {
        mavenCentral()
        maven { url = uri("https://jitpack.io") }
    }
}
```

### 2. 添加依赖
在模块的 `build.gradle.kts` 中添加：

```kotlin
dependencies {
    implementation("com.github.mcxiaoke:CarroKV:v0.2.1")
}
```

在 Kotlin 代码中导入：
```kotlin
import ckv.CkvDb
import ckv.DbOptions
```

---

## 核心特性

- **100% 纯 Kotlin / JVM 实现（Zero Native Dependency）**：
  不依赖任何 `.so` 或 `.dll`，体积轻量，开箱即用，天然兼容全平台 JVM。
- **全内存纳秒级读取**：
  数据常驻内存有序结构，点查仅需数十纳秒，无磁盘 I/O 停顿。
- **跨语言确定性与字节互通**：
  与 **Rust**、**.NET**、**Go**、**Dart** 共享统一 IEEE 802.3 CRC32 校验码与无符号字节序，`.db` 文件在各语言间无缝互读互写。
- **高韧性原子崩溃自愈**：
  采用 `.tmp -> .bak -> .db` 极速原子轮转，断电或异常崩溃重启毫秒级恢复，不丢数据。
- **无死锁事件通知 (Watcher)**：
  在写锁完全释放后派发变更通知，回调内重入读主库绝对安全、永不发生死锁。
- **无锁只读快照 (Snapshot Isolation)**：
  纳秒级瞬时创建时间点视图，长耗时范围扫描与主库高频写入完全互不阻塞。

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```kotlin
import ckv.CkvDb

fun main() {
    // 打开或自动创建数据库（实现 AutoCloseable，支持 use 语法糖）
    CkvDb.open("app_data.db").use { db ->
        // 1. 存取字符串 (String)
        db.putString("users", "user:1001", "Alice")
        val name = db.getString("users", "user:1001")
        println("User Name: $name") // Output: Alice

        // 2. 存取原始字节数组 (Bytes)
        val avatar = byteArrayOf(0x01, 0x02, 0x03, 0x04)
        db.putBytes("users", "user:1001:avatar", avatar)
        val readAvatar = db.getBytes("users", "user:1001:avatar")

        // 3. 强类型整数与原子自增
        db.putInt64("stats", "page_views", 1000L)
        val newViews = db.incrementInt64("stats", "page_views", 1L)
        println("New Views: $newViews")

        // 4. 探查键与删除
        val exists = db.containsKey("users", "user:1001")
        val deleted = db.delete("users", "user:1001")
    }
}
```

### 2. 免传命名空间与操作符重载语法糖 (Convenience & Operator API)

当不需要多命名空间隔离时，可直接省略 `ns` 参数操作默认命名空间 `""`，或使用 Kotlin `[]` 操作符：

```kotlin
// 1. 操作符重载（默认命名空间索引访问）
db["theme"] = "dark"
val theme: String? = db["theme"]

// 2. 免传 ns 参数的强类型 CRUD
db.putString("session_token", "xyz_token_123")
val token: String? = db.getString("session_token")
val hasToken: Boolean = db.containsKey("session_token")
db.delete("session_token")

// 3. 命名空间作用域门面 (Namespace Scope / Bucket 风格)
// 为指定命名空间创建轻量门面，免去重复传参：
val users = db.ns("users")
users["user:1001"] = "Alice"
users.putInt64("user:1001:score", 100L)
val userName: String? = users.getString("user:1001")
val userList = users.scanRange("user:1000", "user:2000")

// 快照与原子批处理同样支持作用域：
db.batch { b ->
    val batchUsers = b.ns("users")
    batchUsers["user:1002"] = "Bob"
}
```

### 3. 命名空间与范围扫描

```kotlin
// 枚举所有命名空间（按无符号 UTF-8 升序排列）
val namespaces: List<String> = db.namespaces()

// 前缀匹配获取所有键
val keys: List<String> = db.keys("users", prefix = "user:")

// 缓冲式前缀匹配条目
val entries = db.iterPrefix("users", prefix = "user:")
for (entry in entries) {
    println("${entry.key} -> ${entry.value}")
}

// 范围切片遍历 [startKey, endKey)
val rangeEntries = db.scanRange("users", startKey = "user:1000", endKey = "user:2000")
```

### 4. 原子批处理事务 (Batch)

批处理支持链式操作与读己所写（Read-Your-Own-Writes），所有变更单次原子提交：

```kotlin
db.batch { b ->
    b.putString("orders", "order:001", "created")
    b.putInt64("orders", "order:001:amount", 9900L)
    b.delete("orders", "order:draft:001")
}
```

### 5. 无锁只读快照隔离 (Snapshot)

```kotlin
// 瞬时创建只读快照
db.snapshot().use { snap ->
    // 快照只读扫描期间，外部主库高频并发写完全互不阻塞
    val snapItems = snap.scanRange("users")
    for (item in snapItems) {
        println("Snap: ${item.key}")
    }
}
```

### 6. 数据变更监听器 (Watcher)

```kotlin
// 注册监听器（返回注销句柄）
val watcher = db.watch { event ->
    println("Change: op=${event.op}, ns=${event.namespace}, key=${event.key}")
}

// 在回调之外触发写操作，回调中发起重入读绝对安全
db.putString("users", "user:1002", "Bob")

// 显式注销监听
watcher.unwatch()
```

---

## 配置选项 (`DbOptions`)

```kotlin
import ckv.DbOptions
import ckv.StorageEngineMode
import ckv.SyncMode

val options = DbOptions(
    // ⚠️ SNAPSHOT 每次写入都会全量序列化整库并 fsync，成本 O(库大小)。
    //    实测稳态写延迟中位数：500 条目 2.29ms、2,000 条目 2.93ms、5,000 条目 4.43ms；
    //    APPEND_ONLY_LOG 稳态约 0.18ms/写且与库大小无关。
    //    数据量超过约 2MB 或存在高频写入时，请选 StorageEngineMode.APPEND_ONLY_LOG。
    engineMode = StorageEngineMode.SNAPSHOT,
    syncMode = SyncMode.IMMEDIATE,
    syncIntervalMs = 100,
    readOnly = false,
    autoRecoverFromBackup = false,
    lockTimeoutMs = 10000
)

val db = CkvDb.open("custom.db", options)
```

> **同路径二次打开的选项冲突**：`readOnly` 与既有实例不一致时抛
> `ConflictingOptionsError`（绝不静默返回一个权限不同的实例）；
> `engineMode` / `syncMode` 不一致时通过 `onWarning("onOptionsConflict")` 告警并沿用既有 options。

---

## 异常体系 (`DbError`)

所有的异常类型均继承自标准 `DbError`：

- `CorruptedError`：数据库格式损坏（魔数错误、CRC32 校验失败等）；
- `LockedError`：获取数据库锁超时或被其他进程占用；
- `ConcurrentModificationError`：检测到外部并发修改或版本不一致；
- `TypeMismatchError`：强类型守卫拦截（例如尝试将 Int64 读取为 String）；
- `InvalidUtf8Error`：字符串编码非有效 UTF-8；
- `KeyTooLargeError`：Key > 64KB 或 Value > 16MB；
- `InvalidNamespaceError`：命名空间超限或不合法；
- `UnsupportedVersionError`：文件版本高于当前库支持的最高读取版本；
- `IoError`：底层系统磁盘 I/O 错误。
