using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Models;

public class SafetyReport
{
    [JsonPropertyName("can_uninstall")]
    public bool CanUninstall { get; set; } = true;

    [JsonPropertyName("block_reason")]
    public string BlockReason { get; set; } = string.Empty;

    [JsonPropertyName("is_critical_system")]
    public bool IsCriticalSystem { get; set; } = false;

    [JsonPropertyName("is_protected_infra")]
    public bool IsProtectedInfra { get; set; } = false;

    [JsonPropertyName("allow_override")]
    public bool AllowOverride { get; set; } = true;

    [JsonPropertyName("cascade_impacts")]
    public List<string> CascadeImpacts { get; set; } = new();

    [JsonPropertyName("prerequisite_advice")]
    public string PrerequisiteAdvice { get; set; } = string.Empty;

    [JsonPropertyName("dependent_apps")]
    public List<string> DependentApps { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("preserved_ai_assets")]
    public List<string> PreservedAiAssets { get; set; } = new();

    [JsonPropertyName("residue_paths")]
    public List<string> ResiduePaths { get; set; } = new();

    [JsonPropertyName("registry_backup_path")]
    public string RegistryBackupPath { get; set; } = string.Empty;

    [JsonPropertyName("restore_point_status")]
    public string RestorePointStatus { get; set; } = string.Empty;
}

public class JunctionInfo
{
    public bool IsJunction { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
