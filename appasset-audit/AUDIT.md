# AppAsset Sentinel Native：代码体检与价值提升方案

审查日期：2026-09-15  
代码基线：`master@f7c9dbf7a29b55cf7fa3d832bf3833ac67ee6e75`  
范围：关键执行链路源码、全部五个测试类、CI 运行记录与日志、发布元数据、部分前端关键交互、官方产品与平台文档。未修改远程仓库。

## 结论

建议继续投入，但收缩为“Windows 本地 AI 资产证据与安全保全控制台”，不继续扩张为万能清理与卸载工具。保留 C# / Photino / Core 分层和扫描、规则的已有基础，重新建立涉及写入与删除的执行协议。

当前定位是：有真实功能和通过构建的原型，尚不足以承接主力机器的重要数据自动迁移、自愈与强制清理。最大问题不是缺少功能，而是证据强度、执行状态、恢复能力与宣传承诺不匹配。

## 验证边界

- 通过 GitHub 连接器直接读取固定提交的代码，而非仅阅读 README。
- 读取 GitHub Actions run `34944387375`、job `104300225208` 的日志：构建成功，13 个 CA1416 平台适用性警告、0 错误；28 个测试通过。运行环境为 Windows Server 2025，不等于 Windows 10/11 桌面验收。
- 当前检查环境为 Linux，没有 .NET SDK，也没有访问用户 Windows 主机。未重新执行 C# 测试、真实安装/卸载、NTFS 跨卷迁移、UAC 或发布版 GUI 验收。
- 本包 `logic_probes.py` 在隔离临时样例中复现六个逻辑反例，结果见 JSON。它不是原生应用测试，不构成 NTFS、注册表、浏览器攻击或发布包验证。
- 发布元数据显示 v1.0.0 只有一个 101,278,453 字节 EXE，附 GitHub SHA-256 digest。未下载检查 EXE、数字签名或二进制组成，不能直接断言发布包必然无法运行。
- 未完成全部第三方依赖的漏洞扫描、整机动态安全测试或每个文件的逐行审计。本报告不是安全认证。

## 应立即采取的限制

在整改验收前，生产版仅开放观察、展示与计划预览。默认禁用真实迁移、自动合并自愈及强制清理；未实现的真实卸载明确返回不支持，不得返回成功。不要按现有 README 在主力用户账户直接执行完整测试套件：其中有写入真实用户环境变量和生产注册记录的路径。

## 逐项发现

严重度定义：P0 阻止开放相关写操作或发布安全承诺；P1 必须在生产版验收前完成；P2 提升性能、可维护性与体验。它们是本次工程优先级，不是 CVSS 分数。

