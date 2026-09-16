# AppAsset Sentinel Native 哨兵

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-9.0%20Native-512BD4?logo=dotnet&logoColor=white" alt=".NET 9" />
  <img src="https://img.shields.io/badge/UI-Photino%20WebView2-blue" alt="Photino" />
  <img src="https://img.shields.io/badge/Tests-184%20Passed-emerald" alt="Tests" />
  <img src="https://img.shields.io/badge/License-Apache%202.0-orange.svg" alt="License" />
</p>

> **面向开发者与本地 AI 时代的 Windows 智能软件资产全生命周期控制台 · 原生轻量版**  
> 彻底解决传统清理软件在“大模型爆炸、环境依赖冲突、C盘爆满断链”面前的认知盲区。基于 **C# (.NET 9) + Win32 原生调用 + Photino (WebView2)** 重构，集全盘真实遥测、全卷资产仓库、双向防漂移锁死、拓扑防爆护盾与系统快照于一体。

> **⚠️ 当前版本状态：R0 安全观察版（只读）**
>
> 本版本**只开放观察、展示与计划预览**。真实迁移、自动愈合、强制清理、真实卸载、
> 链接解除与还原点创建**均未开放**；界面不会展示这些动作，接口会返回明确的
> `Unsupported` / `Blocked` 与原因，绝不返回成功。
>
> 运行中可随时访问 `http://127.0.0.1:8765/api/policy` 查看当前真实能力姿态。
> 整改依据与未完成项见 [`appasset-audit/RESPONSE.md`](appasset-audit/RESPONSE.md)。
>
> 下文的「双向锁死 / 一键平移 / 自动愈合 / 彻底卸载」描述的是**目标能力与设计意图**，
> 不代表当前可用。开放条件见审计整改的 R1/R2/R3 发布门槛。

---

## 🌟 为什么需要 AppAsset Sentinel Native？

传统的系统清理与卸载工具（诞生于 PC 互联网时代）面对现代**开发者与本地 AI 生态（AI Stack）**存在严重的认知缺陷：

| 痛点场景 | 传统工具的致命缺陷 | AppAsset Sentinel 哨兵的治理方案 |
| :--- | :--- | :--- |
| **底层环境误杀** | 把数月未点击图标的 `Python`、`CUDA`、`Git` 当作“僵尸垃圾”建议清理，直接导致全盘 AI 项目和编译链崩溃。 | **拓扑依赖护盾**：基于 DAG 拓扑制约关系与自动化二进制动态嗅探，存在下游依赖时强行阻断误杀，并提供完整级联失效报告与先决操作指引。 |
| **大模型连坐销毁** | 卸载或清理 Ollama、ComfyUI 时，把耗费数天下载的数十 GB `GGUF`、`Safetensors` 模型文件一并物理删除。 | **骨肉分离免死机制**：程序本体与重型数据彻底解耦；卸载外壳只清理代码与软链接，物理仓库中的模型与工程资产分毫未损。 |
| **C 盘爆炸与数据漂移** | 大模型、Docker 虚拟盘疯狂撑爆 C 盘；普通平移会导致软件断链报错；有的工具虽然搬走了文件，但后续新下的模型又悄悄写回 C 盘。 | **全卷资产仓库与双向锁死**：底层采用 Windows 原生 NTFS Junction 目录联接（上游零感知），配合用户环境变量同步锁定；配备**断链自愈看门狗**，彻底杜绝数据漂移！ |
| **多版本共存误判** | 把开发人员并行安装的 Visual Studio 生成工具 2019 (v142) 和 2022 (v143) 误判为“重复副本冗余”催促删除。 | **SxS (Side-by-Side) 豁免机制**：精准识别底层编译工具链与 SDK，提供正规官方并行技术说明，绝无误判。 |

---

## 🚀 四大核心中枢支柱

### 1. 📋 全盘资产感知与多维真实遥测
* **全域多源纳管**：涵盖 64/32 位注册表、HKCU、以及各存储卷根目录下的便携工具目录（如 `\Tools`、`\Programs`、`\PortableApps`），自动聚合 Python 等开发套件子模块；
* **ActivityWatch 级六层客观遥测**：拒绝失真时间戳！结合正在运行的进程快照、活跃 Windows 服务、开始菜单快捷方式、AppData 深层写入日志与主程序时间，形成真实精准的热度梯度（极度活跃、正常使用、较少使用、真实长期闲置、使用情况未知）。

### 2. 🚀 全卷数字资产仓库与防漂移系统
* **多卷存储感知与无损归仓（Storage Tiering & Relocation）**：
  * 自动动态感知本机挂载的所有物理驱动器卷宗及其剩余空间；
  * 将 C 盘系统盘中的大体量资产（AI大模型、容器虚拟盘、开发编译缓存、通讯与媒体素材）无损平移至用户指定的次级存储卷，彻底解除 C 盘空间告急。
* **双向同步与防漂移锁定（Dual-Lock & Config Sync）**：
  * **首选官方配置与 APP 内部设置双向同步**：自动对接 Ollama 等应用的官方配置与内部数据库设置，避免客户端升级或重启后配置失效；
  * **透明重解析转发**：对无官方外置配置支持的应用，采用 Windows 原生 **NTFS 目录联接（Junction Point）** 进行纳秒级透明重定向，上游软件零感知；
  * **防漂移巡检（Drift Watchdog）**：实时监测联接状态、目标存在性与配置偏离，冲突发生时严格保全两份数据，杜绝静默覆盖。
