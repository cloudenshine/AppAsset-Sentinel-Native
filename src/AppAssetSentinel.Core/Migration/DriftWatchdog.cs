using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

/// <summary>What the watchdog actually observed, without inventing a cause (AUDIT A09).</summary>
public enum DriftKind
{
    Healthy,
    AnchorMissing,
    AnchorReplaced,
    TargetMissing,
    JunctionRetargeted,
    Unknown
}

public sealed class DriftFinding
{
    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; set; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DriftKind Kind { get; set; } = DriftKind.Unknown;

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    /// <summary>Observation only. The watchdog never asserts an unproven root cause.</summary>
    [JsonPropertyName("observed_facts")]
    public List<string> ObservedFacts { get; set; } = new();

    [JsonPropertyName("recommended_action")]
    public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class DriftAuditReport
{
    [JsonPropertyName("checked_at")]
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("total_registrations")]
    public int TotalRegistrations { get; set; }

    [JsonPropertyName("drifted_count")]
    public int DriftedCount { get; set; }

    [JsonPropertyName("findings")]
    public List<DriftFinding> Findings { get; set; } = new();

    /// <summary>Explicitly states whether repair is currently possible, and why not.</summary>
    [JsonPropertyName("repair_available")]
    public bool RepairAvailable { get; set; }

    [JsonPropertyName("repair_unavailable_reason")]
    public string RepairUnavailableReason { get; set; } = string.Empty;

    /// <summary>
    /// AUDIT W09: paths that look like vault assets but have no registration, so nothing was
    /// actually verified for them. Without this, a report with drifted_count = 0 reads as
    /// "everything is healthy" when in truth nothing was examined.
    /// </summary>
    [JsonPropertyName("unverified_paths")]
    public List<string> UnverifiedPaths { get; set; } = new();

    /// <summary>True only when every candidate path was actually audited.</summary>
    [JsonPropertyName("coverage_complete")]
    public bool CoverageComplete { get; set; } = true;

    /// <summary>True only when coverage was complete AND every audited registration is healthy.</summary>
    [JsonPropertyName("all_verified_healthy")]
    public bool AllVerifiedHealthy { get; set; }
}

/// <summary>
/// Pure drift auditing. Takes the registrations as a parameter and persists nothing, so
/// tests and callers can reason about drift without touching production state (AUDIT W02).
/// </summary>
public static class DriftDiagnostics
{
    /// <summary>
    /// Audits only the registrations. Coverage is reported as complete because the caller supplied
    /// the whole set it knows about; use the overload below when candidate paths are also known.
    /// </summary>
    public static DriftAuditReport Audit(CapabilityPolicy policy, List<VaultRegistration> registrations)
    {
        var report = AuditCore(policy, registrations);
        report.CoverageComplete = true;
        report.AllVerifiedHealthy = report.DriftedCount == 0;
        return report;
    }

    /// <summary>
    /// AUDIT W09: audits the registrations and additionally reports candidate paths that have no
    /// registration at all. A path with no record is NOT healthy - it is unverified, and the
    /// report says so instead of returning silence.
    /// </summary>
    public static DriftAuditReport Audit(
        CapabilityPolicy policy,
        List<VaultRegistration> registrations,
        IEnumerable<string> candidatePaths)
    {
        var report = AuditCore(policy, registrations);

        var registered = registrations
            .Select(r => r.VirtualAnchorPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!registered.Contains(Normalize(candidate)))
            {
                report.UnverifiedPaths.Add(candidate);
            }
        }

        report.CoverageComplete = report.UnverifiedPaths.Count == 0;
        report.AllVerifiedHealthy = report.CoverageComplete && report.DriftedCount == 0;

        if (!report.CoverageComplete)
        {
            // Repair must not be offered on the strength of an incomplete audit.
            report.RepairAvailable = false;
            report.RepairUnavailableReason =
                $"有 {report.UnverifiedPaths.Count} 个候选路径没有登记记录，未做任何校验；"
                + "在补齐记录或明确排除之前，不能据此判断资产健康。";
        }

