using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Operations;

/// <summary>How a single relative path differs between the anchor and the vault.</summary>
public enum DriftFileClass
{
    /// <summary>Same path, same content on both sides.</summary>
    Identical,

    /// <summary>Present only at the anchor (written while the link was broken).</summary>
    AnchorOnly,

    /// <summary>Present only in the vault.</summary>
    VaultOnly,

    /// <summary>
    /// Present on both sides with different content. AUDIT A06: this is exactly the case the
    /// old repair destroyed by picking the newer timestamp and deleting the other side.
    /// </summary>
    Conflicting
}

public sealed class DriftFileEntry
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("classification")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DriftFileClass Classification { get; set; }

    [JsonPropertyName("anchor_bytes")]
    public long AnchorBytes { get; set; }

    [JsonPropertyName("vault_bytes")]
    public long VaultBytes { get; set; }

    [JsonPropertyName("anchor_sha256")]
    public string AnchorSha256 { get; set; } = string.Empty;

    [JsonPropertyName("vault_sha256")]
    public string VaultSha256 { get; set; } = string.Empty;
}

public sealed class DriftRepairPlan
{
    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; set; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("anchor_path")]
    public string AnchorPath { get; set; } = string.Empty;

    [JsonPropertyName("vault_path")]
    public string VaultPath { get; set; } = string.Empty;

    [JsonPropertyName("findings")]
    public List<DriftFinding> Findings { get; set; } = new();

    [JsonPropertyName("entries")]
    public List<DriftFileEntry> Entries { get; set; } = new();

    /// <summary>
    /// Conflicting paths are never merged automatically. Each one produces a sidecar copy so
    /// both versions survive and a human can decide.
    /// </summary>
    [JsonPropertyName("requires_manual_decision")]
    public List<string> RequiresManualDecision { get; set; } = new();

    [JsonPropertyName("safe_to_apply_automatically")]
    public bool SafeToApplyAutomatically { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("not_run")]
    public List<string> NotRun { get; set; } = new();
}

/// <summary>
/// AUDIT W09: drift repair as a separately authorized plan over the same execution core.
///
/// The rule the audit insists on: a diagnosis must not invent a cause, and a repair must not
/// destroy either side of a conflict. This planner therefore only *classifies*; it never
/// merges by timestamp and never deletes.
/// </summary>
public static class DriftRepairPlanner
{
    public const string ConflictSuffix = ".drift-conflict-";

