# AppAsset Sentinel Native — 审计整改响应（R0 安全观察版）

基线：`master@356073a`（审计基线 `f7c9dbf` 之上的 i18n 提交）
本轮产出：`R0 安全观察版`。写入能力按审计门槛关闭，只读观察与计划预览开放。
本文件记录**已实施**的整改、**未实施**的部分，以及本轮在真实主机上发现并修复的损害。

---

## 一、结论摘要

| 发布门槛 | 状态 | 说明 |
|---|---|---|
| **R0 安全观察版** | ✅ 已完成 | W01、W02、W03 全部达成验收；W04 关键项达成（A13/A14/A16/A17/A22）。 |
| **R1 可信迁移** | ✅ 机制已在真实应用上验收 | W06/W07/W08 完成（含受授权的 Commit 与 Recover）；跨物理盘迁移已在真实两个卷上验收；**真实 Ollama 实例已在重定向位置读取并列出全部 4 个真实模型**（见下节）。R1 档位仍需显式 `--profile=r1`。 |
| **R2 资产关系** | ✅ 计划层完成 | W09 冲突保全漂移修复、W10 HF/只读适配器均已完成。 |
| **R3 发布与 Agent 接入** | ✅ 完成 | W11 取消、W12 可复现 ZIP、四入口统一门控（含 MCP）均已完成；已发布 v1.0.0-r0。旧注：**W11 UI 异步化已完成**。CLI/MCP 接口面未实现。 |
| R2 资产关系版 | ⏳ 未开始 | W09 漂移诊断已可用（只诊断），W10 第二适配器未开始。 |
| R3 发布与 Agent 接入版 | ⏳ 未开始 | 尚未提供完整自包含 ZIP 与空目录离线启动验收。 |

**未把任何未验证的能力标记为已通过。** 下文逐项对应审计发现。

---

## 二、本轮真实主机损害修复（A09 的实际后果）

审计预测「测试会写入真实用户环境」，本轮在该主机上确认损害真实发生：

- `OLLAMA_MODELS`（用户级）被旧测试套件覆写为
  `D:\DevCache\Temp\SentinelVaultTest_110c867b…\VaultParent\Simulated_C_Models`，
  该临时目录已随测试清理消失 → **Ollama 实际处于断链状态**。
- `%LOCALAPPDATA%\AppAssetSentinel\vault_registry.json` 被写入 10 条测试夹具登记。

已执行的修复（证据保留在同目录 `*.polluted-*.json` / `.txt`）：

1. `OLLAMA_MODELS` 恢复为 `D:\AIStack\models\ollama`（该目录含 39.4 GB 真实权重）。
2. 生产登记文件清除全部 `SentinelVaultTest_*` 条目，现为 `[]`。
3. 复验：`ollama list` 正常列出 4 个模型，推理路径恢复。

> 这是本轮最有价值的发现：不是文档问题，而是审计在真实机器上阻止了持续的数据损害。

---

## 三、逐工作包实施情况

### W01 / P0 关闭不可信写操作 —— ✅ 完成

新增单一能力门控 `CapabilityPolicy`（`Core/Policy/`），所有写入口共用：

| 能力 | R0 状态 | 对应发现 |
|---|---|---|
| ObserveReadOnly / PlanPreview / DriftDiagnose | Enabled / DryRunOnly | — |
| RegistryBackup / RulesReload | Enabled | — |
| UninstallLive | **Unsupported** | A01 |
| ForceClean | **Unsupported** | A02 |
| VaultRelocate | **Blocked** | A03/A04/A05/A10 |
| JunctionUnlink | **Blocked** | A12 |
| DriftAutoHeal | **Blocked** | A06 |
| RestorePointCreate | **Blocked** | A07/A24 |

- `ExecutePlan` 的 live 分支不再返回成功，改为 `Unsupported`；simulation 返回独立状态 `Simulated`，`did_mutate=false`，不再生成冒充真实运行的 `audit_id`。
- `ForceClean` 的递归删除实现已移除，接口恒返回 `Unsupported` 与原因。
- 所有接口统一返回 `OperationOutcome`（`status / capability / did_mutate / code / message / evidence / recovery`）。
- `/api/policy` 公开真实能力姿态，UI 只展示真正可用的动作。

**验收证据（真机实测）**

