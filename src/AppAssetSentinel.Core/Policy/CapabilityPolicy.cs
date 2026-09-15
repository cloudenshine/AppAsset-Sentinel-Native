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
        [Capability.VaultRelocate] = new(Capability.VaultRelocate, CapabilityState.Blocked, true,
            "A03/A04/A05/A10：迁移内核缺少 staging 独占、逐文件哈希与路径边界校验，待 W07 验收。"),
        [Capability.JunctionUnlink] = new(Capability.JunctionUnlink, CapabilityState.Blocked, true,
            "A12：解除链接会隐藏真实恢复语义，待 W07 拆分 unlink 与 recover 后开放。"),
        [Capability.DriftAutoHeal] = new(Capability.DriftAutoHeal, CapabilityState.Blocked, true,
            "A06：自动愈合会按时间戳覆盖并删除漂移侧数据，待 W09 冲突保全验收。"),
        [Capability.RestorePointCreate] = new(Capability.RestorePointCreate, CapabilityState.Blocked, true,
            "A07/A24：还原点描述存在脚本注入风险且失败时状态不真实，待 W03/W06 修复。")
    });

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
