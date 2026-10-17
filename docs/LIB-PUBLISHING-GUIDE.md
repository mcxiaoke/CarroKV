# CKV 多语言库发布流程与复杂度分析

> 适用范围：CKV 五语言实现（Rust / Go / Dart / Kotlin / .NET）的对外分发。
> 生成日期：2026-10-17。**决策更新（同日本次）**：pre-1.0 阶段，**真正需要"发布"动作的只有 Kotlin（走 JitPack）和 .NET（NuGet）**；Rust / Go / Dart 因包管理器原生支持 git 依赖，**pre-1.0 直接用 GitHub tag + git 依赖即可，无需任何 registry**。Kotlin 的 Maven Central 正式发布推迟到 1.0 之后。
> 基于各平台 **2026 年当前** 真实规则（crates.io 强制 GitHub 2FA、OSSRH 已于 2025-06-30 关停、pub.dev 质量门禁、JitPack 作为 JVM 的 git 桥接方案）。
> 目标：给出真实发布流程、复杂度排序、CKV 各语言分发策略与待办清单。

---

## 0. TL;DR — 复杂度排序与 CKV 分发决策

| 排名 | 语言 | 目标仓库 | 一次性前置 | 强制门禁 | 综合复杂度 | **CKV pre-1.0 策略** |
| :--: | :--- | :--- | :--- | :--- | :---: | :--- |
| 1 | **.NET** | NuGet | 注册账号 + API Key | 仅病毒扫描 | **低** | ✅ **发布到 NuGet**（便宜，直接做） |
| 2 | **Rust** | crates.io | GitHub 2FA + API Token | 自动编译+测试校验 | **低~中** | ⏸️ pre-1.0 用 **git 依赖**，1.0 再发 crates.io |
| 3 | **Dart** | pub.dev | Google 账号 | **pana 质量评分门禁** | **中** | ⏸️ pre-1.0 用 **git 依赖**，1.0 再发 pub.dev |
| 4 | **Go** | 无需 registry | 无（import path = URL） | 无 | **极低** | ✅ **GitHub tag 即发布**，零成本 |
| 5 | **Kotlin** | **JitPack**（pre-1.0）→ Maven Central（1.0+） | JitPack：零配置；MC：命名空间+GPG | JitPack 无；MC 强制 GPG | **JitPack 低 / MC 高** | ✅ **pre-1.0 走 JitPack（git 桥接）** |

> **核心决策**：五语言中，包管理器**原生支持 git 依赖**的有 Go / Rust / Dart（Dart 3.12 起原生支持 git+LFS）。这三者 pre-1.0 可完全跳过 registry，仅靠 GitHub tag + git 依赖分发。**.NET 的 NuGet 发布足够便宜，直接做**。**Kotlin/JVM 的 Gradle/Maven 不支持 git 源依赖**，必须发到 Maven 仓库；用 **JitPack 作为"git 桥接"零配置方案**覆盖 pre-1.0，待 1.0 再上 Maven Central。因此 pre-1.0 真正要"动手发布"的**只有 Kotlin(JitPack) 与 .NET(NuGet)**。

---

## 1. .NET → NuGet.org（复杂度：低）

### 1.1 真实流程
1. **注册账号**：nuget.org 免费账号（Microsoft / GitHub 登录均可）。**无命名空间/DNS 验证**，包 ID 全局唯一即可（重名会被拒）。
2. **获取 API Key**：右上角用户 → API Keys → Create，Scope 选 `Push`，Glob Pattern 填 `*`，复制保存（仅显示一次）。支持按包/操作范围与过期时间做最小权限。
3. **打包**：`dotnet pack -c Release`（也可在 .csproj 设 `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` 让每次 build 自动出包）。产物 `bin/Release/<PackageId>.<version>.nupkg`。
4. **推送**：
   ```bash
   dotnet nuget push bin/Release/Ckv.0.1.0.nupkg \
     --api-key <NUGET_API_KEY> \
     --source https://api.nuget.org/v3/index.json
   ```
5. **生效**：nuget.org 自动病毒扫描 + 约 1–5 分钟校验后公开。**无需人工确认发布步骤**。