```
POST /api/uninstall/force-clean
→ {"status":"Unsupported","did_mutate":false,"code":"not_supported",
   "message":"A02：递归删除未经过依赖护盾、数据保留与备份门控，已禁用。"}

POST /api/vault/relocate（真实 Ollama 路径）
→ {"status":"Blocked","did_mutate":false,"code":"blocked_by_policy",
   "message":"A03/A04/A05/A10：…（未做任何修改。）"}
```

### W02 / P0 先隔离测试再运行测试 —— ✅ 完成

- 新增 `IEnvironmentStore`（`SystemEnvironmentStore` / `InMemoryEnvironmentStore`），测试不再触碰真实用户环境变量。
- `VaultRegistry.Load/Save` 支持显式路径；**所有测试只写临时路径**；写入改为「临时文件 + 原子替换」，失败抛出而非吞掉（A18）。
- 破坏性单元测试改为纯函数式：`DriftDiagnostics.Audit(policy, registrations)` 不落盘。
- A27：新增 `TestEnvironment.RequireJunctionSupport()`。xUnit v2 无动态 Skip，能力不足时**显式失败并说明原因**，不再静默通过；循环保护测试改为 `finally` 清理并断言联接创建成功。

**验收证据（测试运行前后对比）**

```
BEFORE: OLLAMA_MODELS(User) = 'D:\DevCache\Temp\SentinelVaultTest_110c867b…'
        vault_registry.json hash = AE148CF6A3188BC8…
AFTER:  OLLAMA_MODELS(User) = 'D:\DevCache\Temp\SentinelVaultTest_110c867b…'
        vault_registry.json hash = AE148CF6A3188BC8…
UNCHANGED env: True    UNCHANGED registry: True
```

### W03 / P0 修复输入与授权边界 —— ✅ 完成

- **A25（路径损坏与注入）**：彻底移除把路径插入 inline handler 的写法。卡片改为
  `data-src / data-dst / data-name / data-domain / data-reason` 属性 + 事件委托。
  审计探针的 `C:\Tools\ComfyUI\models` 现在按字节原样往返。
- **A25（HTML 注入）**：新增 `esc()/escAttr()`，所有服务端来源文本（路径、名称、发布商、描述、标签、残留原因）在插入 HTML 前转义；`innerText` 改为 `textContent`。
- **A07（脚本注入）**：`SystemRestoreService` 不再拼接 PowerShell 字符串。改为固定脚本 + 通过环境变量传参，并对描述做字符白名单与长度上限。
- **A08（本地 API 无鉴权）**：新增 `LocalRequestGuard`（`Core/Security/`）+ 中间件：
  - 前端资源本地化（Tailwind 已下载至 `wwwroot/vendor/`，不再执行远程 CDN 脚本）；
  - Host 白名单阻断 DNS rebinding；
  - Origin 白名单阻断跨站请求；
  - 每进程随机会话令牌，写接口必须携带 `X-Sentinel-Session`。
- **W03 计划身份**：服务端用 `_planStore` 持有计划，客户端只传 `plan_id`；未知/伪造/缺失计划一律拒绝，绝不执行客户端回传的完整计划。

**验收证据（真机实测）**

```
跨站 Origin  → HTTP 403 origin_not_allowed
无令牌 POST  → HTTP 401 session_required
有效令牌 POST→ HTTP 200 {"status":"observed","did_mutate":false}
伪造 plan_id → {"status":"Failed","code":"unknown_plan"}
```

### W04 / P1 建立真实未知状态与配置来源 —— ✅ 关键项完成

- **A13**：删除「无证据即填 180 天前并判为僵尸」的伪造路径。现在无证据 → `heat_level="unknown"`、`days_since_last_use=null`、`telemetry_source="no_evidence_observed"`、`usage_confidence="unknown"`。
  另外区分证据强度：仅凭文件时间戳（`executable_file_stamp` / `install_date_registration`）判为 `inferred`，**不再单独据此判为可清理僵尸**。
  真机复验：`inferred_legacy` 计数 **0**；僵尸计数由 8 降为 7（其中 1 项仅有弱证据，降级为 cooling）。
