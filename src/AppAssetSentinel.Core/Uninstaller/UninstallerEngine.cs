using System.Text.Json;
using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Shield;

namespace AppAssetSentinel.Core.Uninstaller;

public class PlanSummary
{
    [JsonPropertyName("total_residue_count")]
    public int TotalResidueCount { get; set; } = 0;

    [JsonPropertyName("auto_clean_count")]
    public int AutoCleanCount { get; set; } = 0;

    [JsonPropertyName("preserved_data_count")]
    public int PreservedDataCount { get; set; } = 0;
}

public class DependentItem
{
    [JsonPropertyName("dependent_name")]
    public string DependentName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public class UninstallPlanResult
{
    [JsonPropertyName("plan_id")]
    public string PlanId { get; set; } = string.Empty;

    /// <summary>False when the request could not even be turned into a plan.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("is_blocked")]
    public bool IsBlocked { get; set; } = false;

    [JsonPropertyName("allow_override")]
    public bool AllowOverride { get; set; } = true;

    [JsonPropertyName("block_reason")]
    public string BlockReason { get; set; } = string.Empty;

    [JsonPropertyName("cascade_impacts")]
    public List<string> CascadeImpacts { get; set; } = new();

    [JsonPropertyName("prerequisite_advice")]
    public string PrerequisiteAdvice { get; set; } = string.Empty;

    [JsonPropertyName("dependents")]
    public List<DependentItem> Dependents { get; set; } = new();

    [JsonPropertyName("software")]
    public SoftwareAsset? Software { get; set; }

    [JsonPropertyName("silent_info")]
    public SilentInfo? SilentInfo { get; set; }

    [JsonPropertyName("preserve_data")]
    public bool PreserveData { get; set; } = true;

    [JsonPropertyName("is_ghost_entry")]
    public bool IsGhostEntry { get; set; } = false;

    [JsonPropertyName("all_residues")]
    public List<ResidueItem> AllResidues { get; set; } = new();

    [JsonPropertyName("auto_clean_items")]
    public List<ResidueItem> AutoCleanItems { get; set; } = new();

    [JsonPropertyName("summary")]
    public PlanSummary Summary { get; set; } = new();

    /// <summary>Real execution posture for this plan, from the single capability gate (W01).</summary>
    [JsonPropertyName("live_uninstall_supported")]
    public bool LiveUninstallSupported { get; set; } = false;

    [JsonPropertyName("live_uninstall_reason")]
    public string LiveUninstallReason { get; set; } = string.Empty;
}

/// <summary>
/// Plans and executes uninstall operations. Under W01 no real uninstall exists yet:
/// planning is open, live execution reports <see cref="OperationStatus.Unsupported"/>
/// and <see cref="ForceClean"/> no longer exists as a callable shortcut (A01/A02).
/// </summary>
public static class UninstallerEngine
{
    public static UninstallPlanResult PlanUninstall(
        string softwareId,
        List<SoftwareAsset> allApps,
        DependencyShield shield,
        CapabilityPolicy policy,
        bool preserveData = true)
    {
        var app = allApps.FirstOrDefault(a => a.Id == softwareId);
        if (app == null)
        {
            return new UninstallPlanResult
            {
                Success = false,
                BlockReason = "未在系统中找到该软件记录。"
            };
        }

        var safety = shield.EvaluateUninstallSafety(softwareId, allApps);
        var silent = FingerprintEngine.IdentifyAndMakeSilent(app);
        var allResidues = ResidueRadar.ScanResidues(app, preserveData);
        var autoClean = allResidues
            .Where(r => r.ConfidenceRating == "HIGH_CONFIDENCE_SAFE" && !r.IsPreservedData)
            .ToList();

        var liveDecision = policy.Check(Capability.UninstallLive);

        return new UninstallPlanResult
        {
            PlanId = "plan_" + Guid.NewGuid().ToString("N")[..12],
            Success = true,
            IsBlocked = !safety.CanUninstall,
            AllowOverride = safety.AllowOverride,
            BlockReason = safety.BlockReason,
            CascadeImpacts = safety.CascadeImpacts,
            PrerequisiteAdvice = safety.PrerequisiteAdvice,
            Dependents = safety.DependentApps
                .Select(d => new DependentItem { DependentName = d, Description = "下游依赖" })
                .ToList(),
            Software = app,
            SilentInfo = silent,
            PreserveData = preserveData,
            IsGhostEntry = app.IsGhostEntry,
            AllResidues = allResidues,
            AutoCleanItems = autoClean,
            Summary = new PlanSummary
            {
                TotalResidueCount = allResidues.Count,
                AutoCleanCount = autoClean.Count,
                PreservedDataCount = allResidues.Count(r => r.IsPreservedData)
            },
            LiveUninstallSupported = liveDecision.IsAllowed,
            LiveUninstallReason = liveDecision.Reason
        };
    }