### 1.2 关键事实
- **签名非强制**：NuGet 支持 X.509 证书签名（`dotnet nuget sign`，提供完整性/来源证明），但公开 OSS 库不要求。
- **符号包可选**：`IncludeSymbols`/`SymbolPackageFormat=snupkg` 推荐但非必须。
- **README 入包需显式声明**：要在 nuget.org 页面显示 README，必须在 .csproj 加 `<PackageReadmeFile>README.md</PackageReadmeFile>` 并用 `<None Include="README.md" Pack="true" />` 纳入。
- **版本不可改**：发布后只能发新版本，旧版本无法修改/删除（可 unlist）。

### 1.3 CKV 当前状态与待办
当前 `src/dotnet/src/Ckv/Ckv.csproj` 已有 `TargetFrameworks=netstandard2.0;net8.0`、`Version`、`Description`。
待补齐（均写在 .csproj 的 `<PropertyGroup>` 内）：

```xml
<PackageId>Ckv</PackageId>
<Authors>mcxiaoke</Authors>
<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
<PackageProjectUrl>https://github.com/mcxiaoke/CarroKV</PackageProjectUrl>
<RepositoryUrl>https://github.com/mcxiaoke/CarroKV</RepositoryUrl>
<RepositoryType>git</RepositoryType>
<PackageTags>kv;embedded;database;cross-platform;no-ffi</PackageTags>
<PackageReadmeFile>README.md</PackageReadmeFile>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
```
并在 `src/dotnet/src/Ckv/` 下放置 `README.md`（可引用仓库根 README 或精简版），用 `<None Include="README.md" Pack="true" />` 纳入。
> 复杂度：**低**。约 0.5 天可落地，主要为填元数据。

---

## 2. Rust → crates.io（复杂度：低~中）

### 2.1 真实流程
1. **注册账号**：crates.io 用 GitHub 登录，**GitHub 账号必须开启 2FA**（短信方式不推荐）。
2. **生成 API Token**：crates.io 账号设置 → API Tokens → New Token。推荐 **scoped token**（限定到具体 crate、设过期时间，最长 90 天）。
3. **本地登录**：`cargo login`（交互粘贴 token，写入 `~/.cargo/credentials.toml`），或 CI 中用环境变量 `CARGO_REGISTRY_TOKEN`。
4. **预检**：`cargo package --list` 核对将打包的文件，避免泄漏密钥/`.env`；`cargo publish --dry-run` 预览。
5. **发布**：`cargo publish`。crates.io 会执行：打包 → 编译 → **自动跑测试校验（verify）** → 上传。首次发布同名 crate 需确认。
6. **生效**：几分钟内在搜索可见。版本只能 yank（标记不可用，代码仍可见），**不能删除/修改**。

### 2.2 关键事实
- **无 GPG 签名**：crates.io 不使用 PGP 签名；令牌安全是重中之重（不支持发布用 2FA，token 即凭据）。
- **crate 名抢占风险**：`ckv` 这个名字很通用，需提前在 crates.io 查是否被占；被占则需改名或先认领。
- **必须字段**：`name` / `version` / `description` / `license` / `repository`（推荐）/`readme`。`license` 须用 SPDX 表达式。
- **include/exclude**：用 allowlist（`include`）比 denylist 更安全，防止误打包敏感文件。

### 2.3 CKV 当前状态与待办
当前 `src/rust/Cargo.toml` **已相当完整**：含 name/version/edition/authors/description/license/repository/readme/rust-version/keywords/categories 及依赖。
待办：
1. 立即到 crates.io 查询 `ckv` 是否被占用；若被占，决定改名（如 `ckv-store`）或认领。
2. 补齐可选但推荐字段：`homepage`、`documentation`。
3. 确保包目录含 `LICENSE`、`README.md`（已存在）；`cargo package --list` 确认无多余文件。
4. `cargo publish`（建议先用 scoped token）。
> 复杂度：**低~中**。配置已基本就绪，主要风险是 **crate 名抢占**，需先确认。约 0.5 天。

---

## 3. Dart → pub.dev（复杂度：中）