- **A14**：`VolumeManager` 探测失败不再回退成「NVMe SSD」。新增 `media_known` 字段；未确认的卷显示「未知介质」，`bus_type="Unknown"`，推荐文案不再承诺速度。
- **A22**：`FastDirectorySizer` 新增 `CalculateDirectorySizeDetailed`，返回 `Complete / SkippedDirectories / SkippedFiles / HitDepthLimit`；资产增加 `size_measurement_complete / size_skips`；概览返回 `size_scope="sum_of_measured_install_directories"` 与 `size_measurement_incomplete_count`。真机：4 项体积未完整测量，已在 UI 标注。
- **A16**：`BuildGraph` 不再删环。事实图保留全部边，新增 `CountCyclicEdges` 仅作展示统计。A→B→C→A 三条边全部保留（有测试覆盖）。
- **A17**：规则热加载改为「先校验候选快照，再替换」；正则加 250 ms 超时；校验失败保留上一版有效规则。
- **A18**：登记文件原子写入、损坏显式报错（不再当作空列表）。
- **A19**：`VaultRegistration` 增加 `env_var_previous_value / env_var_was_set`，为后续可恢复的环境配置变更留出字段。

### W05 / P1 保留证据图 —— ✅ 完成（见 A16）

### W06 / P0→P1 操作日志与统一写门控 —— ✅ 完成

新增 `Core/Operations/OperationRecord.cs` 与 `OperationLog.cs`：

- 记录精确源/目标身份、staging 与备份路径、环境配置旧值、期望清单、冲突列表与步骤时间线。
- **写前日志**：每个文件系统动作之前先落盘，异常终止后仍能读到「尝试了什么」。
  恢复判断读日志，**不再相信缓存里的 Completed**。
- 显式状态：Planned / PreflightFailed / Copying / Copied / VerifyFailed / Verified /
  SwitchFailed / Switched / NeedsAttention / FailedRecoverable / Committed。
- **重叠资源互斥**：两个任务不能在同一路径上赛跑（大小写与尾分隔符不敏感）。
- 原子写入（临时文件 + 替换），损坏文件显式报错而非当作「无记录」。
- 只读恢复接口：`GET /api/operations`、`GET /api/operations/{taskId}`。

### W07 / P1 文件迁移内核 —— ✅ 完成

新增 `Core/Operations/MigrationKernel.cs`，逐条落实审计要求：

| 审计要求 | 实现 |
|---|---|
| A03 staging 新建且任务独占 | 目标恒为 `<target>.sentinel-staging-<taskId>`，已存在则拒绝 |
| A03/A06 同名冲突不覆盖 | 目标已存在同名文件时**两侧都保留**，停止并报 `NeedsAttention` |
| A04 逐文件哈希 | `FileIntegrity` 按 SHA-256 与文件集比对，明确拒绝「仅比总字节数」 |
| A04 源稳定性 | 复制后重新测量源；发生变化则拒绝切换（`source_unstable`） |
| A05 路径边界 | `PathIdentity` 拒绝同路径、互相包含、别名越界、重解析点叠加 |
| A10 精确目标 | 严格使用用户确认路径；修复探针「`…\models\ollama` → `…\models\models`」 |
| A11 空间不早收 | 切换成功后**源备份保留**，`BackupDisposition=Retained`，回收是独立受授权步骤 |
| W06 崩溃可判定 | 每步先写日志；失败或取消回滚到原布局 |

**真机实测（R1 档，临时目录）**

```
R1 relocate → {"status":"Succeeded","did_mutate":true,"code":"switched",
  "message":"切换完成：原路径已指向新位置。源备份仍保留，空间尚未回收。"}
exact target exists: True
anchor is ReparsePoint: True
read through anchor: FAKE-WEIGHTS-2MB
/api/operations → total=1, unfinished=1
  state=Switched  backup=…\ollama.sentinel-backup-task_aa79d1cd3a50  disposition=Retained
```

默认档位仍为 R0：`POST /api/vault/relocate` → `Blocked`。

### W08 / P1 Ollama 专用适配器 —— ✅ 完成（协议层）

新增 `Core/Adapters/OllamaAdapter.cs`。**审计纠正了机制顺序**：应优先修改应用自身支持的
官方配置，文件系统重定向只作为经过评估的兼容手段——因为 Junction 对应用隐瞒了真相，
且会被应用自己的更新器切断（A06）。

适配器能力：
- 发现可执行文件、版本、服务可达性、**有效**模型路径，以及该路径的来源
  （用户变量 / 进程变量 / 应用默认）；
