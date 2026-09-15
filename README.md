# AppAsset Sentinel (Native) 哨兵

> **面向开发者与 AI 时代的 Windows 智能软件资产全生命周期控制台 · 原生轻量版**  
> 基于 **C# (.NET 9)** + **Win32 原生调用** + **Photino (Edge WebView2)** 架构重构，兼顾极致性能与敏捷交互。

---

## 🌟 为什么需要 AppAsset Sentinel Native？

传统的系统清理与卸载软件（如 GeekUninstaller、CCleaner 等）诞生于 PC 互联网时代，在现代**开发者生态**与**本地大模型（AI Stack）**面前存在严重的认知盲区：
1. **传统工具误杀环境底座**：经常将数月未直接双击运行的 `Python`、`CUDA`、`Git` 判定为“僵尸软件”建议删除，导致本地所有 AI 项目和构建工具瞬间崩溃。
2. **重资产大模型清理隐患**：本地 AI（Ollama、ComfyUI、SD-WebUI）动辄下载数十 GB 的 `GGUF`、`Safetensors` 权重文件，传统清理软件常将其当作未知临时文件清空。
3. **C 盘爆炸与平移刚需**：大模型权重极易撑爆 C 盘，普通剪切移动会破坏路径，而 Windows 符号链接（Symlink）通常需要开发者模式或管理员权限。

**AppAsset Sentinel Native** 专为解决上述痛点而生：
* **🛡️ 依赖护盾（Dependency Shield）**：基于有向无环图（DAG）自动推导依赖拓扑，下级应用存活时强行拦截上游底座卸载。
* **🧠 AI 资产免删与免死金牌**：对驱动、运行时、SDK 实施终身免疫；深度探测大模型权重并予以专项保护。
* **⚡ 杀手级特性：NTFS 目录联接（Junction）原子迁移**：无需开发者模式与管理员提权，一键将数十 GB 模型资产无损平移至大容量盘，源目录无缝替换为透明 Junction，上游软件零感知。
* **🚀 极致原生性能**：全盘 Win32 注册表与指纹扫描仅需 **160 毫秒**；严格 ReparsePoint 探测，彻底杜绝软链接死循环。
* **🧯 工业级安全熔断**：关键系统路径（`C:\Windows`、根盘符、用户目录）强行防御，卸载前自动导出注册表快照与触发系统还原点。

---

## 🏗️ 架构设计与核心模块

```
AppAsset-Sentinel-Native/
├── src/
│   ├── AppAssetSentinel.Core/         # 原生核心库 (扫描、迁移、规则引擎、安全网)
│   │   ├── Models/                    # 领域实体 (SoftwareAsset, DependencyLink, MigrationTask)
│   │   ├── Scanner/                   # Win32RegistryScanner, FastDirectorySizer (抗环递归测算)
│   │   ├── Migration/                 # JunctionEngine (原子平移与回滚), AiAssetDetector
│   │   ├── Shield/                    # DependencyShield, 依赖拓扑图与循环依赖熔断器
│   │   ├── Semantic/                  # SemanticRuleEngine (外置规则动态加载与打标)
│   │   └── Safety/                    # CriticalDirectoryGuard, RegistryBackupService, SystemRestoreService
│   ├── AppAssetSentinel.App/          # Photino.NET 桌面原生启动宿主 (支持 WebUI 互通与单文件打包)
│   └── AppAssetSentinel.Tests/        # xUnit 25 项全方位单元测试与极端对抗性测试套件
├── rules/                             # 外置解耦语义知识库 (YAML / JSON 格式，可热更新)
│   ├── app_semantics.json             # 100+ 常用与 AI 软件语义特征、角色画像、保护等级
│   └── dependency_rules.json          # 上下游拓扑制约规则定义
└── dist/                              # 最终独立免安装发行产物
```

---

## 🧪 极端对抗性测试覆盖 (25 项全面通过)

项目包含 4 大阶段完整测试与极端对抗审查：
* **Phase 1 扫描与测算**：真实注册表 64/32 位双向扫描验证、超深嵌套路径抗死循环测试（在目录内部创建指向父目录的 Junction 闭环，测算器零死循环）。
* **Phase 2 迁移与 AI 识别**：GGUF / Safetensors / Ollama Blobs 真实指纹识别、原子无损平移工作流验证、软链接透明度与只删链接保留数据测试。
* **Phase 3 语义与拓扑护盾**：规则外置加载与实体打标、ComfyUI -> Python/CUDA 依赖拦截、循环依赖（A->B->C->A）自动破环。
* **Phase 4 深度安全与熔断**：`C:\Windows` / `C:\` / 用户主目录删除强行拦截、注册表导出备份生效验证、畸形与穿越路径注入防御。

---

## 🛠️ 构建与运行指南

### 1. 运行测试
```powershell
dotnet test src/AppAssetSentinel.Tests/AppAssetSentinel.Tests.csproj
```

### 2. 命令行极速扫描模式
```powershell
dotnet run --project src/AppAssetSentinel.App/AppAssetSentinel.App.csproj -- --headless-scan
```

### 3. 编译发布单文件绿色发行包
```powershell
dotnet publish src/AppAssetSentinel.App/AppAssetSentinel.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```
生成物位于 `dist/AppAssetSentinel.App.exe`，免安装 Python 与 .NET 运行时，双击即可直接使用。