| ID | 级别 | 实际发现 | 主要证据位置 | 修复方向 |
|---|---|---|---|---|
| A01 | P0 | `ExecutePlan` 的 live 分支直接返回 executed=true 和卸载成功，没有调用卸载程序或清理动作；audit_id 仅在此处生成随机值。 | `Core/Uninstaller/UninstallerEngine.cs::ExecutePlan` | 未实现时返回 NotSupported；实现后用真实进程结果与后置检查决定结果，审计持久化。 |
| A02 | P0 | `ForceClean` 直接递归删除安装目录、删除注册项，未执行依赖护盾、数据保留或备份门控；异常被吞掉。API 仍移除缓存记录并返回成功。 | 同上 `ForceClean`；`App/Program.cs` force-clean endpoint | 禁止作为默认补救；所有写入口进入同一计划/政策/执行门控。 |
| A03 | P0 | 迁移允许既有目标目录；`FileMode.Create` 会覆盖目标同名文件，没有目标侧恢复协议。 | `Core/Migration/JunctionEngine.cs::MigrateDirectoryAsync` / `CopyDirectoryRecursivelyAsync` | 新建、由任务独占的 staging 目录；检测目标碰撞，不覆盖不属于本任务的数据。 |
| A04 | P0 | 迁移仅检查目标总大小不小于扫描源总大小，不能证明逐文件一致；未协调应用写入或检查源版本稳定性。 | 同 A03 | 文件清单、流式哈希、源稳定性与应用静默、目录和文件类型校验。 |
| A05 | P0 | 未拒绝源=目标、目标在源内部、源在目标内部、父路径重解析造成的别名与路径越界。 | 同 A03；`CriticalDirectoryGuard.cs` | 在执行前及关键切换点验证真实文件系统身份与路径边界；拒绝不支持情形。 |
| A06 | P0 | 自愈按时间戳择一覆盖，随后删除原漂移目录。相同时间戳但不同内容会丢失漂移侧；漂移侧较新时会覆盖仓库侧。 | `Core/Migration/DriftWatchdog.cs::AutoHealDrift` | 冲突保全、两版本隔离、哈希证据；默认只诊断，不覆盖或删除冲突数据。 |
| A07 | P0 | 还原点描述从请求直接拼入 PowerShell `-Command` 脚本字符串，存在静态命令注入风险。 | `App/Program.cs` restore endpoint；`Core/Safety/SystemRestoreService.cs` | 不拼接不可信文本为代码；使用固定代码与独立数据通道或原生调用。 |
| A08 | P0 | 本地固定端口 API 未见身份认证、Origin/Host 验证及服务端不可篡改计划绑定；UI 从远程 CDN 执行脚本，并将软件元数据插入 HTML/内联事件。 | `App/Program.cs`；`App/wwwroot/index.html` | 本地静态资源、严格内容策略、安全 DOM、会话认证与写授权。未实测恶意网页可达性，不声称远程攻击已成功。 |
| A09 | P0 | 双锁测试调用生产存储和用户环境变量写入；临时目录清理后未恢复 OLLAMA_MODELS 或注册记录。 | `Tests/TestAssetVaultAndAntiDrift.cs`；`AssetVaultEngine.cs` | 注入测试数据根和环境变量存储；默认测试无真实用户持久副作用；原生集成测试进隔离账户/VM。 |
| A10 | P1 | 迁移请求的完整目标路径最后一级被丢弃，实际使用源目录名。 | `AssetVaultEngine.cs::RelocateAndDualLockAsync` | 明确 TargetPath 契约，后端执行路径必须等于用户确认路径；禁止静默改写。 |
| A11 | P1 | 成功迁移仍保留源盘 `.sentinel_backup_*` 整份目录，因此此时尚未释放相应源盘空间；没有显式提交/保留期/备份处理阶段。 | `JunctionEngine.cs` | 把“切换成功”与“空间已回收”区分，新增受控 Commit/备份保留策略，不能直接删备份掩盖问题。 |
| A12 | P1 | 名称为 rollback 的 API 只删除 Junction，未恢复源路径数据、环境变量或注册状态；UI 实际称其“解除软链接”。 | `App/Program.cs` migration/rollback；`index.html::rollbackJunction` | 将 unlink 与真正 recover 分为不同操作；真实恢复须处理切换后产生的新数据。 |
| A13 | P1 | 无历史使用证据时硬填 180 天前，并将普通应用归为僵尸；快捷方式/EXE 的写入时间也被当成最后使用时间。 | `Core/Scanner/TelemetryEngine.cs` | Unknown 与未观察到使用分离，保留观测窗口、证据类型、时间、置信度；不得据此自动删除。 |
| A14 | P1 | 硬件探测失败会把系统盘或 D 盘标为 NVMe SSD；其他盘默认机械 SATA；推荐文本承诺秒级模型加载。 | `Core/Migration/VolumeManager.cs` | Unknown，不猜事实；保留卷—分区—物理磁盘关系；用户偏好与实测事实分开。 |
| A15 | P1 | 文件名静态抽样发现 python/cuda 等就关联第一个同名系统运行时，不能区分内嵌与外部、版本与路径。 | `Core/Shield/DependencyShield.cs::BuildGraph` | 应用实例及路径级证据；区分 confirmed/inferred/unknown、内嵌/外部/可选依赖。 |
| A16 | P1 | 为生成 DAG 直接删除形成循环的边；测试反而要求删掉 C→A。 | `DependencyShield.cs::BreakCycles`；`Tests/TestPhase3_SemanticAndShield.cs` | 原始证据图保留全部边；用强连通分量处理展示与计划，不改变事实。 |
| A17 | P1 | 规则热加载先清空旧规则，再容错吞掉错误；发现规则目录依靠逐级上溯父目录，可能受运行位置影响。 | `Core/Semantic/SemanticRuleEngine.cs`；`DependencyShield.cs` | 明确规则来源、schema/version/hash；校验新快照后替换，失败保持已知良好版本；正则超时。 |
| A18 | P1 | vault_registry.json 原地写且吞异常；解析错误当成空列表。环境变量同步或登记失败也可能返回双锁成功。 | `AssetVaultEngine.cs` | 原子持久化、互斥、显式失败、损坏检测；任务日志与状态一致性。 |
| A19 | P1 | 环境变量按名字/路径子串选择，未保存旧值或验证消费端；watchdog 只检查路径和链接，不检查环境变量。 | `AssetVaultEngine.cs`；`DriftWatchdog.cs` | 由应用适配器拥有配置语义，保存并可恢复旧值，重启后验证实际写入位置。 |
| A20 | P1 | HF 扫描的是 `...huggingface/hub`，写入的是 `HF_HOME`，而不是对应的 hub 级配置；可能让默认 cache 指向新目录中的额外 hub 层。 | `MultiDomainAssetScanner.cs`；`AssetVaultEngine.cs`；HF env docs | 尊重 HF_HOME 与 HF_HUB_CACHE 不同层级；以有效配置优先，保留缓存引用结构。 |
| A21 | P1 | Docker 候选是 ext4.vhdx 文件，但迁移后端要求 Directory.Exists；固定路径也不能覆盖所有新旧布局。 | `MultiDomainAssetScanner.cs`；`JunctionEngine.cs` | 未支持则不显示可执行迁移；按 Docker 版本、有效配置及官方方式实现专用适配器。 |
| A22 | P1 | 目录大小计算跳过 reparse、超深及失败部分，却只返回单个 long；对外称“真实物理空间”不准确。 | `Core/Scanner/FastDirectorySizer.cs`；`Program.cs` overview | 返回统计完整性、逻辑/分配/共享/未知字节；保护扫描不跨链接的优点，但不能误当完整资产清单。 |
| A23 | P1 | 注册表 32 位路径被拼为 LocalMachine\WOW6432Node\Software\...，不能代替真实 Hive+RegistryView 身份；按名称+版本去重可能合并不同架构/安装实例。 | `Core/Scanner/Win32RegistryScanner.cs::ScanRegistry` | 注册项保存类型化 Hive/View/SubKey；身份使用架构、作用域和实例路径，避免字符串逆解析。 |
| A24 | P1 | 安全状态接口硬编码可用；还原点失败时仍宣称注册表快照成功，未核对导出返回值。仅导出 HKCU\Software 不是全系统快照。 | `Program.cs` safety endpoints；`SystemRestoreService.cs` | 分项真实结果；只在本操作所需恢复材料实际验证后允许执行。 |
| A25 | P1 | 普通 Windows 路径直接插入 inline onclick 单引号 JS 字符串会丢反斜杠或改变含义；元数据还存在 HTML/脚本上下文注入入口。 | `index.html::renderVaultCandidates`、应用卡片渲染 | textContent、DOM 属性和闭包事件，不拼接路径到脚本；中文/空格/单引号等回归。 |
| A26 | P1 | 发布仅一个 EXE，而程序在文件系统寻找 wwwroot/rules；当前 CI 不执行 publish 后独立启动。发布完整性不能由源码目录运行推断。 | `App.csproj`；`Program.cs::GetWebRootPath`；`SemanticRuleEngine.cs`；CI/release metadata | 首先提供完整自包含 ZIP，离开源码目录、断网、普通账户启动验收；不执着单文件。 |
| A27 | P1 | 循环保护测试仅在成功创建 Junction 后断言；创建失败可以无断言通过。所谓跨盘迁移测试使用同一个临时根的两子目录。 | `Tests/TestPhase1_ScannerAndSizer.cs`；`TestPhase2_JunctionAndAiAssets.cs` | 显式失败或有原因的 Skip；真实跨卷在两个隔离测试卷执行，结果单列。 |
| A28 | P2 | 全扫描及体积计算在窗口启动前运行；固定 800ms 等待服务器；静态可变共享状态；大型页面卡片一次渲染。 | `App/Program.cs`；`index.html` | 先显示缓存并异步扫描；真实 readiness；不可变快照/串行任务；再按实际规模优化。 |