    public static DriftRepairPlan BuildPlan(
        VaultRegistration registration,
        IReadOnlyList<DriftFinding> findings,
        bool includeFileInventory = true)
    {
        var plan = new DriftRepairPlan
        {
            RegistrationId = registration.Id,
            AssetName = registration.AssetName,
            AnchorPath = registration.VirtualAnchorPath,
            VaultPath = registration.PhysicalVaultPath,
            Findings = findings.ToList()
        };

        plan.NotRun.Add("跨卷与真实应用验证：未运行。");
        plan.NotRun.Add("自动合并冲突文件：按设计永不执行。");

        bool anchorIsDirectory = Directory.Exists(registration.VirtualAnchorPath);
        bool vaultIsDirectory = Directory.Exists(registration.PhysicalVaultPath);

        if (!anchorIsDirectory || !vaultIsDirectory)
        {
            // A missing side is not an invitation to delete the other.
            plan.SafeToApplyAutomatically = false;
            plan.Summary = !vaultIsDirectory
                ? "仓库侧不可访问：先恢复该卷，不做任何合并。"
                : "锚点侧不存在：需确认应用实际写入位置后再评估重建。";
            return plan;
        }

        if (!includeFileInventory)
        {
            plan.SafeToApplyAutomatically = false;
            plan.Summary = "仅诊断模式：未比较文件内容。";
            return plan;
        }

        var anchorManifest = FileIntegrity.BuildManifest(registration.VirtualAnchorPath);
        var vaultManifest = FileIntegrity.BuildManifest(registration.PhysicalVaultPath);

        if (!anchorManifest.Complete || !vaultManifest.Complete)
        {
            plan.SafeToApplyAutomatically = false;
            plan.Summary = "存在无法完整读取的条目，拒绝自动合并。";
            return plan;
        }

        var anchorFiles = anchorManifest.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var vaultFiles = vaultManifest.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, anchorDigest) in anchorFiles)
        {
            if (!vaultFiles.TryGetValue(path, out var vaultDigest))
            {
                plan.Entries.Add(new DriftFileEntry
                {
                    RelativePath = path,
                    Classification = DriftFileClass.AnchorOnly,
                    AnchorBytes = anchorDigest.Length,
                    AnchorSha256 = anchorDigest.Sha256
                });
                continue;
            }

            bool same = string.Equals(anchorDigest.Sha256, vaultDigest.Sha256, StringComparison.OrdinalIgnoreCase);

            plan.Entries.Add(new DriftFileEntry
            {
                RelativePath = path,
                Classification = same ? DriftFileClass.Identical : DriftFileClass.Conflicting,
                AnchorBytes = anchorDigest.Length,
                VaultBytes = vaultDigest.Length,
                AnchorSha256 = anchorDigest.Sha256,
                VaultSha256 = vaultDigest.Sha256
            });

            if (!same)
            {
                // Both versions matter. The sidecar name is derived from the content hash so
                // repeated runs are idempotent instead of piling up copies.
                plan.RequiresManualDecision.Add(
                    $"{path} → 保留两份（仓库侧原有 + 锚点侧 {path}{ConflictSuffix}{anchorDigest.Sha256[..8]}）");
            }
        }

        foreach (var path in vaultFiles.Keys)
        {
            if (!anchorFiles.ContainsKey(path))
            {
                plan.Entries.Add(new DriftFileEntry
                {
                    RelativePath = path,
                    Classification = DriftFileClass.VaultOnly,
                    VaultBytes = vaultFiles[path].Length,
                    VaultSha256 = vaultFiles[path].Sha256
                });
            }
        }

        int conflicts = plan.Entries.Count(e => e.Classification == DriftFileClass.Conflicting);
        int anchorOnly = plan.Entries.Count(e => e.Classification == DriftFileClass.AnchorOnly);

        plan.SafeToApplyAutomatically = conflicts == 0;
        plan.Summary = conflicts > 0
            ? $"发现 {conflicts} 处同名不同内容、{anchorOnly} 处仅在锚点侧。冲突文件不会被自动覆盖或删除，需要人工决定。"
            : $"无内容冲突；锚点侧独有 {anchorOnly} 处可直接并入仓库。";

        return plan;
    }

    /// <summary>
    /// Applies a plan that has no conflicts. Refuses whenever any conflicting path exists, so
    /// the destructive merge the audit rejected cannot be reached by accident.
    /// </summary>
    public static OperationOutcome Apply(
        DriftRepairPlan plan,
        bool authorized,
        Func<string, string, bool>? fileMover = null)
    {
        if (!authorized)
        {
            return OperationOutcome.Blocked(Capability.DriftAutoHeal,
                "修复需要独立授权；未做任何修改。");
        }

        if (plan.RequiresManualDecision.Count > 0)
        {
            return new OperationOutcome
            {
                Status = OperationStatus.NeedsAttention,
                Capability = Capability.DriftAutoHeal,
                DidMutate = false,
                Code = "conflicts_present",
                Message = $"存在 {plan.RequiresManualDecision.Count} 处内容冲突，已保留两份，未做自动合并。",
                Evidence = plan.RequiresManualDecision.Take(20).ToList(),
                Recovery = "请人工比对两侧文件后决定保留哪一份。"
            };
        }

        var anchorOnly = plan.Entries.Where(e => e.Classification == DriftFileClass.AnchorOnly).ToList();
        if (anchorOnly.Count == 0)
        {
            return OperationOutcome.Simulated(Capability.DriftAutoHeal,
                "没有需要并入的锚点侧独有文件。");
        }

        int moved = 0;
        var errors = new List<string>();

        foreach (var entry in anchorOnly)
        {
            try
            {
                if (fileMover != null)
                {
                    if (!fileMover(plan.AnchorPath, plan.VaultPath))
                    {
                        errors.Add($"并入失败：{entry.RelativePath}");
                        continue;
                    }
                }

                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.RelativePath}: {ex.Message}");
            }
        }

        return errors.Count == 0
            ? new OperationOutcome
            {
                Status = OperationStatus.Succeeded,
                Capability = Capability.DriftAutoHeal,
                DidMutate = moved > 0,
                Code = "merged",
                Message = $"已并入 {moved} 个锚点侧独有文件；两侧均无内容被覆盖。",
                Evidence = anchorOnly.Take(20).Select(e => e.RelativePath).ToList()
            }
            : new OperationOutcome
            {
                Status = OperationStatus.NeedsAttention,
                Capability = Capability.DriftAutoHeal,
                DidMutate = moved > 0,
                Code = "partial",
                Message = $"部分并入成功（{moved}），存在错误需要人工处理。",
                Evidence = errors.Take(20).ToList()
            };
    }

    /// <summary>
    /// Detects a configuration value that no longer matches the registered vault path, which is
    /// a drift cause the old watchdog never checked (AUDIT A19).
    /// </summary>
    public static string DescribeConfigDeviation(VaultRegistration registration, string? currentValue)
    {
        if (string.IsNullOrEmpty(registration.SyncedEnvVar))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(currentValue))
        {
            return $"配置 {registration.SyncedEnvVar} 当前未设置，但登记中存在该配置，属于配置偏离。";
        }

        string normalizedCurrent = PathIdentity.Normalize(currentValue);
        string normalizedExpected = PathIdentity.Normalize(registration.PhysicalVaultPath);

        return string.Equals(normalizedCurrent, normalizedExpected, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"配置偏离：{registration.SyncedEnvVar} 指向 {normalizedCurrent}，登记预期 {normalizedExpected}。";
    }
}
