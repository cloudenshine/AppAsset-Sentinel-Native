# AppAsset Sentinel Native v1.0.0-r0 — 安全观察版

**发布摘要。本文件区分「已验证」「未运行」「不支持」三类，不得混为一谈。**

- 提交：`66f9e55cfe988f9c783deb3b4e665569cf0348ab`（构建时工作树干净）
- 分发包：`AppAssetSentinelNative-win-x64-66f9e55.zip`
- 内容：可执行文件 + `wwwroot`（含本地前端资源）+ `rules`（59 条画像规则）+ README + LICENSE + `MANIFEST.json`
- `MANIFEST.json` 载有逐文件 SHA-256、提交号、SDK 版本与工作树是否干净

---

## 一、已验证（有可复现证据）

| 项目 | 证据 |
|---|---|
| 构建与测试 | `162 passed, 0 failed` |
| 空目录离线启动 | 解压到含中文与空格的、与源码无关的目录（无 `src/`），普通账户启动成功 |
| 包内依赖完整 | 启动自检输出 `[OK] 界面资源 / 规则库（59 条）/ 本地前端资源` |
| 缺依赖清楚报错 | 删除 `rules/` 后输出 `[!] 分发包不完整：…未找到 rules/app_semantics.json` |
| 能力门控（四入口一致） | GUI / HTTP API / CLI / MCP 共用同一 `CapabilityPolicy` |
| 跨物理盘迁移 | D:(Disk1) → E:(Disk0)，精确目标、无重复叶子层级、备份保留、两侧数据保全 |
| 真实 Ollama 配置重定向 | 临时实例在重定向位置列出 **4 个真实模型**；用户配置与服务未受影响 |
| 写操作拒绝行为 | `force-clean` / `uninstall/execute` / `drift/auto-heal` 返回 `Unsupported` / `Blocked`，不返回成功 |
| 测试隔离 | 测试运行前后用户环境变量与登记文件字节不变 |
| MCP | JSON-RPC 握手成功；5 个只读工具；写工具不予广告且以策略原因拒绝 |
| 扫描取消 | `phase=Cancelled`，938ms 停止（完整扫描约 3550ms），继续提供上一次完整结果 |

## 二、未运行（不计为通过）

| 项目 | 原因 |
|---|---|
| 对用户真实 42 GB 模型库执行完整「停机 → 迁移 → 重启 → 推理」 | 机制已在真实 Ollama 上验证；搬动生产数据需用户在场的维护窗口 |
| 隔离 Windows 账户下的真应用迁移验收 | 未创建隔离账户 |
| 进程级强制终止注入（每步 kill 后重启） | 判定逻辑已实现并测试（`RecoveryInspector`）；字面级故障注入未做 |
| 系统还原点的提权真实创建 | 需交互式 UAC；脚本注入与失败状态判定问题**已修复**，但真实创建未验收 |
| WebView2 GUI 渲染实机检查 | 已验收的是 HTTP 服务与静态资源，不是渲染结果 |
| 二进制签名与第三方依赖漏洞扫描 | 需签名证书与外部漏洞库 |
| .NET 10 LTS 迁移 | 需先验证 Photino 打包兼容（.NET 9 支持至 2026-11-10） |

## 三、不支持（返回 Unsupported，非缺陷）

| 能力 | 原因 |
|---|---|
| 真实卸载执行 | A01：尚未实现，必须返回 `not_supported` |
| 强制清理 | A02：绕过依赖护盾与备份的递归删除已被移除 |
| Docker / WSL 虚拟磁盘迁移 | A21：`ext4.vhdx` 是文件而非目录；需停机与一致性协议 |
| 社交/聊天库与活动影视工程迁移 | 需应用级停放协议 |
| HuggingFace 缓存**写入** | 层级解析与布局识别已实现，写入路径未验收（只读） |

---

## 四、R0 档位的写入政策

默认档位下，**所有**会改变用户数据的操作均被关闭，接口返回明确的 `Unsupported` / `Blocked`
与原因，**绝不返回成功**。运行中可访问 `http://127.0.0.1:8765/api/policy` 查看实际姿态。

`--profile=r1` 可开启已验收的迁移能力（`VaultRelocate` / `VaultCommit` / `VaultRecover` /
`JunctionUnlink` / `DriftAutoHeal`）；`UninstallLive`、`ForceClean` 与 `RestorePointCreate`
在任何档位下都保持关闭。

---

## 五、构建可复现性

```
pwsh -File scripts/New-Release.ps1
```

脚本在 `index.html` 仍引用远程 CDN 或缺少本地前端资源时会**直接失败**，因此发布物不会
在缺少依赖的情况下产出。`MANIFEST.json` 中的 `working_tree_dirty` 为 `false`，
即本分发包可证明对应上述提交。