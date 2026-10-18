# CarroKV for Go

**CarroKV for Go** 是 CKV 嵌入式 Key-Value 数据库引擎的纯 Go 原生实现，严格遵循 [CKV 二进制规范 v3.2](../../docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口规范](../../docs/API-SPEC.md)。

专为轻量微服务、CLI 命令行工具、边缘计算与小型状态存储设计，不依赖任何 CGO 动态库或外部厚重依赖。

---

## 安装与引入

使用 Go 官方模块工具直接获取：

```bash
go get github.com/mcxiaoke/CarroKV/src/go@v0.2.0
```

在 Go 代码中导入：

```go
import (
    "github.com/mcxiaoke/CarroKV/src/go"
)
```

---

## 核心特性

- **100% 纯 Go 实现（Zero CGO / Zero FFI）**：
  跨平台构建极其简单，完全杜绝交叉编译困难与 CGO 带来的性能与运行时隐患。
- **纳秒级内存读取**：
  数据常驻内存有序结构，点查仅需数十纳秒，无磁盘 I/O 阻塞。
- **跨语言字节级确定性与互通**：
  与 **Rust**、**.NET**、**Kotlin**、**Dart** 共享严格一致的 IEEE 802.3 CRC32 校验与小端序二进制存储，`.db` 文件在各语言间无缝互读互写。
- **高韧性原子崩溃恢复**：
  采用两代轮转模型（`.tmp -> .bak -> .db`），进程异常退出或断电重启自动晋升完整数据。
- **死锁防御的变更监听器 (Watcher)**：
  在写锁完全释放后异步派发事件通知，在回调中重入读主库绝对安全、永不发生死锁。
- **无锁只读快照 (Snapshot Isolation)**：
  纳秒级生成只读视图，长耗时范围扫描与主库高频写入完全互不阻塞。

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```go
package main

import (
    "fmt"
    "log"

    "github.com/mcxiaoke/CarroKV/src/go"
)

func main() {
    // 1. 打开或创建数据库（默认选项）
    db, err := ckv.Open("app_data.db", nil)
    if err != nil {
        log.Fatalf("failed to open db: %v", err)
    }
    defer db.Close()

    // 2. 存取字符串 (String)
    if err := db.PutString("users", "user:1001", "Alice"); err != nil {
        log.Fatal(err)
    }
    name, err := db.GetString("users", "user:1001")
    if err != nil {
        log.Fatal(err)
    }
    fmt.Printf("User Name: %s\n", *name)

    // 3. 存取原始二进制字节流 (Bytes)
    avatarBytes := []byte{0xDE, 0xAD, 0xBE, 0xEF}
    _ = db.PutBytes("users", "user:1001:avatar", avatarBytes)

    // 4. 强类型整数与原子自增
    _ = db.PutInt64("stats", "page_views", 1000)
    newViews, _ := db.IncrementInt64("stats", "page_views", 1)
    fmt.Printf("New Views: %d\n", newViews)

    // 5. 探查键与删除
    if exists, _ := db.ContainsKey("users", "user:1001"); exists {
        deleted, _ := db.Delete("users", "user:1001")
        fmt.Printf("Deleted: %v\n", deleted)
    }
}
```

### 2. 命名空间与范围扫描

```go
// 枚举所有命名空间（按 UTF-8 升序排列）
namespaces := db.Namespaces()

// 获取前缀匹配的键
keys, _ := db.Keys("users", "user:")

// 缓冲式前缀匹配条目
entries, _ := db.IterPrefix("users", "user:")
for _, entry := range entries {
    fmt.Printf("Key: %s, Value: %v\n", entry.Key, entry.Value)
}

// 左闭右开范围切片扫描 [startKey, endKey)
start := "user:1000"
end := "user:2000"
rangeEntries, _ := db.ScanRange("users", &start, &end)
```

### 3. 原子批处理事务 (Batch)

批处理支持链式操作与读己所写（Read-Your-Own-Writes），所有变更单次原子落盘提交：

```go
err := db.Batch(func(b *ckv.BatchWriter) error {
    if err := b.PutString("orders", "order:001", "created"); err != nil {
        return err
    }
    if err := b.PutInt64("orders", "order:001:price", 9900); err != nil {
        return err
    }
    return b.Delete("orders", "order:draft:001")
})
```

### 4. 无锁只读快照隔离 (Snapshot)

```go
// 瞬时创建只读快照
snap, err := db.Snapshot()
if err != nil {
    log.Fatal(err)
}
defer snap.Close()

// 快照只读扫描期间，外部主库高频并发写完全互不阻塞
entries, _ := snap.ScanRange("users", nil, nil)
```

### 5. 数据变更监听器 (Watcher)

```go
// 注册监听器
watcher := db.Watch(func(ev ckv.KeyChangeEvent) {
    fmt.Printf("Event: Op=%v, Ns=%s, Key=%s\n", ev.Op, ev.Namespace, ev.Key)
})

// 在回调之外触发写操作，回调中发起重入读绝对安全
_ = db.PutString("users", "user:1002", "Bob")

// 注销监听器
watcher.Unwatch()
```

---

## 配置选项 (`Options`)

```go
opts := &ckv.Options{
    // ⚠️ 快照原子轮转每次写入都会全量序列化整库并 fsync，成本 O(库大小)。
    //    实测稳态写延迟中位数：500 条目 2.29ms、2,000 条目 2.93ms、5,000 条目 4.43ms；
    //    AppendOnlyLog 稳态约 0.18ms/写且与库大小无关。
    //    数据量超过约 2MB 或存在高频写入时，请选 ckv.StorageEngineAppendOnlyLog。
    EngineMode:               ckv.StorageEngineSnapshotAtomic,
    SyncMode:                 ckv.SyncModeImmediate,
    SyncIntervalMs:           100,
    ReadOnly:                 false,
    AutoRecoverFromBackup:    false,
    LockTimeoutMs:            10000,
}

db, err := ckv.Open("custom.db", *opts)
```

> **同路径二次打开的选项冲突**：`ReadOnly` 与既有实例不一致时返回
> `ConflictingOptionsError`（绝不静默返回一个权限不同的实例）；
> `EngineMode` / `SyncMode` 不一致时通过 `OnWarning("onOptionsConflict")` 告警并沿用既有 options。

---

## 错误处理体系

CarroKV 的 Go 实现导出了符合跨语言规范的强类型错误结构，支持通过 `errors.As` 精确断言：

* `*ckv.CorruptedError`：数据库物理损坏或魔数、CRC32 校验失败；
* `*ckv.LockedError`：排他文件锁被占用超时，或只读模式下尝试写入；
* `*ckv.TypeMismatchError`：类型守卫拦截（例如尝试将 Int64 读取为 String）；
* `*ckv.ConcurrentModificationError`：检测到外部并发修改或版本冲突；
* `*ckv.KeyTooLargeError`：Key > 64KB 或 Value > 16MB；
* `*ckv.InvalidUtf8Error`：字符串编码非有效 UTF-8；
* `*ckv.IoError`：底层系统磁盘 I/O 错误。