上述 A23 的 Hive 判断不宜误报：当前扫描器写的是枚举名 LocalMachine，而不是 HKEY_LOCAL_MACHINE，因此 `Contains("LOCALMACHINE")` 在该路径本身可匹配；真正问题是 RegistryView 及子路径结构，不是必然选错 Hive。

## 六个隔离逻辑反例

详见 `logic_probe_results.json`。

1. 源 `C:\Users\Shine\.ollama\models`，请求 `D:\AIStack_Vault\models\ollama`，按当前函数组合得到 `D:\AIStack_Vault\models\models`。
2. 同名不同内容、时间戳相同：自愈条件不复制漂移版本，却删除漂移目录。
3. 漂移版本时间更新：仓库已有不同版本被直接覆盖。
4. `AAAA` 与 `BBBB` 同样四字节，总大小判断通过但 SHA-256 不同。
5. `C:\Tools\ComfyUI\models` 插入 inline handler 后，JavaScript 得到 `C:ToolsComfyUImodels`。
6. A→B→C→A 输入经过删环逻辑，C→A 关系丢失。

## 保留、重做、延期

**保留：** C# 与 Photino 基础；App/Core/Tests 的项目分离；注册表与便携软件扫描；重解析点安全遍历的意识；外置规则思想；原目录改名留备份的恢复出发点；已有可运行测试与 CI。

