using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Models;

public class RedundancyInfo
{
    [JsonPropertyName("has_redundancy")]
    public bool HasRedundancy { get; set; } = false;

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; } = false;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "primary"; // primary, secondary

    [JsonPropertyName("sibling_count")]
    public int SiblingCount { get; set; } = 0;

    [JsonPropertyName("tip")]
    public string Tip { get; set; } = string.Empty;
}

public class SuiteInfo
{
    [JsonPropertyName("component_count")]
    public int ComponentCount { get; set; } = 0;

    [JsonPropertyName("components")]
    public List<string> Components { get; set; } = new();

    [JsonPropertyName("tip")]
    public string Tip { get; set; } = string.Empty;
}

public class SxsInfo
{
    [JsonPropertyName("tip")]
    public string Tip { get; set; } = string.Empty;
}

public class SoftwareAsset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("display_version")]
    public string DisplayVersion { get; set; } = string.Empty;

    [JsonPropertyName("install_location")]
    public string InstallLocation { get; set; } = string.Empty;

    [JsonPropertyName("uninstall_string")]
    public string UninstallString { get; set; } = string.Empty;

    [JsonPropertyName("quiet_uninstall_string")]
    public string QuietUninstallString { get; set; } = string.Empty;

    [JsonPropertyName("install_date")]
    public string InstallDate { get; set; } = string.Empty;

    [JsonPropertyName("estimated_size_bytes")]
    public long EstimatedSizeBytes { get; set; } = 0;

    /// <summary>AUDIT A22: false when the size walk could not read every entry.</summary>
    [JsonPropertyName("size_measurement_complete")]
    public bool SizeMeasurementComplete { get; set; } = true;

    [JsonPropertyName("size_skips")]
    public long SizeSkips { get; set; } = 0;

    [JsonPropertyName("registry_key_path")]
    public string RegistryKeyPath { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = "x64"; // x64, x86, arm64

    [JsonPropertyName("heat_level")]
    public string HeatLevel { get; set; } = "unknown"; // hot, warm, cooling, zombie, infrastructure, unknown

    [JsonPropertyName("heat_score")]
    public double HeatScore { get; set; } = 0.0;

    [JsonPropertyName("days_since_last_use")]
    public int? DaysSinceLastUse { get; set; } = null;

    [JsonPropertyName("last_used_timestamp")]
    public string LastUsedTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("telemetry_source")]
    public string TelemetrySource { get; set; } = string.Empty;

    /// <summary>
    /// AUDIT A13: how strongly the usage claim is supported.
    /// confirmed = observed live activity; inferred = indirect file evidence;
    /// unknown = nothing was observed, which must never be reported as "abandoned".
    /// </summary>
    /// <summary>
    /// AUDIT W05: how this application relates to Python. Embedded means it owns the runtime;
    /// ExternalShared means the runtime belongs to other software too. Collapsing the two into a
    /// single "uses python" flag would let a shared runtime be treated as this app's residue.
    /// </summary>
    [JsonPropertyName("python_relationship")]
    public string PythonRelationship { get; set; } = "none";

    [JsonPropertyName("python_runtime_owned")]
    public bool PythonRuntimeOwned { get; set; }

    [JsonPropertyName("usage_confidence")]
    public string UsageConfidence { get; set; } = "unknown";

    /// <summary>Evidence kinds actually observed, so the UI can explain the basis.</summary>
    [JsonPropertyName("usage_evidence")]
    public List<string> UsageEvidence { get; set; } = new();

    [JsonPropertyName("is_running")]
    public bool IsRunning { get; set; } = false;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "tools_utility";

    [JsonPropertyName("asset_type")]
    public string AssetType { get; set; } = "gui_app"; // gui_app, runtime_environment, sdk_toolchain, hardware_driver, system_service

    [JsonPropertyName("is_protected")]
    public bool IsProtected { get; set; } = false;

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = "regular_app"; // regular_app, system_infra, dev_runtime, ai_engine

    [JsonPropertyName("plain_role")]
    public string PlainRole { get; set; } = string.Empty;

    [JsonPropertyName("core_usage")]
    public string CoreUsage { get; set; } = string.Empty;

    [JsonPropertyName("ecosystem")]
    public string Ecosystem { get; set; } = string.Empty;

    [JsonPropertyName("dependency_chain")]
    public string DependencyChain { get; set; } = string.Empty;

    [JsonPropertyName("purpose_summary")]
    public string PurposeSummary { get; set; } = string.Empty;

    [JsonPropertyName("main_executable")]
    public string MainExecutable { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("ai_asset_paths")]
    public List<string> AiAssetPaths { get; set; } = new();

    [JsonPropertyName("is_junction")]
    public bool IsJunction { get; set; } = false;

    [JsonPropertyName("junction_target")]
    public string JunctionTarget { get; set; } = string.Empty;

    [JsonPropertyName("is_portable")]
    public bool IsPortable { get; set; } = false;

    [JsonPropertyName("is_ghost_entry")]
    public bool IsGhostEntry { get; set; } = false;

    [JsonPropertyName("ghost_reason")]
    public string GhostReason { get; set; } = string.Empty;

    [JsonPropertyName("redundancy_info")]
    public RedundancyInfo? RedundancyInfo { get; set; }

    [JsonPropertyName("suite_info")]
    public SuiteInfo? SuiteInfo { get; set; }

    [JsonPropertyName("sxs_info")]
    public SxsInfo? SxsInfo { get; set; }
}