- 盘点 manifests / blobs 与总字节数，作为"数据确实存在"的证据；
- 探测运行中的服务实际愿意提供哪些模型；
- 置信度由证据推导（Confirmed / Inferred / Unknown），**未知即 Unsupported**，不做尽力而为；
- `ChooseMechanism()` 优先 `OfficialConfig` 并说明为何 Junction 是妥协。

内核扩展（W07）：
- `MigrationRequest` 增加 `Mechanism / ConfigVariable / IEnvironmentStore / HealthCheck`，
  测试永不写入真实用户环境变量；
- 新增官方配置切换路径：停放源目录 → 修改配置 → **要求消费端健康检查通过**；
  失败则同时回滚配置值与原目录。

**通过测试发现的真实顺序缺陷**：机制前置条件原本在"切换"阶段校验，也就是在数据**已经发布之后**。
现已移入 preflight，无法满足的请求不会再移动任何字节。

**真机只读验证（真实 Ollama）**

```
found=True  confidence=Confirmed  version='ollama version is 0.34.0'
models_path=D:\AIStack\models\ollama  source='用户环境变量 OLLAMA_MODELS'
manifests=4  blobs=13  bytes=42306743403  served_models=4
mechanism=OfficialConfig
relocation_enabled=False   # 默认仍是 R0
```

只读接口：`GET /api/adapters/ollama`，响应中显式列出哪些验收已做、哪些未做。

### W09 / P1 漂移诊断与冲突保全修复 —— ✅ 完成（计划层）

新增 `Core/Operations/DriftRepairPlanner.cs`：

- **逐路径分类**：Identical / AnchorOnly / VaultOnly / **Conflicting**。
- **冲突绝不自动合并**：同名不同内容时两侧都保留，产出可供人工比对的 sidecar 命名建议
  （由内容哈希派生，重复运行幂等）。
- **不按时间戳取舍**：审计的反例（时间戳相同、内容不同 → 丢一侧；锚点更新 → 覆盖仓库侧）
  已有专门测试锁定，两处都断言**两侧数据均未被改动**。
- **离线卷不是可清理残留**：目标卷不可访问时只报告"先恢复该卷"，不触碰锚点数据。
- **配置偏离**作为独立、具名的原因（`DescribeConfigDeviation`），覆盖 A19。
- **诊断不杜撰原因**：报告与建议中不出现"软件升级导致"这类未经证明的归因。

### W10 / P1 第二适配器与只读扩展 —— ✅ 完成

新增 `Core/Adapters/HuggingFaceAdapter.cs` 与 `AdapterRegistry.cs`。

**修正 A20 的层级错误**：`HF_HUB_CACHE` 指向 hub 目录本身，`HF_HOME` 指向其父级。
原实现扫描 `…huggingface\hub` 却写入 `HF_HOME`，会把缓存变成 `hub/hub` 并孤立真实缓存。
`ValidateRelocationTarget` 现在会拒绝这种层级不匹配。

- 两种快照布局都被识别（符号链接 vs 实体副本）；混用时如实报告，不做假设。
- 共享 blob 只从 `blobs/` 计一次。
  **修复此点暴露了我自己第一版的缺陷**：用 `AllDirectories` 枚举快照会跟随重解析点
  回到 `blobs/`，把同一个 payload 再算一次。清单现在按不跟随链接的方式遍历。
- `AdapterRegistry` 逐领域声明写能力：只有通过验收的 `ai_models` 可进入目录迁移；
  `docker_disk` / `social_docs` / `creative_media` 一律 `Unsupported` 并附原因。
- **A21**：单文件候选在进入目录内核之前就被拒绝，判定依据是**文件系统的真实类型**，
  而不是只信声明。

### W12 / P1 可复现发布与离线启动验收 —— 🟡 主要项完成

新增 `scripts/New-Release.ps1`，产出完整 ZIP：可执行文件、`wwwroot`（含本地前端资源）、
`rules`、README、LICENSE，以及带**逐文件哈希 + 提交 + SDK 版本 + 工作树是否干净**的
`MANIFEST.json`。若 `index.html` 仍引用远程 CDN 或缺少本地资源，脚本直接失败。

**修复了一个真实的 A26 打包缺陷**（由新脚本发现）：`rules/` 只用
`CopyToOutputDirectory` 声明，能进 `bin/` 却**在 `dotnet publish` 时被静默丢弃**。
源码树里运行正常、独立安装却找不到自己的规则——正是审计警告的"掩蔽"。
现改为 `Content` + `CopyToPublishDirectory`。