### 3.1 真实流程
1. **注册账号**：pub.dev 用 Google 账号登录并接受服务条款。
2. **（推荐）验证发布者 Publisher**：用户菜单 → Create Publisher → 填域名（如 `mcxiaoke.com`）→ 经 Google Search Console 做 DNS TXT 验证。验证发布者后显示徽章、隐藏个人邮箱。*不验证也可用个人账号直接发，但官方推荐验证。*
3. **预检**：`dart pub publish --dry-run` 模拟，列出将上传文件并报告问题，最终应显示 `Package has 0 warnings.`
4. **发布**：`dart pub publish`（首次需 `dart pub login` 完成 Google OAuth；CI 用 `--force` 非交互）。
5. **质量门禁（核心复杂度）**：发布后 pub.dev 跑 **`pana` 自动分析** 计算「pub points / 160」，影响页面星级。关键项：
   - 0 个 analyzer 警告（`dart analyze` 干净）；
   - 公共 API ≥20% 有 dartdoc 文档；
   - `homepage` 可被 HTTP HEAD 访问（建议填 GitHub 仓库 URL）；
   - 代码经过 `dart format`；
   - 包体积 **gzip 后 < 10 MB**。
6. **生效**：发布即公开，不可撤回（仅可标记 discontinued）。

### 3.2 关键事实
- **必须文件**：`LICENSE`、 `CHANGELOG.md`、 `README.md`、 合法 `pubspec.yaml`（含 name/version/description/environment）。这些文件需位于**包根目录**内。
- **pubspec 推荐字段**：`repository`、`homepage`、`issue_tracker`、`documentation`、`topics`（替代旧 keywords）；作者可用 `authors` 或发布者。
- **example/ 目录**：强烈建议提供示例，提升可用性与分数。

### 3.3 CKV 当前状态与待办
当前 `src/dart/pubspec.yaml` 仅有 name/description/version/environment + 依赖，**缺少发布所需元数据与文件**。
待办：
1. `pubspec.yaml` 补：`repository`、`homepage`、`issue_tracker`、`documentation`、`topics`。
2. 在 `src/dart/` 包根新建：`LICENSE`（Apache-2.0 全文）、`CHANGELOG.md`、`README.md`（当前仓库根有 README，但包内需独立一份）。
3. 代码加 dartdoc 注释（公共 API ≥20%），跑 `dart analyze` 与 `dart format` 清零警告。
4. `dart pub publish --dry-run` 做到 0 warnings，再 `dart pub publish`。
> 复杂度：**中**。真正的门槛是 **pana 质量评分**，需补文档与规范文件，比单纯推送更花时间。约 1–2 天。

---

## 4. Kotlin → JitPack（pre-1.0） / Maven Central（1.0+）

### 为什么 Kotlin 必须走 registry，而 Go/Rust/Dart 不用
Gradle/Maven **只从 Maven 仓库按坐标解析依赖，没有原生 git 源依赖**（与 Go / Rust / Dart 不同）。因此 JVM 库无法像其他语言那样"GitHub tag 即发布"，必须发到一个 Maven 仓库。**JitPack 正是 JVM 世界的"git 桥接"**：你把 GitHub repo 打 tag，它云端自动构建并暴露成 Maven artifact（`com.github.<user>:<repo>:<tag>`），对 Gradle 而言就是一个普通 Maven 仓库。

### 4.1 pre-1.0 方案：JitPack（零配置，复杂度：低）
1. **无需注册、无需 GPG、无需命名空间验证**。只要仓库能被 JitPack 用 Gradle 构建成功即可。
2. **坐标规则**：仓库 `github.com/mcxiaoke/CarroKV` → 消费者依赖
   ```kotlin
   repositories { maven { url = uri("https://www.jitpack.io") } }
   dependencies { implementation("com.github.mcxiaoke:CarroKV:v0.1.0") }
   ```
3. **所需最小配置**：JitPack 默认对 Gradle 项目执行 `install` 任务产出 Maven artifact。需让项目可被 `install`/`publishToMavenLocal`：
   - `build.gradle.kts` 应用 `maven-publish` 插件并设 `group`/`version`（或靠 JitPack 按 git 推断）；
   - 在仓库根放一个 `jitpack.yml`（可选但推荐，指定 JDK 与构建命令）：
     ```yaml
     jdk:
       - openjdk17
     install:
       - ./gradlew publishToMavenLocal
     ```
