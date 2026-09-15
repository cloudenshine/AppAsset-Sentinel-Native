# 安全策略 (Security Policy)

## 🛡️ 安全设计理念

AppAsset Sentinel 是一个深涉系统文件、注册表与进程管理的系统级控制台。我们从第一天起就将**防御性安全（Defensive Safety）**置于最高优先级：

1. **物理级系统路径硬熔断 (`CriticalDirectoryGuard`)**：
   * 任何卸载、清理、移动逻辑触碰 `C:\Windows`、`System32`、`SysWOW64`、`C:\` 根盘符、`C:\Users\Admin` 时，底层代码会无条件抛出 `CriticalSystemDirectoryException` 立即熔断并中断进程。
2. **免死金牌与骨肉分离**：
   * 对驱动、SDK、系统运行库赋予终身免疫；
   * 软件卸载时自动剥离并保护关联的大模型权重、虚拟磁盘与创作工程，绝不连坐销毁数据。
3. **高危操作后悔药**：
   * 卸载前自动导出 `.reg` 注册表备份；
   * 支持联动 Windows 卷影系统还原点快照。

## 🚨 漏洞报告

如果您在代码或逻辑中发现了可能导致系统文件受损或权限绕过的安全漏洞，请勿通过公开 Issue 披露：
* 请发送邮件至：`cloudenshine@gmail.com`
* 我们将在 48 小时内确认并评估风险，并在修复后统一发布安全公告与致谢。