新增启动自检，缺失资源会被明确报出。

**实机验收**：把 ZIP 解压到含**中文与空格**、**与源码无关**且**不含 `src/`** 的目录，
以普通账户离线启动：

```
[OK] 界面资源: …\验收 测试 目录\Sentinel R0 空目录\wwwroot
[OK] 规则库: …（59 条画像规则）
[OK] 本地前端资源: wwwroot/vendor
index.html → 200      vendor/tailwind.min.js → 200
policy profile → R0-safe-observation
删除 rules/ 后 → [!] 分发包不完整：…未找到 rules/app_semantics.json
```

未完成：.NET 10 LTS 迁移（需先验证 Photino 打包兼容）、WebView2 GUI 实机检查、CLI/MCP 接口面。

### W11 / P2 简化界面与异步任务 —— ✅ 完成

**A28 的固定延迟已移除**：启动不再依赖 `Thread.Sleep(800)` 猜测服务就绪，改为轮询
`/api/ready`——能问到就说明 HTTP 服务真的在受理连接。

**启动不再阻塞在全盘扫描上**：新增 `ScanSnapshotCache`，上次扫描结果原子落盘；
本次启动**立即载入缓存并渲染**，同时后台线程执行新扫描。

```
第一次启动（无缓存）: 服务就绪 1578 ms → phase=Scanning（界面可立即查询）
第二次启动（有缓存）: 服务就绪 1322 ms → 立即可用 apps=84 serving_cache=True
                     后台扫描完成后 phase=Completed assets=84 duration=2993ms
```

界面侧改动（对应审计的"首页应回答什么"）：
- **扫描状态横幅**：检查中 / 已验证 / 需要注意 / 尚未扫描 四态用颜色与文字明确区分，
  并单独标注"当前显示缓存结果"，**不把陈旧数据当作刚验证过的数据**。
- **每张卡片回答"证据是什么"与"可做哪一项动作"**：证据分级说明（实时进程 / 间接的快捷方式与
  配置写入 / 仅文件时间戳的弱证据 / 无证据），动作用途与当前能力姿态一致。
- **字号从 10–11px 提升到 13–14px**，长文本行高 1.55。
- **路径可一键复制**（事件委托 + `data-copy`，不拼接进 handler）。
- 扫描按钮改为**启动即返回 + 轮询状态**，不再冻结界面。

### W06 Commit 步骤与只读 CLI —— ✅ 完成（R3 补齐）

**Commit**（`Core/Operations/MigrationCommit.cs`）：空间回收是**独立且受授权**的动作，不再是切换的副作用。

- 未授权时不删除任何数据；只有 `Switched` 状态可提交，其他状态一律拒绝——避免"猜"上一次发生了什么。
- 操作记录缺失或损坏时**失败并明确告知不要凭推断删除任何目录**。
- **回收量以卷可用空间的观测差值为证据**，不是文件大小累加；同时并列报告预期值，让差异可见。
  误差来源如实说明：文件占用为簇大小整数倍、期间可能有其他进程写入。
- **Recover 不是"解除链接"**：它会重建原布局，并在锚点出现切换后的新数据时**拒绝删除**，
  保留两侧并说明位置；若备份已回收，它明确说明无法恢复而不是假装可以。

**只读 CLI**（`CommandLine.cs`）：`--policy / --scan / --adapters / --operations / --dependencies / --help`。
与 GUI、HTTP 共用同一个 `CapabilityPolicy`，**脚本无法取得界面会拒绝的能力**；此文件内不存在任何写路径。

```
R0  --policy : VaultRelocate / Commit / Recover 均为 Blocked
R1  --policy : 上述四项 Enabled；UninstallLive 与 ForceClean 仍 Unsupported；
               RestorePointCreate 仍 Blocked（未在提权环境完成真实创建验收）
--scan       : read_only=true，84 项资产，JSON 输出
--dependencies: 243 条边，其中 6 条参与环（事实图保留全部边）
```

### 尚未完成

（本节原列「MCP 未实现」，该缺口已在后续轮次关闭，见下方「八、后续轮次补充」。）
- **真实"停→迁→重启→推理"**与**跨卷迁移**：需隔离账户 + 两个测试卷。
- **.NET 10 LTS 迁移**：未做，需先验证 Photino 打包兼容（.NET 9 官方支持至 2026-11-10）。
- **WebView2 GUI 实机检查**：未做（已验收的是 HTTP 服务与静态资源，不是渲染结果）。