4. **发布动作**：在 GitHub 打 tag（如 `v0.1.0`）→ 消费者首次引用时 JitPack 自动 checkout + 构建 + 服务 artifact。无人工确认、无签名要求。
5. **取舍**：JitPack 首次拉取需云端构建（稍慢）；企业防火墙可能拦截；artifact 不强制签名。作为 pre-1.0 分发完全够用。

### 4.2 1.0+ 升级方案：Maven Central（复杂度：高，OSSRH 已退役）
待 API 冻结、发布门禁通过后，再走正式 Maven Central，获得长期可用、签名、企业内可消费的 artifact。
> ⚠️ **OSSRH（`oss.sonatype.org` / `s01.oss.sonatype.org`、Jira 申请 groupId）已于 2025-06-30 永久关停**。必须走 **Central Publisher Portal（`central.sonatype.com`）**。

流程要点：
1. 注册 Central Portal 账号；验证命名空间（GitHub 账号直接用 `io.github.<user>`，自定义域名需 DNS TXT 验证）。
2. 生成并发布 GPG 密钥（**强制**）到 keyserver（`gpg --full-gen-key` → `gpg --send-keys`）。
3. 引入 `maven-publish` + `signing`（或 `com.vanniktech.maven.publish`），产出主 jar + **`-sources.jar`** + **`-javadoc.jar`** + 完整 POM（license/developers/scm）。
4. 获取 Portal User Token，`./gradlew publish` 上传 bundle → Portal UI 状态变 **VALIDATED** 后**点击 Publish** → 2–10 分钟后 search.maven.org 可查。
> 复杂度：约 3–5 天（含等待验证与试错），且已通过 JitPack 阶段验证过构建与发布配置，难度被显著摊薄。

### 4.3 CKV 当前状态与待办
当前 `src/kotlin/build.gradle.kts` **仅有 kotlin jvm 插件 + junit 测试 + jvmToolchain(17)**，无发布配置。
**pre-1.0（JitPack）待办**（轻量，约 0.5 天）：
1. `build.gradle.kts` 应用 `maven-publish`，设 `group = "com.github.mcxiaoke"`、`version`（或交 JitPack 按 tag 推断）。
2. 仓库根放 `jitpack.yml`（见 4.1）。
3. GitHub 打 `v0.1.0` tag，到 jitpack.io 验证 `com.github.mcxiaoke:CarroKV:v0.1.0` 可拉取。
**1.0+（Maven Central）待办**：按 4.2 接 Central Portal（命名空间 + GPG + sources/javadoc + 点发布）。

---

## 5. CKV 五语言分发就绪度与策略

> 关键区分：**pre-1.0 是否真的要"发布"**。Go/Rust/Dart 的包管理器原生支持 git 依赖，pre-1.0 仅需 GitHub tag，无需任何 registry；.NET 的 NuGet 发布足够便宜，直接做；Kotlin 因 Gradle/Maven 不支持 git 源，pre-1.0 用 JitPack（git 桥接）覆盖。

| 语言 | pre-1.0 分发方式 | 正式仓库（1.0+） | 现有配置 | 缺什么（pre-1.0） | 预估工时 | 复杂度 |
| :--- | :--- | :--- | :--- | :--- | :---: | :---: |
| **Go** | GitHub tag（git 原生） | 可保持 git，或 Go proxy 自动代理 | `go.mod` 需确认存在 | 基本无，打 tag 即可 | 0 | 极低 |
| **.NET** | **发布 NuGet** | NuGet | .csproj 有 TargetFrameworks/Version/Description | PackageId/Authors/License/Url/Repository/Tags/Readme 元数据 | 0.5 天 | 低 |
| **Rust** | GitHub tag + git 依赖 | crates.io | Cargo.toml 已较完整 | 仅待 1.0 时确认 `ckv` 名 + 补可选字段 | 0（pre-1.0） | 极低 / 低~中（1.0） |
| **Dart** | GitHub tag + git 依赖 | pub.dev | pubspec 仅基础字段 | 仅待 1.0 时补包内 LICENSE/CHANGELOG/README + dartdoc | 0（pre-1.0） | 极低 / 中（1.0） |
| **Kotlin** | **JitPack（git 桥接）** | Maven Central | 仅构建，无发布配置 | pre-1.0：`maven-publish` + `jitpack.yml`；1.0：命名空间+GPG+Portal | 0.5 天（JitPack） / 3–5 天（MC） | JitPack 低 / MC 高 |