**重做：** 所有写操作的状态机和统一安全门控；资产与软件实例的身份/引用模型；未知状态；规则与任务持久化；前端传参与真结果展示；无副作用测试及发布验收。

**延期：** 万能卸载、驱动/SDK 强制清除、活跃数据库与聊天记录通用迁移、自动内容去重、任意目录自愈、跨平台重写、云端控制、复杂 Agent 团队、付费规则商城。

## 最合适的价值定位

### 三条路线比较

| 路线 | 价值判断 | 决策 |
|---|---|---|
| 万能清理/批量卸载 | BCU 已有免费开源、自动卸载、残留检测和原卸载器调用。重做宽泛功能很难形成优势。 | 不作为主线。 |
| 模型路径搬家/Junction 包装器 | Ollama 官方已经支持 OLLAMA_MODELS，且说明改过模型位置后卸载器不会删模型。路径修改本身不足以形成差异化。 | 作为能力，不作为产品全部价值。 |
| AI 资产证据与安全保全 | 回答资产是什么、在哪、谁依赖、能否动、失败如何恢复、之后是否漂移。可同时服务用户与 Agent。 | 推荐主线；仍需真实用户验证价值，不宣称市场已被证明。 |

推荐对用户表达为：“整理本地 AI 环境时，不丢模型、不误断项目；每次改动有证据，失败有恢复路径。”

### 第一条生产闭环：Ollama 模型库

发现实际 Ollama 实例、作用账户、版本、有效配置与模型目录；盘点 manifests/blobs 和可访问模型；在明确的维护窗口停止相关写入；为用户指定的完整目标路径生成计划；复制到任务专属 staging；校验清单、哈希与模型引用；优先修改官方配置，Junction 仅作经过评估的兼容措施；让真实消费进程重新读取配置；验证原有模型可列出和推理、新的受控写入落于目标位置；再提交并依据备份策略回收源空间。

取消、断电、断盘、重启、目标冲突等情况下，只允许处于“原数据仍安全且可恢复”的状态，不允许为了显示成功提前删除备份。恢复也不能简单回到旧备份而丢掉切换后的新增数据。

第二个适配器是 Hugging Face：区分 HF_HOME/HF_HUB_CACHE，保留 refs/blobs/snapshots 及有/无符号链接两种实际布局。第三步才考虑 ComfyUI 等模型引用配置。Docker/WSL 与聊天数据库先只读发现，待专用停机/备份/恢复验收后再开放写操作。

## 最小可维护设计

### 五种业务对象

SoftwareInstance（版本、架构、账户、可执行文件身份）；Asset（用途、不可替代性、内容身份、路径）；Reference（谁以何配置/链接/路径引用谁）；Evidence（来源、时间、置信度与失效条件）；Operation（计划、授权、日志、恢复材料、真实结果）。

未识别不能等于安全，未观察到依赖不能等于无依赖，未观察到使用不能等于废弃，目录大小不能等于可回收空间。

### 统一执行路径

`Observe → Evidence → Plan → Preflight → Copy → Verify → Switch → HealthCheck → Commit`

异常进入 `Blocked / FailedRecoverable / NeedsAttention / Recovered` 等真实状态。对外使用 task_id/plan_id 查询，不能由浏览器传回任意完整计划并直接执行。

计划绑定资产与卷身份、源/目标路径、配置旧值、规则/策略版本、失效条件和授权范围；执行前重验，重叠资源互斥，重试幂等。所有危险操作通过同一个门控，包括旧兼容接口。

### 持久化与工程取舍