### R1 档位的开放条件

`--profile=r1` 启用 `RelocationVerifiedProfile`：只开放 `VaultRelocate` 与 `JunctionUnlink`；
`UninstallLive / ForceClean / DriftAutoHeal / RestorePointCreate` 仍然关闭。
之所以不设为默认：R1 验收要求**隔离账户 + 两个真实测试卷 + 真应用重启可用性**，
本轮未具备该环境，故不宣称 R1 已通过。

---

## 四、测试与未运行项

```
dotnet test src/AppAssetSentinel.Tests/AppAssetSentinel.Tests.csproj
已通过! - 失败: 0，通过: 162，已跳过: 0，总计: 162
```

新增/重写的用例覆盖：能力门控、登记原子性与损坏、路径边界（同路径/互相包含/别名/大小写/尾分隔符）、
内容级完整性（同尺寸不同内容、缺失、多余）、漂移诊断（锚点被替换、目标离线）且断言**两侧数据均未被改动**、
Origin/Host/令牌、路径 HTML 属性往返、恶意标记转义、计划身份拒绝、未知状态不伪造、介质不猜测、
体积完整性、证据图保留环。

**验收状态汇总**

已运行（见下方"跨物理盘迁移验收结果"与 W12 小节）：
- ✅ 跨物理盘迁移（D: Disk1 → E: Disk0），含 A10 精确目标与 A11 空间不早收的真实验证。
- ✅ 发布物在空目录、离线、普通账户、含中文与空格的路径下启动（W12）。
- ✅ 真实 Ollama 只读探测：服务可达并列出 4 个模型（W08）。
- ✅ 注册表备份以 reg.exe 退出码与文件非空判定（A24）。

仍未运行（不计为通过）：
- 对用户真实模型库执行完整"停机 → 迁移 → 重启 → 推理"。**机制**已在真实 Ollama 实例上
  验证通过（重定向后成功列出全部 4 个真实模型，且未改动用户配置、未影响其服务）；
  但真正搬动其 42 GB 数据属于需要用户在场的维护窗口操作，未在未授权情况下执行。
- UAC 提权与系统还原点的真实创建结果（需交互式提权）。
- 发布包二进制组成、数字签名与第三方依赖漏洞扫描。
- .NET 10 LTS 迁移（需先验证 Photino 打包兼容）。
- WebView2 GUI 渲染实机检查（已验收的是 HTTP 服务与静态资源，不是渲染结果）。
- CLI / MCP 接口面（未实现）。

---

## 五、当前默认限制（与 README 承诺的差异）

README 中「双向锁死防漂移、一键平移、自动愈合、彻底卸载」等描述描述的是**目标能力**，
在当前 R0 版本中均**未开放**。程序内 `/api/policy` 与界面会如实展示这一姿态，
未开放的动作不再出现在界面上，也不会返回成功。README 需按 R0 实际能力重写。

---

## 六、真实应用配置重定向验收（R1 最后一块证据）

`scripts/Test-OllamaConfigRedirect.ps1` 用一个**临时的真实 Ollama 实例**验证 W08 选择的
「官方配置优先」机制：该实例的 `OLLAMA_MODELS` 指向一个经联接重定向的位置，模型库是用户真实的存储。

安全属性（脚本内逐条断言，而非口头保证）：

- 不修改用户级或机器级 `OLLAMA_MODELS`；
- 不重启、不停止用户正在运行的服务；
- 只在子进程内重定向，并用一个未占用端口启动**第二个**临时实例；
- 通过临时联接读取真实库，不复制、不删除任何生产字节。

```
用户级 OLLAMA_MODELS（运行前）：D:\AIStack\models\ollama
重定向路径（联接）：…\Temp\sentinel_ollama_redirect_a15bf007\models -> D:\AIStack\models\ollama

[验收] 重定向后的实例可提供的模型：
  - hf.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF:UD-Q3_K_XL
  - hf.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF:UD-Q2_K_XL
  - qwen3.5-defiant:q8_0
  - huihui_ai/gemma-4-abliterated:12b-qat
  合计：4 个

用户级 OLLAMA_MODELS（运行后）：D:\AIStack\models\ollama
用户配置未被修改：True
重定向路径残留：False
用户原有服务仍在提供 4 个模型（未受影响）
```