* **自由自定义目标路径**：完全由用户自由选择目标磁盘卷宗与目标文件夹，支持任意合法路径，绝不绑死任何特定盘符或预设硬件。

### 3. 🛡️ 拓扑依赖护盾与外置规则库解耦
* **零人工规则的自动化二进制动态静态嗅探**：
  * 自动嗅探程序目录下是否引用了 `python*.dll`、`*cuda*.dll`、`msvcp*.dll`、`git.exe`、`ext4.vhdx`，新软件装机自动关联底层依赖；
* **100% 外置解耦（Decoupled Rules）**：
  * 规则完全独立于 C# 二进制主程序，存放于 `rules/app_semantics.json` 和 `rules/dependency_rules.json`；
  * 支持本地任意编辑并提供 **`[🔄 立即热重载外部规则库]`** 按钮，无需重启或重新编译；
* **依赖拓扑图谱置顶呈现**：
  * 自动按关联深度排序，被几十款软件引用的核心（Python、CUDA、Git、WSL）置顶高亮展示。

### 4. 🧯 终极安全护城河：系统快照与物理熔断
* **物理级系统路径硬熔断 (`CriticalDirectoryGuard`)**：
  * 任何代码触碰 `C:\Windows`、`System32`、`SysWOW64`、`C:\` 根盘符时，无条件强制抛出异常中断；
* **双轨快照保险库**：
  * 联动 Windows 原生卷影与系统保护，支持一键触发 Windows 系统还原点（System Restore Checkpoint）；
  * 同时配备**零权限要求的秒级注册表全量快照保险库（.reg 备份）**，确保任何高危卸载操作均有“后悔药”。

---

## 📦 资产归仓支持领域

系统能够自动嗅探与纳管下列高占用数字资产，并提供安全的平移归仓与依赖保护：

| 资产领域 | 典型软件载荷 | 治理方案 | 核心防护价值 |
| :--- | :--- | :--- | :--- |
| **AI 大模型权重** | Ollama、ComfyUI、HuggingFace (`.gguf`, `.safetensors`) | 官方配置与内部设置双向同步，或无损目录联接 | 保障数十 GB 模型不占系统盘，软件卸载/重装资产不丢失 |
| **容器与虚拟磁盘** | Docker Desktop (`ext4.vhdx`)、WSL2、Android AVD 镜像 | 单文件虚拟磁盘专属感知与安全迁移防护 | 避免虚拟机镜像撑爆系统盘，迁移前严格执行一致性前置检查 |
| **社交通讯归档** | 微信文件、QQ 附件、飞书会议缓存 | 大容量通讯资料外置归仓 | 解决多年历史聊天媒体占用数十 GB 系统盘的顽疾 |
| **影视工程草稿** | 剪映专业版工程草稿 (`Projects`)、素材视频与缓存 | 创作草稿工程独立沉淀与目录重解析 | 保证软件崩溃或重装时核心工程草稿分毫未损 |
| **开发构建缓存** | uv、pip、npm 全局构建缓存与 Wheel 包 | 开发工具包缓存重定向至次级盘 | 避免现代开发工具链频繁构建拉爆系统盘 |

---

## 🧪 极端对抗性测试套件 (28 项全量通过)

系统内置了全面的 xUnit 自动化与极端对抗审查用例（`AppAssetSentinel.Tests`）：
* **Phase 1**：真实系统全盘扫描、超深循环软链接检测（目录内部创建指向父目录的死循环 Junction，测算器零死循环）；
* **Phase 2**：NTFS Junction 双向透明读写验证、跨盘原子迁移与校验、数据保全免死；
* **Phase 3**：DAG 拓扑依赖循环破环器、外置解耦规则热解析、级联失效报告生成；
* **Phase 4**：关键系统路径硬熔断拦截测试、注册表备份导出与还原点接口容错；
* **Vault & Anti-Drift**：硬件介质感知与四领域路由、模拟软件破坏升级覆盖软链接后的漂移自动报警与 Auto-Heal 增量自愈。

---

## 🛠️ 快速上手与构建

### 1. 开箱即用（绿色单文件版）
从 [Releases 页面](https://github.com/cloudenshine/AppAsset-Sentinel-Native/releases/tag/v1.0.0) 下载最新的 `AppAssetSentinel.App.exe`，免安装 Python 与 .NET 运行时，双击即可直接拉起控制台。

### 2. 源码构建与运行
```powershell
# 克隆仓库
git clone https://github.com/cloudenshine/AppAsset-Sentinel-Native.git
cd AppAsset-Sentinel-Native

# 运行自动化测试套件
dotnet test src/AppAssetSentinel.Tests/AppAssetSentinel.Tests.csproj

# 本地以开发模式运行
dotnet run --project src/AppAssetSentinel.App/AppAssetSentinel.App.csproj

# 编译发布自包含独立单文件绿色程序
dotnet publish src/AppAssetSentinel.App/AppAssetSentinel.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```

---

## 🤝 参与贡献

欢迎查阅 [CONTRIBUTING.md](CONTRIBUTING.md) 了解如何为规则库贡献新软件画像或依赖制约条目。无需编写任何 C# 代码，提交 Pull Request 即可为全网开发者规避环境崩溃！

## 📄 开源许可证

本项目基于 [Apache License 2.0](LICENSE) 开源协议发布。
