# CarroKV 开发、测试与发布全流程指南

> 本文档规范 CarroKV 多语言 Monorepo 代码库的**本地开发环境配置**、**自动化测试与 Interop 验证**，以及**全语言发布操作流程（Playbook）**。

---

## 1. 开发环境要求

CarroKV 包含五种语言原生实现，本地开发和验证需安装以下工具链：

| 语言模块 | 目录 | 最低环境要求 | 常用校验工具 |
| :--- | :--- | :--- | :--- |
| **Rust** | `src/rust/` | `rustc 1.89+` / `cargo` | `cargo fmt`, `cargo clippy` |
| **.NET** | `src/dotnet/` | `.NET 8.0 SDK` (支持 netstandard2.0 与 net8.0) | `dotnet format`, `dotnet pack` |
| **Go** | `src/go/` | `Go 1.21+` | `go vet`, `go test` |
| **Kotlin** | `src/kotlin/` | `JDK 17+` (推荐 Temurin/Corretto) + 内置 Gradle 8.14 Wrapper | `./gradlew check` |
| **Dart** | `src/dart/` | `Dart 3.0+ SDK` (或 Flutter 3.10+) | `dart analyze`, `dart format` |

---

## 2. 本地构建与测试命令速查

### 2.1 各语言单模块测试

在项目根目录下或进入各子目录执行：

#### Rust 模块：
```bash
cd src/rust
# 格式检查与静态分析
cargo fmt --check
cargo clippy --all-targets -- -D warnings
# 运行单元测试、集成测试与文档测试
cargo test --release
```

#### .NET 模块：
```bash
cd src/dotnet
# 还原依赖
dotnet restore tests/Ckv.Tests/Ckv.Tests.csproj
# 执行测试
dotnet test tests/Ckv.Tests/Ckv.Tests.csproj -c Release
# 验证打包无警告
dotnet pack src/Ckv/Ckv.csproj -c Release -o ../../temp/artifacts
```

#### Go 模块：
```bash
cd src/go
# 静态检查与全部测试
go vet ./...
go test -v -race ./...
```

#### Kotlin 模块：
```bash
cd src/kotlin
# 使用仓库内置 Gradle 8.14 Wrapper 运行全部测试
./gradlew test --no-daemon
# 验证本地发布构件
./gradlew publishToMavenLocal -x test
```

#### Dart 模块：
```bash
cd src/dart
# 代码分析与格式化
dart analyze
dart format --output=none --set-exit-if-changed .
# 运行全部测试
dart test
```

---

### 2.2 跨语言确定性与 Interop 验证

CarroKV 的核心基石是**字节级跨语言确定性与互通**。CI 与本地测试包含两层严格校验：

#### (1) 黄金测试夹具哈希锁定 (Golden Vectors)
位于 `testdata/` 目录下的 `.db` 文件由 `testdata/MANIFEST.txt` 固化 SHA-256 哈希，绝对禁止静默修改：
```bash
# Linux / macOS
awk 'NF>=3 && $1 ~ /\.db$/ {print $3"  testdata/"$1}' testdata/MANIFEST.txt | sha256sum -c

# Windows (pwsh)
Get-Content testdata/MANIFEST.txt | Where-Object { $_ -match '\.db$' } | ForEach-Object {
    $parts = $_.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    $hash = (Get-FileHash -Algorithm SHA256 "testdata/$($parts[0])").Hash.ToLower()
    if ($hash -ne $parts[2]) { throw "Hash mismatch on $($parts[0])" }
}
```

#### (2) 五语言交叉互读写链 (Five-Way Interop)
所有语言统一遵守环境变量 `CKV_INTEROP_DIR` 契约（默认为 `temp/interop`）：
1. **Rust 写**：生成 `rust_data.db`；
2. **Go 读并写**：读取 `rust_data.db` 校验并写入 `go_data.db`；
3. **Kotlin & Dart 写**：读取既有夹具并生成 `kotlin_data.db` 与 `dart_data.db`；
4. **.NET 读并写**：读取其他四语言数据并写入 `dotnet_data.db`；
5. **Rust 反向读全部**：读取 Go、Kotlin、Dart、.NET 生成的数据文件，验证全量无损互读。

---

## 3. 全语言统一发布流程 (Release Playbook)

### 3.1 版本命名规范与 Git Tag 机制