        return report;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return path.TrimEnd('\\');
        }
    }

    private static DriftAuditReport AuditCore(CapabilityPolicy policy, List<VaultRegistration> registrations)
    {
        var report = new DriftAuditReport { TotalRegistrations = registrations.Count };

        foreach (var reg in registrations)
        {
            var finding = InspectOne(reg);
            reg.LastVerifiedAt = DateTime.UtcNow;
            reg.IsJunctionIntact = finding.Kind == DriftKind.Healthy;
            reg.DriftDetected = finding.Kind != DriftKind.Healthy;
            reg.DriftDetails = finding.Details;

            report.Findings.Add(finding);
            if (reg.DriftDetected)
            {
                report.DriftedCount++;
            }
        }

        var heal = policy.Check(Capability.DriftAutoHeal);
        report.RepairAvailable = heal.IsAllowed;
        if (!heal.IsAllowed)
        {
            report.RepairUnavailableReason = heal.Reason;
        }

        return report;
    }

    internal static DriftFinding InspectOne(VaultRegistration reg)
    {
        var finding = new DriftFinding
        {
            RegistrationId = reg.Id,
            AssetName = reg.AssetName,
            Kind = DriftKind.Healthy
        };

        bool anchorExists = Directory.Exists(reg.VirtualAnchorPath);
        bool targetExists = Directory.Exists(reg.PhysicalVaultPath);

        finding.ObservedFacts.Add($"锚点 {reg.VirtualAnchorPath}：{(anchorExists ? "存在" : "不存在")}");
        finding.ObservedFacts.Add($"仓库 {reg.PhysicalVaultPath}：{(targetExists ? "存在" : "不存在")}");

        if (!targetExists)
        {
            finding.Kind = DriftKind.TargetMissing;
            finding.Details = "物理仓库路径当前不可访问（可能磁盘离线或目录被移除）。";
            finding.RecommendedAction = "先恢复仓库所在卷再重新检查；不要把该状态当作残留清理。";
            return finding;
        }

        if (!anchorExists)
        {
            finding.Kind = DriftKind.AnchorMissing;
            finding.Details = "原路径联接点已不存在，上游写入可能已无处可去。";
            finding.RecommendedAction = "确认应用实际写入位置后，再评估重建联接。";
            return finding;
        }

        if (!FastDirectorySizer.IsReparsePoint(reg.VirtualAnchorPath))
        {
            finding.Kind = DriftKind.AnchorReplaced;
            finding.Details = "原路径已变回普通目录（存在未走联接的写入）。";
            finding.RecommendedAction = "需要先保全两侧数据并比对差异，不能按时间戳直接覆盖。";
            return finding;
        }

        var junction = FastDirectorySizer.GetJunctionInfo(reg.VirtualAnchorPath);
        string observed = PathIdentity.Normalize(junction.TargetPath);
        string expected = PathIdentity.Normalize(reg.PhysicalVaultPath);
        finding.ObservedFacts.Add($"联接指向：{observed}");

        if (!string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase))
        {
            finding.Kind = DriftKind.JunctionRetargeted;
            finding.Details = $"联接指向与登记不符：期望 {expected}，实际 {observed}。";
            finding.RecommendedAction = "先确认哪个目标包含最新数据，再由人工决定合并方向。";
            return finding;
        }

        finding.Details = "联接与仓库一致。";
        finding.RecommendedAction = "无需操作。";
        return finding;
    }
}

/// <summary>
/// Watches vault anchors. Under W01 the watchdog is diagnose-only: it reports facts and
/// refuses to merge or delete anything, because the previous auto-heal destroyed the data
/// of one side on every timestamp tie (AUDIT A06).
/// </summary>
public static class DriftWatchdog
{
    public static DriftAuditReport InspectAndAuditDrifts(CapabilityPolicy policy)
    {
        var registrations = AssetVaultEngine.GetActiveRegistrations();
        var report = DriftDiagnostics.Audit(policy, registrations);
        AssetVaultEngine.SaveRegistrations(registrations);
        return report;
    }

    /// <summary>
    /// Refuses to run. The previous implementation copied by timestamp and then deleted the
    /// drifted directory, which lost one of two divergent versions (AUDIT A06) and could
    /// overwrite a newer vault copy with an older anchor copy.
    /// </summary>
    public static OperationOutcome RepairDrift(CapabilityPolicy policy, string registrationId)
    {
        var decision = policy.Check(Capability.DriftAutoHeal);
        if (!decision.IsAllowed)
        {
            return OperationOutcome.Blocked(Capability.DriftAutoHeal, decision.Reason);
        }

        // Reachable only once the W09 conflict-preserving merge is implemented and verified.
        return OperationOutcome.Unsupported(Capability.DriftAutoHeal,
            "冲突保全式漂移修复尚未实现：需要双版本隔离与哈希证据，未做任何修改。");
    }
}
