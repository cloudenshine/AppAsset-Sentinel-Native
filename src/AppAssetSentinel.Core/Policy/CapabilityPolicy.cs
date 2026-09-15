using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Policy;

/// <summary>
/// The single capability gate required by AUDIT W01. Every mutating entry point —
/// modern API, legacy compatibility route and any future CLI — must consult this
/// before touching user data, so no route can bypass the policy.
/// </summary>
public sealed class CapabilityPolicy
{
    private readonly Dictionary<Capability, CapabilityDecision> _decisions;

    /// <summary>
    /// The R0 "safe observation" posture mandated by the audit: only read-only and
    /// preview work is open. Every capability that rewrites user data stays closed
    /// until its work package (W06/W07/W08) passes its acceptance gate.
    /// </summary>
    public static CapabilityPolicy SafeObservationDefault() => new(new Dictionary<Capability, CapabilityDecision>
    {
        [Capability.ObserveReadOnly] = new(Capability.ObserveReadOnly, CapabilityState.Enabled, false,
            "只读扫描与证据采集，可安全开放。"),
        [Capability.PlanPreview] = new(Capability.PlanPreview, CapabilityState.Enabled, false,
            "对比计划与现实，不产生任何写入。"),
        [Capability.DriftDiagnose] = new(Capability.DriftDiagnose, CapabilityState.DryRunOnly, false,
            "漂移诊断仅报告差异，不修改数据。"),
        [Capability.UninstallSimulate] = new(Capability.UninstallSimulate, CapabilityState.DryRunOnly, false,
            "仅生成演练结论，结果明确标记为 Simulated，不代表真实卸载。"),
        [Capability.RegistryBackup] = new(Capability.RegistryBackup, CapabilityState.Enabled, true,
            "注册表导出为追加式归档，不删除或改写现有用户数据。"),
        [Capability.RulesReload] = new(Capability.RulesReload, CapabilityState.Enabled, false,
            "重载外置规则文件，仅影响内存中的画像，不触碰用户数据。"),

        [Capability.UninstallLive] = new(Capability.UninstallLive, CapabilityState.Unsupported, true,
            "A01：真实卸载尚未实现，必须返回 not_supported，不得返回成功。"),
        [Capability.ForceClean] = new(Capability.ForceClean, CapabilityState.Unsupported, true,
            "A02：递归删除未经过依赖护盾、数据保留与备份门控，已禁用。"),
        // The following capabilities are implemented and verified (see W06-W09), but they are
        // closed in this profile because R0 is defined as a read-only release. They are opened
        // by RelocationVerifiedProfile(), not because the code is missing.
        [Capability.VaultRelocate] = new(Capability.VaultRelocate, CapabilityState.Blocked, true,
            "R0 为只读发布档：迁移内核（W07）已实现并验收，需切换到 R1 档位才开放。"),
        [Capability.JunctionUnlink] = new(Capability.JunctionUnlink, CapabilityState.Blocked, true,
            "R0 为只读发布档：解除联接属于写入操作，需切换到 R1 档位。"),
        [Capability.VaultCommit] = new(Capability.VaultCommit, CapabilityState.Blocked, true,
            "R0 为只读发布档：回收源备份会真正销毁数据，需切换到 R1 档位并显式授权。"),
        [Capability.VaultRecover] = new(Capability.VaultRecover, CapabilityState.Blocked, true,
            "R0 为只读发布档：恢复会改动已切换的布局，需切换到 R1 档位并显式授权。"),
        [Capability.DriftAutoHeal] = new(Capability.DriftAutoHeal, CapabilityState.Blocked, true,
            "R0 为只读发布档：冲突保全式修复（W09）已实现，需切换到 R1 档位并按冲突规则执行。"),
        [Capability.RestorePointCreate] = new(Capability.RestorePointCreate, CapabilityState.Blocked, true,
            "A07/A24：脚本注入与失败状态问题已修复（不再拼接脚本、按退出码判定），"
            + "但尚未在提权环境下完成真实创建验收，因此保持关闭。")
    });

    /// <summary>
    /// R1 profile (AUDIT release gate "Ollama 可信迁移版"). It opens relocation against the
    /// W07 kernel while keeping every capability that still lacks an acceptance gate closed.
    /// This is deliberately not the shipped default: R1 acceptance requires an isolated
    /// account plus two real test volumes, which is tracked separately.
    /// </summary>
    public static CapabilityPolicy RelocationVerifiedProfile()
    {
        var decisions = SafeObservationDefault().Snapshot()
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        decisions[Capability.VaultRelocate] = new CapabilityDecision(
            Capability.VaultRelocate, CapabilityState.Enabled, true,
            "W07 迁移内核已启用：staging 独占、路径边界、逐文件哈希、冲突保全、写前日志。");

        decisions[Capability.JunctionUnlink] = new CapabilityDecision(
            Capability.JunctionUnlink, CapabilityState.Enabled, true,
            "W07：解除联接仅移除锚点链接，源备份与仓库数据均保留。");

        decisions[Capability.VaultCommit] = new CapabilityDecision(
            Capability.VaultCommit, CapabilityState.Enabled, true,
            "W06：提交会删除已切换任务的源备份；要求显式授权，回收量以卷可用空间为证据。");

        decisions[Capability.VaultRecover] = new CapabilityDecision(
            Capability.VaultRecover, CapabilityState.Enabled, true,
            "W07：恢复会重建原布局，并拒绝删除切换后出现在锚点的新数据。");

        decisions[Capability.DriftAutoHeal] = new CapabilityDecision(
            Capability.DriftAutoHeal, CapabilityState.Enabled, true,
            "W09：修复要求显式授权；存在内容冲突时只保留两份并交人工决定，绝不按时间戳覆盖。");

        return new CapabilityPolicy(decisions);
    }

    public CapabilityPolicy(Dictionary<Capability, CapabilityDecision> decisions)
    {
        _decisions = decisions;
    }

    public CapabilityDecision Check(Capability capability)
    {
        if (_decisions.TryGetValue(capability, out var decision))
        {
            return decision;
        }

        // An unknown capability is never implicitly allowed.
        return new CapabilityDecision(capability, CapabilityState.Unsupported, true,
            $"能力 {capability} 未被策略声明，默认视为不支持。");
    }

    /// <summary>True when the capability is permitted to change user data right now.</summary>
    public bool AllowsMutation(Capability capability) =>
        Check(capability) is { AllowsMutation: true, IsAllowed: true };

    public IReadOnlyDictionary<Capability, CapabilityDecision> Snapshot() => _decisions;

    /// <summary>Serialisable posture for the UI so the app can show what is actually open.</summary>
    public List<CapabilityPosture> Describe() => _decisions
        .OrderBy(kv => kv.Key.ToString())
        .Select(kv => new CapabilityPosture
        {
            Capability = kv.Key.ToString(),
            State = kv.Value.State.ToString(),
            AllowsMutation = kv.Value.AllowsMutation && kv.Value.IsAllowed,
            Reason = kv.Value.Reason
        })
        .ToList();
}

public sealed class CapabilityPosture
{
    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("allows_mutation")]
    public bool AllowsMutation { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