CarroKV 严格遵循 [Semantic Versioning 2.0.0](https://semver.org/)（例如 `0.2.0`）。

由于代码库采用 Monorepo 架构，发版时涉及两种 Tag：
1. **全局主版本 Tag**：`vX.Y.Z`（例如 `v0.2.0`）
   * 触发 GitHub Actions 自动向 **NuGet.org** 发布 .NET 包；
   * 驱动 **JitPack** 云端为 Kotlin 模块构建 Maven Artifacts；
   * 驱动 **Rust** 与 **Dart** 的 Git 依赖引用。
2. **Go 专用子目录 Tag**：`src/go/vX.Y.Z`（例如 `src/go/v0.2.0`）
   * 专用于满足 Go Modules 对 Monorepo 子目录模块的版本识别规范，确保下游 `go get ...@vX.Y.Z` 成功。

---

### 3.2 各语言发布执行步骤

#### 步骤 1：本地提交与推送 master
在完成代码修改与全语言测试后，提交代码并推送到 GitHub：
```bash
git add .
git update-index --chmod=+x src/kotlin/gradlew   # 确保 Linux 环境执行权限
git commit -m "release: prepare release v0.2.0"
git push origin master
```

---

#### 步骤 2：发布 Rust 模块 (`carro_kv`) 到 crates.io
由于 crates.io 强制要求 2FA 并在本地通过 Token 发布：
```bash
cd src/rust
# 1. 登录 (仅首次需要)
cargo login <YOUR_CRATES_IO_TOKEN>

# 2. 预演检查
cargo publish --dry-run

# 3. 正式发布
cargo publish
cd ../..
```
*下游使用*：`cargo add carro_kv` 或在 `Cargo.toml` 中写入 `carro_kv = "0.2.0"`。

---

#### 步骤 3：推送 Git Tag 触发 .NET (NuGet) 与 Kotlin (JitPack) 发布
打上全局版本 Tag 并推送到 GitHub：
```bash
git tag v0.2.0
git push origin v0.2.0
```
推送后触发的行为：
* **.NET (NuGet)**：
  * GitHub Actions 自动运行 `.github/workflows/publish-nuget.yml`；
  * 使用微软官方 **Trusted Publishing（OIDC 信任认证）** 零密钥自动打包并推送到 NuGet.org；
  * *下游使用*：`dotnet add package CarroKV`。
* **Kotlin (JitPack)**：
  * 访问 [jitpack.io/#mcxiaoke/CarroKV](https://jitpack.io/#mcxiaoke/CarroKV)；
  * 输入 `mcxiaoke/CarroKV`，点击 **Look up**，找到 `v0.2.0` 点击 **Get it** 触发首发构建；
  * 检查构建 Log 状态变为绿色（OK）；
  * *下游使用*：`implementation("com.github.mcxiaoke:CarroKV:v0.2.0")`。

---

#### 步骤 4：推送 Go 专有子目录 Tag (Go 模块生效)
打上带子目录前缀的 Tag：
```bash
git tag src/go/v0.2.0
git push origin src/go/v0.2.0
```
*下游使用*：
```bash
go get github.com/mcxiaoke/CarroKV/src/go@v0.2.0
```

---

#### 步骤 5：Dart 模块提供引用
Dart 模块在打上全局 `v0.2.0` Tag 后自动可被下游直接消费：
*下游使用*：
```yaml
dependencies:
  ckv:
    git:
      url: https://github.com/mcxiaoke/CarroKV.git
      ref: v0.2.0
      path: src/dart
```

---

## 4. 故障排查与常见问题

1. **JitPack 构建失败**：
   * 检查仓库根目录的 `jitpack.yml` 是否配置了 `cd src/kotlin && chmod +x ./gradlew`；
   * 确保 `src/kotlin/gradlew` 具有可执行权限；
   * 确认 `build.gradle.kts` 中配置了 `maven-publish` 插件。
2. **NuGet Actions 提示未经授权**：
   * 检查 NuGet.org 的 Trusted Publishing Policy：
     * Repository Owner 是否为 `mcxiaoke`；
     * Repository 是否为 `CarroKV`；
     * Workflow File 是否严格填写为 `publish-nuget.yml`。
3. **Go 下游拉取报错 `unexpected module path`**：
   * 检查 `src/go/go.mod` 第一行是否为 `module github.com/mcxiaoke/CarroKV/src/go`。