    /// <summary>
    /// Executes (or simulates) a plan. The client may only reference a plan by id that the
    /// server itself stored; an arbitrary client-supplied plan document is never executed.
    /// </summary>
    public static OperationOutcome ExecutePlan(
        CapabilityPolicy policy,
        UninstallPlanResult? storedPlan,
        string requestedPlanId,
        bool simulate)
    {
        if (string.IsNullOrWhiteSpace(requestedPlanId))
        {
            return OperationOutcome.Failed(Capability.UninstallSimulate, "missing_plan_id",
                "请求缺少 plan_id；服务端只执行自己生成的计划。");
        }

        if (storedPlan == null || !string.Equals(storedPlan.PlanId, requestedPlanId, StringComparison.Ordinal))
        {
            // AUDIT W03: unknown / expired / tampered plans must be rejected.
            return OperationOutcome.Failed(Capability.UninstallSimulate, "unknown_plan",
                $"服务端不存在 plan_id = {requestedPlanId} 的已授权计划，请求被拒绝。");
        }

        if (storedPlan.IsBlocked)
        {
            return OperationOutcome.Blocked(Capability.UninstallSimulate,
                $"该计划已被依赖护盾拦截：{storedPlan.BlockReason}");
        }

        string appName = storedPlan.Software?.DisplayName ?? "未知程序";
        int cleanCount = storedPlan.Summary.AutoCleanCount;

        if (simulate)
        {
            // Honest simulation: no mutation, no audit id pretending to be a live run.
            return OperationOutcome.Simulated(Capability.UninstallSimulate,
                $"演练完成：若执行将处理「{appName}」的 {cleanCount} 项残留（本次未修改任何文件或注册表）。",
                new[]
                {
                    $"指纹: {storedPlan.SilentInfo?.InstallerType ?? "unknown"}",
                    $"命令预览: {storedPlan.SilentInfo?.CommandDisplay ?? "(无)"}",
                    $"计划号: {storedPlan.PlanId}"
                });
        }

        var decision = policy.Check(Capability.UninstallLive);
        return decision.IsAllowed
            ? OperationOutcome.Unsupported(Capability.UninstallLive,
                "真实卸载执行器尚未实现，未做任何修改。")
            : OperationOutcome.Unsupported(Capability.UninstallLive, decision.Reason);
    }

    /// <summary>
    /// Previously performed an unguarded recursive delete (AUDIT A02). It now refuses:
    /// every destructive path must go through the same plan / policy / execution gate.
    /// </summary>
    public static OperationOutcome ForceClean(CapabilityPolicy policy, SoftwareAsset? app)
    {
        var decision = policy.Check(Capability.ForceClean);
        return OperationOutcome.Unsupported(Capability.ForceClean, decision.Reason);
    }
}

/// <summary>Rejects any client-supplied plan document that the server did not author.</summary>
public static class PlanRequestGuard
{
    public static string? ExtractPlanId(JsonElement body)
    {
        try
        {
            if (body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("plan_id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String)
            {
                return idEl.GetString();
            }
        }
        catch { }

        return null;
    }
}