---

## 6. 推荐分发顺序与执行建议（pre-1.0 视角）

核心结论：**pre-1.0 真正要"动手发布"的只有 Kotlin(JitPack) 和 .NET(NuGet)**；Go/Rust/Dart 靠 GitHub tag + git 依赖即可，零发布成本。

1. **.NET（NuGet）**：最便宜，直接补 .csproj 元数据后 `dotnet pack` + `dotnet nuget push`，约 0.5 天。
2. **Kotlin（JitPack）**：在 `build.gradle.kts` 加 `maven-publish` + 仓库根放 `jitpack.yml`，GitHub 打 tag 即分发，约 0.5 天。这是 JVM 世界的"git 等价物"，避开 Maven Central 的前期重投入。
3. **Go / Rust / Dart**：pre-1.0 **不发布到任何 registry**，仅维护好源码与 GitHub tag；消费者用 git 依赖（见附录）。待 1.0 API 冻结后再决定是否上 crates.io / pub.dev（Rust 配置已就绪，Dart 需补包内文档与 pana 分数）。

### 跨语言统一建议
- **统一坐标/版本**：五语言版本号随 CKV 发布节拍同步（当前均为 `0.1.0` pre-release），门禁通过后再统一升 `1.0.0`。
- **统一必须文件**：每个包目录内都放 `LICENSE`、`README.md`、`CHANGELOG.md`（当前仅仓库根有，需下沉到各语言包目录）。
- **Go 的 `go.mod` 待修（真实问题）**：当前 `src/go/go.mod` 的 `module ckv` 是裸名、非 URL 形式，远程 `go get github.com/mcxiaoke/CarroKV/go@v0.1.0` **无法定位**。需改为 `github.com/mcxiaoke/CarroKV/go`（或对应子路径），否则打 tag 后别人拉不到。这是 Go 发布前唯一硬性改动。
- **CI 自动化（仅针对需发布的两种）**：.NET 用 `NUGET_API_KEY`、Kotlin/JitPack 无需密钥（tag 即发）。建议先把这两条链路手动跑通，再把 NuGet token 配进 GitHub Secrets，用 tag 触发发布。
- **令牌最小权限**：NuGet / crates.io(1.0) / Central(1.0) 均支持按包/范围/过期时间限定的 token，CI 用独立 token，泄露可单独吊销。

---

## 附录：各平台分发速查

```bash
# ── 需要发布的两种（pre-1.0） ──

# .NET / NuGet：补完 .csproj 元数据后
dotnet pack -c Release
dotnet nuget push bin/Release/Ckv.0.1.0.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json

# Kotlin / JitPack：在 build.gradle.kts 加 maven-publish + 仓库根放 jitpack.yml 后，
# 只需在 GitHub 打 tag 即可，无需任何命令/密钥（JitPack 云端自动构建）
git tag v0.1.0 && git push origin v0.1.0
# 消费者：repositories { maven { url = uri("https://www.jitpack.io") } }
#         implementation("com.github.mcxiaoke:CarroKV:v0.1.0")

# ── 仅用 git 依赖的三种（pre-1.0 不发布 registry） ──

# Go：打 tag 即发布（import path 即 URL）
git tag v0.1.0 && git push origin v0.1.0
# 消费者：go get github.com/mcxiaoke/CarroKV/go@v0.1.0

# Rust：打 tag，消费者用 git 依赖（或 1.0 后 cargo publish）
git tag v0.1.0 && git push origin v0.1.0
# 消费者 Cargo.toml: ckv = { git = "https://github.com/mcxiaoke/CarroKV", tag = "v0.1.0" }

# Dart：打 tag，消费者用 git 依赖（或 1.0 后 dart pub publish）
git tag v0.1.0 && git push origin v0.1.0
# 消费者 pubspec.yaml: ckv: git: https://github.com/mcxiaoke/CarroKV

# ── 1.0+ 正式发布（届时再做） ──
# Rust   : cargo publish            # 前置 cargo login
# Dart   : dart pub publish --dry-run && dart pub publish
# Kotlin : ./gradlew publish         # 然后到 central.sonatype.com 点 Publish（需 GPG + 命名空间）
```