**证明了什么**：真实 Ollama 确实读取 `OLLAMA_MODELS`，并能在重定向后的位置加载并列出真实模型
——W08 选择「改官方配置而非建联接」的机制成立，且不需要让应用对数据位置一无所知。

**没有证明什么**（不计为通过）：对用户真实存储执行一次完整的「停机 → 迁移 → 重启 → 推理」
尚未运行。机制已被真实应用验证，但搬动用户的真实数据属于需要用户在场的维护窗口操作。

---

## 七、下一步（按审计顺序）

1. 对用户真实存储执行一次完整的维护窗口迁移演练（需用户在场），验证 `ollama` 推理可用。
2. `RestorePointCreate` 在提权环境下的真实创建验收。
3. `.NET 10 LTS` 迁移（需先验证 Photino 打包兼容）。
4. WebView2 渲染实机检查、MCP 接入面、二进制签名与依赖漏洞扫描。


---

## 八、后续轮次补充（第 6–12 轮）

上文第 1–5 轮写就后，又完成了以下工作。**本节是当前状态的权威记录。**

### W06 进程级故障注入验收 —— ✅ 完成

W06 要求「每个关键步骤前后强制终止并重启，可由日志确定实际状态」。此前只实现了判定逻辑
（`RecoveryInspector`），未做字面的强制终止。现已补上。

`FaultInjection.cs` 在选定状态**落盘之后、对应文件系统动作执行之前**调用
`Environment.FailFast`——那正是崩溃会落进去的窗口。安全护栏：拒绝任何命中用户数据片段的路径
（`AIStack`/`.ollama`/`Users`/`AppData`/`Program Files`/`Windows`/`.cache`/`huggingface`），
并要求源路径含 `sentinel_fixture` 标记，无法被误指向生产数据。

`scripts/Test-CrashRecovery.ps1` 的结果：

```
CrashAt   LoggedState  Observed            DataAccountedFor  Consistent
Copying   Copying      OriginalIntact      True              True
Verified  Verified     OriginalIntact      True              True
Switched  Switched     SwitchedWithBackup  True              True

所有崩溃点重启后都能确认数据位置：True
```

三次注入都产生真实终止（退出码 `0xC0000409`）。重启后**仅凭日志**即可判定真实状态，
磁盘与日志在每一点一致，三处数据都有归属。

### W11 任务取消 —— ✅ 完成

新增 `ScanCancellation` 与 `ScanPhase.Cancelled`。令牌在**顶层阶段之间**与**逐资产体积循环内**
两处被遵守（后者是几乎全部耗时所在）。

真机验证：

```
取消前:   phase=Scanning
取消请求: {"status":"cancelling","cooperative":true}  HTTP=200
取消后:   phase=Cancelled  duration=938ms  serving_cache=True   （完整扫描约 3550ms）
重复取消: {"status":"not_running"}
```

`serving_cache=True` 是关键：取消后继续提供**上一次完整**结果，不把残缺数据当权威。
响应中明说取消是**协作式**的，未扫描部分为空而非「没有内容」。

### W12 MCP 接入面 —— ✅ 完成

`McpServer.cs`，newline-delimited JSON-RPC 2.0 over stdio。与 GUI / HTTP API / CLI
**共用同一个 `CapabilityPolicy` 实例**——这个进程拒绝的能力，问 MCP 也拿不到。

真机 JSON-RPC 实测：

```
initialize  → protocol 2024-11-05, server appasset-sentinel 1.0.0-r0
tools/list  → 5 个只读工具
sentinel_policy (R0)           → R0-safe-observation
sentinel_policy (--profile=r1) → R1-relocation-verified（与其他三个入口同源）
sentinel_vault_relocate        → isError=true，以策略原因拒绝，且从未出现在 tools/list 中
```

设计上**不广告写工具**：不是「调用后被拒」，而是压根不在列表里。

### W08 已迁移存储可用性验收 —— ✅ 完成（「可列出」部分）

`scripts/Test-MigratedStoreUsable.ps1` 测完整链路：

