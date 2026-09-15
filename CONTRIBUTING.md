# 贡献指南 (Contributing Guide)

感谢关注与参与 **AppAsset Sentinel Native** 开源项目！

本项目致力于构建面向**开发者与本地 AI 时代**的智能资产控制台。我们推崇 **“代码与规则 100% 解耦”** 的现代架构哲学，这意味着您**无需懂得 C# 或编译知识，就能轻松为全网生态贡献力量**。

---

## 🎯 贡献领域

### 1. 扩充外置语义规则库（零代码门槛，最受欢迎！）
项目所有的软件画像和依赖关系均以独立 JSON 格式保存在 `rules/` 目录下：
* **`rules/app_semantics.json`**：收录各类软件的正则表达式、通俗定位、真实核心用途、生态与依赖风险评级；
* **`rules/dependency_rules.json`**：定义上游底层环境（如 Python、CUDA、WSL2、Ollama、Git）与下游应用（如 ComfyUI、Docker Desktop、Cherry Studio）之间的拓扑制约规则。

#### 如何提交新规则：
1. Fork 本仓库；
2. 在 `rules/app_semantics.json` 中追加新条目（参考既有成熟条目格式）；
3. 提交 Pull Request，CI 自动化测试将自动验证 JSON 格式与有效性。

---

## 💻 本地开发与构建

### 开发环境要求
* **Windows 10 / 11 (x64)**
* **.NET 9.0 SDK** 或更高版本
* Visual Studio 2022 / VS Code / Rider

### 常用命令
```powershell
# 1. 还原与构建工程
dotnet build AppAssetSentinelNative.sln

# 2. 运行 28 项全量自动化测试与极端对抗审查
dotnet test src/AppAssetSentinel.Tests/AppAssetSentinel.Tests.csproj

# 3. 本地启动带内嵌 Web 服务的原生控制台
dotnet run --project src/AppAssetSentinel.App/AppAssetSentinel.App.csproj

# 4. 发布完全自包含的单文件免安装绿色版
dotnet publish src/AppAssetSentinel.App/AppAssetSentinel.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```

---

## 🛡️ 代码与安全原则

1. **绝对禁止修改关键系统目录硬熔断**：`CriticalDirectoryGuard` 是系统的物理级防线，任何逻辑不可绕过对 `C:\Windows`、根盘符、用户目录的删除阻断。
2. **任何涉及文件移动的操作必须具备原子性与回滚机制**：迁移失败时必须确保源文件与软链接安全恢复。
3. **保持零人工干预的动态探测能力**：所有底层推导应优先依赖 Win32 原生 API、PE 版本头以及静态二进制依赖嗅探，人工规则仅作精准语义增强。
