# CarroKV for Dart / Flutter

**CarroKV for Dart** 是 CKV 跨语言嵌入式 Key-Value 数据库引擎的纯 Dart 原生实现，严格遵循 [CKV 二进制规范 v3.2](../../docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口规范](../../docs/API-SPEC.md)。

专为 Flutter 移动端（iOS / Android）、桌面端（Windows / macOS / Linux）以及 Dart 命令行/后端工具打造，不依赖任何原生 SQLite 库或 C/C++ 共享库，杜绝 FFI 平台适配与崩溃隐患。

---

## 安装与引入 (Git 依赖)

在 Flutter 或 Dart 项目的 `pubspec.yaml` 中添加：

```yaml
dependencies:
  ckv:
    git:
      url: https://github.com/mcxiaoke/CarroKV.git
      ref: v0.1.0 # 亦可指向 master 或具体提交 SHA
      path: src/dart # 必须指定子目录
```

在 Dart 代码中导入：

```dart
import 'package:ckv/ckv.dart';
```

---

## 核心特性

- **100% 纯 Dart 实现（Zero FFI / Zero Native Dependency）**：
  无需携带任何动态链接库，零编译配置，无缝运行在 Flutter 全平台与原生 Dart VM 上。
- **全内存纳秒级读取**：
  数据常驻内存有序结构，点查单次读取仅需数十纳秒，UI 线程绝不掉帧卡顿。
- **跨语言确定性与字节互通**：
  与 **Rust**、**.NET**、**Go**、**Kotlin** 共享完全一致的 IEEE 802.3 CRC32 校验与无符号 UTF-8 字节序，`.db` 文件在各语言间自由热插拔无损互读互写。
- **高韧性原子崩溃自愈**：
  采用两代轮转模型（`.tmp -> .bak -> .db`），App 遭遇突发闪退或断电重启毫秒级恢复，不丢数据。
- **死锁防御的变更监听器 (Watcher)**：
  在写锁释放后异步派发事件通知，回调中重入读主库绝对安全、永不发生死锁。
- **无锁只读快照 (Snapshot Isolation)**：
  瞬时创建时间点只读视图，长耗时列表扫描与主库高频写入完全互不阻塞。

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```dart
import 'package:ckv/ckv.dart';

void main() {
  // 打开或创建数据库
  final db = CkvDb.open('app_data.db');
  try {
    // 1. 存取字符串 (String)
    db.putString('users', 'user:1001', 'Alice');
    final name = db.getString('users', 'user:1001');
    print('User Name: $name'); // Output: Alice

    // 2. 存取原始二进制字节流 (Bytes)
    final avatar = [0x01, 0x02, 0x03, 0x04];
    db.putBytes('users', 'user:1001:avatar', avatar);
    final readAvatar = db.getBytes('users', 'user:1001:avatar');

    // 3. 强类型整数与原子自增
    db.putInt64('stats', 'page_views', 1000);
    final newViews = db.incrementInt64('stats', 'page_views', 1);
    print('New Views: $newViews');

    // 4. 探查键与删除
    final exists = db.containsKey('users', 'user:1001');
    final deleted = db.delete('users', 'user:1001');
  } finally {
    db.close();
  }
}
```

### 2. 命名空间与范围扫描

```dart
// 枚举所有命名空间（按 UTF-8 升序排列）
final List<String> namespaces = db.namespaces();

// 前缀匹配获取所有键名
final List<String> keys = db.keys('users', prefix: 'user:');

// 缓冲式前缀匹配条目
final List<Entry> entries = db.iterPrefix('users', prefix: 'user:');
for (final entry in entries) {
  print('${entry.key} -> ${entry.value}');
}

// 范围切片遍历 [startKey, endKey)
final rangeEntries = db.scanRange('users', startKey: 'user:1000', endKey: 'user:2000');
```

### 3. 原子批处理事务 (Batch)

批处理支持链式操作与读己所写（Read-Your-Own-Writes），所有变更单次原子提交：

```dart
db.batch((b) {
  b.putString('orders', 'order:001', 'created')
   .putInt64('orders', 'order:001:amount', 9900)
   .delete('orders', 'order:draft:001');
});
```

### 4. 无锁只读快照隔离 (Snapshot)

```dart
// 瞬时创建只读快照
final snap = db.snapshot();
try {
  // 快照只读扫描期间，外部主库高频并发写完全互不阻塞
  final snapItems = snap.scanRange('users');
  for (final item in snapItems) {
    print('Snap: ${item.key}');
  }
} finally {
  snap.close();
}
```

### 5. 数据变更监听器 (Watcher)

```dart
// 注册监听器（返回注销句柄）
final watcher = db.watch((event) {
  print('Change: op=${event.op}, ns=${event.namespace}, key=${event.key}');
});

// 在回调之外触发写操作，回调中发起重入读绝对安全
db.putString('users', 'user:1002', 'Bob');

// 显式注销监听
watcher.unwatch();
```

---

## 配置选项 (`DbOptions`)

```dart
final options = DbOptions(
  storageEngineMode: StorageEngineMode.snapshotAtomic, // 快照原子轮转或追加日志
  syncMode: SyncMode.always,                           // 同步策略：always, periodic, deferred, manual
  syncIntervalMs: 50,                                  // 后台刷盘检查周期（毫秒）
  readOnly: false,                                     // 是否以只读模式打开
  autoRecoverFromBackup: false,                        // 主库物理损坏时是否自动从 .bak 恢复晋升
  lockTimeoutMs: 10000,                                // 获取排他锁超时时间（毫秒）
);

final db = CkvDb.open('custom.db', options: options);
```

---

## 异常体系 (`DbError`)

所有的异常类型均继承自标准 `DbError`：

- `CorruptedError`：数据库格式损坏（魔数错误、CRC32 校验失败等）；
- `LockedError`：获取数据库锁超时或被其他进程占用；
- `ConcurrentModificationError`：检测到外部并发修改或版本冲突；
- `TypeMismatchError`：强类型守卫拦截（例如尝试将 Int64 读取为 String）；
- `InvalidUtf8Error`：字符串包含非法 UTF-8 字节序列；
- `KeyTooLargeError`：Key > 64KB 或 Value > 16MB；
- `InvalidNamespaceError`：命名空间超限或不合法；
- `UnsupportedVersionError`：文件版本高于当前库支持的最高读取版本；
- `IoError`：底层系统磁盘 I/O 错误。