```
[1] 内核跨物理盘迁移（Disk1 → Disk0）
    status=Succeeded code=switched did_mutate=True
    确认目标存在: True   清单已随迁: 4

[2] 真实 Ollama 读取内核的「确认目标」
    迁移后可列出的模型: 4
      - hf.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF:UD-Q3_K_XL
      - hf.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF:UD-Q2_K_XL
      - qwen3.5-defiant:q8_0
      - huihui_ai/gemma-4-abliterated:12b-qat

[3] 源侧断言
    锚点已是重解析点: True
    操作记录: state=Switched / disposition=Retained   源备份保留: True

用户级 OLLAMA_MODELS 未变 · 残留 0 · 真实库完好（4 manifests / 13 blobs）
```

用的是**真实的 4 个模型清单**、真实的跨物理盘内核迁移、真实的 Ollama 实例。
这满足了 W08 的「已存在模型迁移后可列出」。

**仍未证明、不声称**：**推理**。那需要多 GB 权重，本验收刻意只搬约 25 KB 的清单与元数据。

### 本轮发现的三个自制缺陷（均靠运行产物而非阅读源码发现）

1. **测试污染生产数据（第 1 轮）**：旧测试套件覆写了用户 `OLLAMA_MODELS` 到已删除的临时路径，
   并把 10 条夹具写进生产登记文件——**Ollama 实际处于断链状态**。已修复并复验。
2. **写入静默失败（第 6 轮）**：两个文件被报告「创建成功」但磁盘上什么都没有；随后的构建
   「成功」是因为没有新代码可编译。若未核对测试数（143 → 152），就会声称代码存在而它并不存在。
3. **`--profile` 被当作命令（第 12 轮）**：`IsGlobalFlag` 匹配任何以 `--profile` 开头的参数，
   导致 `--profile=r1 --server-only` 打印帮助后退出——**R1 服务器模式自第 5 轮起一直不可用**。
   之所以长期未暴露，是因为后续验收要么用 R0 默认档，要么用 `--fault-inject`（其分支在
   `IsCommand` 检查之前）。

三者共同点：**只有把真实产物跑起来才会暴露**。本报告中的所有结论均以运行结果为准。

### 发布物

- **v1.0.0-r0**（预发布）：https://github.com/cloudenshine/AppAsset-Sentinel-Native/releases/tag/v1.0.0-r0
- ZIP SHA256 `a3e1a0d1…`（该次构建的工作树干净，`MANIFEST.json` 中 `working_tree_dirty=false`）
- 包内含 `RELEASE_v1.0.0-r0.md`，按「已验证 / 未运行 / 不支持」三类分列

### 仍未运行（不计为通过）

| 项目 | 原因 |
|---|---|
| 真实 42 GB 存储的完整迁移**并推理** | 机制、跨卷、可列出三层均已用真实数据验证；搬动生产数据需用户在场的维护窗口 |
| 隔离 Windows 账户验收 | 未创建隔离账户 |
| 系统还原点的提权真实创建 | 需交互式 UAC |
| WebView2 GUI 渲染实机检查 | 已验收的是 HTTP 服务与静态资源，不是渲染结果 |
| 二进制签名与依赖漏洞扫描 | 需签名证书与外部漏洞库 |
| .NET 10 LTS 迁移 | 需先验证 Photino 打包兼容 |
### 第 14 轮：恢复判定的一处真实建模错误 —— 已修复

`NeedsAttention` 被映射到**单一**预期磁盘布局（`SwitchedWithBackup`），但它由**三条路径**产生，
布局各不相同：

| 路径 | 磁盘实际布局 |
|---|---|
| 目标冲突 / 目标被占用 | 源仍是普通目录，两侧都存在 |
| 切换未能确认 | 锚点是链接且备份存在 |

因此冲突路径下，判定会报告「日志与磁盘不一致」——而实际上两者是一致的——并且会**基于错误的
布局**给出后续建议。这是通过追问「分类器对一条我没跑过的路径会怎样」发现的。

修复：`ExpectedLayout` 对多路径可达的状态返回 `null`；新增 `LogDecides` 标志说明日志是否有决定权；
无决定权时结论明确**以磁盘为准**。`NeedsAttention` 现在只报告实测布局，不再声称日志决定了它。

```
冲突场景 → ObservedLayout=BothPresent, LogDecides=false, DataAccountedFor=true,
           建议为「两侧都保留，请人工比对，系统不会自动合并或删除」
确定状态 → LogDecides=true, LogMatchesDisk=true（行为不变）
```

测试数 162 → **164**。