保留现有技术栈，不重写 Rust/Tauri，不引入微服务或专用图数据库。观察版可先修复 JSON 原子读写和损坏处理；开始持久化执行任务时，单机 SQLite 是合理候选，用于操作事务和恢复索引。但数据库事务不能包住文件系统动作：仍需写前日志、幂等状态机与补偿恢复。

轻量适配器只提供 discover / effective-config / quiesce / validate / switch / health-check / recover 等明确能力，先静态注册，不先做插件市场。没有经过测试的写能力必须显式标记 unsupported。

只读 CLI 可提前开放；写 CLI/API/MCP 在安全核心验收后复用同一执行入口，Agent 没有绕过策略和授权的特权。与 Windows Steward 之类其他工具将来集成时，应保持一个资源一个写操作所有者，不能假定两仓库已有共享后端或两个后台可同时修复。

.NET 9 当前尚受支持，但官方 EOL 为 2026-11-10；建议验证 Photino 与打包兼容后迁移 .NET 10 LTS（EOL 2028-11-14），不要为了所谓原生轻量贸然引入 NativeAOT。自包含分发还需维护所携带的运行时补丁。

## 交付顺序与验收门槛

### R0：安全观察版

关闭真实危险操作，未实现返回不支持；移除虚假状态/伪造历史；封闭脚本注入和不可信 HTML；测试隔离；对照当前 release 添加清晰限制说明。验收：任何未开放写请求不得修改用户数据；模拟不修改系统；错误不返回成功；测试运行前后用户设置不变。

### R1：Ollama 可信迁移版

以新执行协议完成一个适配器，不边做边加领域。验收：精确目标、源/目标边界、同名冲突保全、逐文件验证、所有关键崩溃恢复点、取消、重复请求、真应用重启及可用性、确认后的实际空间回收。先隔离账户/VM/两个测试卷，不把用户主力数据当故障注入样例。

### R2：资产关系版

引入基线与差异、软件实例与共享资产引用、环境配置漂移、HF 专用支持、可信规则更新；漂移先诊断，再生成独立修复计划。验收：内嵌 Python 不被误判为指定系统 Python 的依赖，循环不丢边，未知证据保持未知，规则损坏不能失去已知保护。

### R3：发布与 Agent 接入版

可复现完整发布、离开源码目录启动、离线资源、标准账户与权限不足用例、WebView2 检测、端口冲突、中文/空格/单引号路径、API/CLI 同政策与同后置验证、持久审计导出及隐私脱敏。分发必须绑定构建提交、测试证据与完整文件清单。

## 不以测试数量替代验收

特别需要覆盖：源目标相同和包含、路径别名和重解析、目标已有同名不同内容、同时间戳不同内容、复制中写入、文件锁、权限变化、磁盘满/断开、复制后切换前进程终止、切换后登记失败、每个状态重启恢复、并发及重放、测试污染、未知硬件/使用/依赖、HF 链接与非链接布局、32/64 注册表实例区分、恶意元数据安全渲染、无效/过期计划拒绝、只下载发布物的启动与应用健康检查。

单元测试验证事实和政策；契约测试验证 API；隔离 Windows 集成验证 NTFS/注册表/进程；发布 E2E 验证真实应用。仅在各层相应通过后才能开放对应能力。未尝试的测试明确 NotRun/Unsupported，不算通过。

## 参考来源

仓库：`https://github.com/cloudenshine/AppAsset-Sentinel-Native`  
所有源码证据固定于前述完整提交；报告中的 `Core/`、`App/`、`Tests/` 分别是 `src/AppAssetSentinel.Core/`、`src/AppAssetSentinel.App/`、`src/AppAssetSentinel.Tests/` 的缩写。

- 构建记录：`https://github.com/cloudenshine/AppAsset-Sentinel-Native/actions/runs/34944387375`
- 发布元数据：`https://api.github.com/repos/cloudenshine/AppAsset-Sentinel-Native/releases/tags/v1.0.0`
- .NET 支持政策：`https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core`
- .NET 单文件分发：`https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview`
- Ollama Windows：`https://docs.ollama.com/windows`
- HF 缓存：`https://huggingface.co/docs/huggingface_hub/en/guides/manage-cache`
- HF 环境变量：`https://huggingface.co/docs/huggingface_hub/en/package_reference/environment_variables`
- Docker 备份恢复：`https://docs.docker.com/desktop/settings-and-maintenance/backup-and-restore/`
- BCU 官方说明：`https://www.bcuninstaller.com/`

这些官方来源用于校验平台行为与路线取舍，不代表对本项目实现进行了认证。
