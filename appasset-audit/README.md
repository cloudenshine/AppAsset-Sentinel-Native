# AppAsset Sentinel Native 体检材料

基线：f7c9dbf7a29b55cf7fa3d832bf3833ac67ee6e75；审查日：2026-09-15。

- `AUDIT.md`：结论、28 项工程发现、价值路线和验证边界。
- `IMPLEMENTATION_BACKLOG.md`：12 个整改工作包及验收门槛，不是已经完成的变更。
- `logic_probes.py`：仅在新建临时目录中运行的六个逻辑反例，Python 3.10+ 与 Node.js；不运行 C# 应用或读取用户数据。
- `logic_probe_results.json`：本次已执行的逻辑反例结果；并非原生 Windows 验收结果。
- `manifest.json`：文件摘要。

可复现逻辑反例：`python logic_probes.py`。不要把此命令与仓库原有 `dotnet test` 混淆；原测试套件的用户设置隔离问题尚待修复。

本次未修改 GitHub 仓库，未在用户主机部署/迁移/删除任何资产。
