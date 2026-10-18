# CKV 开发与 CI 本地预检准则 (AGENTS.md)

本项目为Key-Value DB库，多语言 Monorepo，包含 **Rust / .NET / Go / Dart / Kotlin** 五种原生实现。

---

## 1. 提交前必跑命令速查

在对应语言子目录下执行以下命令：

### Rust (`src/rust/`)
> CI 严格要求 `clippy` 零警告，任何 `match` 冗余或未处理警告都会导致构建失败。
```bash
# 1. 自动格式化（保证 cargo fmt --check 零差异）
cargo fmt

# 2. 静态分析（CI 会将警告视为错误）
cargo clippy --all-targets -- -D warnings

# 3. 运行测试
cargo test --release
```

### .NET (`src/dotnet/`)
> CI 要求代码格式符合 dotnet 规范，无格式偏差。
```bash
# 1. 自动格式化代码
dotnet format

# 2. 验证格式无未提交变更（等价于 CI 检查）
dotnet format --verify-no-changes

# 3. 运行测试
dotnet test tests/Ckv.Tests/Ckv.Tests.csproj -c Release
```

### Go (`src/go/`)
> CI 会校验 `gofmt` 是否干净，并执行 `go vet`。
```bash
# 1. 自动格式化所有 Go 代码
gofmt -w .

# 2. 静态分析检查
go vet ./...

# 3. 运行单元测试
go test -v ./...
```

### Dart (`src/dart/`)
> CI 要求 `dart analyze` 0 issues，且代码格式无差异。
```bash
# 1. 自动格式化代码
dart format .

# 2. 代码静态分析（必须 0 issues）
dart analyze

# 3. 运行全部测试
dart test
```

### Kotlin (`src/kotlin/`)
> CI 会运行全量 `gradle check`（包含编译校验与全部单测）。
```bash
# Windows
.\gradlew.bat check --no-daemon

# Linux / macOS
./gradlew check --no-daemon
```

---

## 2. 跨语言确定性校验 (Golden Vectors)

若涉及数据库底层二进制编解码（文件头、BatchFrame、CRC32、序列化等）的改动，必须确保测试向量未被意外破坏：

```powershell
# Windows (PowerShell)
Get-Content testdata\MANIFEST.txt | Where-Object { $_ -match '\.db' } | ForEach-Object {
    $tokens = -split $_
    $file = $tokens[0]
    $expected = $tokens[2]
    $actual = (Get-FileHash "testdata\$file" -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) { throw "Hash mismatch on $file" }
}
```

```bash
# Linux / macOS (Bash)
awk 'NF>=3 && $1 ~ /\.db$/ {print $3"  testdata/"$1}' testdata/MANIFEST.txt | sha256sum -c
```

---

## 3. 标准开发提交流程

1. **修改代码**：针对具体语言模块进行编码与测试；
2. **执行格式化**：`cargo fmt` / `dotnet format` / `gofmt -w .` / `dart format .`；
3. **执行静态分析**：`cargo clippy` / `dotnet format --verify-no-changes` / `go vet` / `dart analyze` / `gradle check`；
4. **运行本地测试**：确保单测 100% 绿灯；
5. **记录变更与提交**：在 `docs/CHANGES-YYYYMMDD.md` 记录要点后再执行 Git 提交与推送。
