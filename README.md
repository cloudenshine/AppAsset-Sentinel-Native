# AppAsset Sentinel Native 哨兵

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-9.0%20Native-512BD4?logo=dotnet&logoColor=white" alt=".NET 9" />
  <img src="https://img.shields.io/badge/UI-Photino%20WebView2-blue" alt="Photino" />
  <img src="https://img.shields.io/badge/Tests-102%20Passed-emerald" alt="Tests" />
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
* **全域多源纳管**：涵盖 64/32 位注册表、HKCU、以及便携独立工具路径（`D:\AIStack\tools`、`D:\Tools`、`D:\Programs`），自动聚合 Python 官方开发套件子模块；
* **ActivityWatch 级六层客观遥测**：拒绝失真时间戳！结合正在运行的进程快照、活跃 Windows 服务、开始菜单快捷方式、AppData 深层写入日志与主程序时间，形成真实精准的热度梯度（极度活跃、正常使用、较少使用、真实僵尸）。

### 2. 🚀 全卷数字资产仓库与双向锁死防漂移（杀手级特性）
* **硬件介质感知与智能分层（Storage Tiering）**：
  * 底层实测识别物理磁盘介质类型：**NVMe SSD 高速固态**（读写 5000+ MB/s） vs **SATA HDD 机械硬盘**（顺序读写 ~150 MB/s）；
  * **高频高 IOPS 资产**（AI大模型、Docker 虚拟盘、Node/uv 编译缓存）：系统强制优先推荐部署至 NVMe 固态仓库（如 `D:\AIStack_Vault`），保障模型秒级入显存，杜绝卡顿；
  * **大容量归档冷数据**（微信历史聊天附件、QQ群文件、长视频工程素材）：优先引导沉淀至大容量机械仓库（如 `E: DOCUMENTS`、`G: VIDIOS`），为高速固态节省宝贵空间。
* **双向锁死（Dual-Lock）防漂移**：
  * **第一重锁（内核文件系统级）**：建立 Windows 原生 **NTFS 目录联接（Junction Point）**，零延迟纳秒级透明转发，上游工具（Ollama/Docker/微信）零感知；
  * **第二重锁（环境变量级）**：同步锁死 `OLLAMA_MODELS`、`HF_HOME`、`TORCH_HOME`、`UV_CACHE_DIR` 等系统环境；
* **断链与漂移实时巡检看门狗（Drift Watchdog）**：
  * 软件静默自动更新覆盖软链接时，顶部立即红标警报，支持 **`[⚡ 一键自动愈合 (Auto-Heal)]`** 增量合并新数据并重新加固链接；
* **自由自定义**：提供盘符一键秒切与任意路径完全自定义编辑。

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
  * 任何代码触碰 `C:\Windows`、`System32`、`SysWOW64`、`C:\` 根盘符、`Users\Admin` 时，无条件强制抛出异常中断；
* **双轨快照保险库**：
  * 联动 Windows 原生卷影与系统保护，支持一键触发 Windows 系统还原点（System Restore Checkpoint）；
  * 同时配备**零权限要求的秒级注册表全量快照保险库（.reg 备份）**，确保任何高危卸载操作均有“后悔药”。

---

## 📊 硬件介质分层存储规划矩阵

根据本地实际硬件介质，系统智能执行下列分流策略：

| 资产领域 | 典型软件载荷 | 介质要求 | 推荐物理卷宗去向 | 达成效果 |
| :--- | :--- | :--- | :--- | :--- |
| **AI 大模型权重** | Ollama、ComfyUI、HuggingFace (`.gguf`, `.safetensors`) | 极高吞吐 / 频繁加载 | **`D:` (NVMe SSD 高速固态仓)** | 3秒载入显存；卸载软件权重终身免死 |
| **容器与虚拟磁盘** | Docker Desktop (`ext4.vhdx`)、Android AVD 虚拟机镜像 | 高随机 4K IOPS | **`D:` (NVMe SSD 高速固态仓)** | 容器数据库秒开，避免机械盘卡死系统 |
| **社交通讯归档** | 微信历史视频与聊天库、QQ群文件、飞书会议缓存 | 大容量 / 冷数据存储 | **`E: DOCUMENTS` (SATA 机械文档仓)** | C盘0占用；重装系统一键还原多年记录 |
| **影视工程草稿** | 剪映专业版未导出草稿 (`Projects`)、素材视频 | 大容量 / 创作保全 | **`G: VIDIOS` (SATA 机械媒体仓)** | 创作工程与软件分离，软件崩溃草稿绝不丢失 |

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
