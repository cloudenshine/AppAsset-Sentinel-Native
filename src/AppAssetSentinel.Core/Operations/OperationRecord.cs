using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Adapters;

namespace AppAssetSentinel.Core.Operations;

/// <summary>
/// AUDIT W06: the state a relocation task is really in. Every value is derivable from the
/// write-ahead log, so a crash between steps can be classified on the next start instead of
/// being guessed from cache. Only <see cref="Committed"/> means the user's data moved.
/// </summary>
public enum OperationState
{
    /// <summary>Log written; nothing on disk has been touched yet.</summary>
    Planned,

    /// <summary>Boundary/identity/precondition checks failed. No side effects.</summary>
    PreflightFailed,

    /// <summary>Copying into the task-owned staging directory.</summary>
    Copying,

    /// <summary>Copy finished; verification not yet performed.</summary>
    Copied,

    /// <summary>Manifest/derived checks failed. Source untouched; staging is disposable.</summary>
    VerifyFailed,

    /// <summary>Content verified against the source manifest.</summary>
    Verified,

    /// <summary>Switching failed. Rollback attempted; see <see cref="OperationRecord.RecoveryNote"/>.</summary>
    SwitchFailed,

    /// <summary>
    /// Source renamed to its backup path and the anchor was re-linked. This is the only
    /// state where the layout changed but source space is NOT yet reclaimed.
    /// </summary>
    Switched,

    /// <summary>Partially applied in a way a human must resolve. Nothing is auto-deleted.</summary>
    NeedsAttention,

    /// <summary>Rolled back to the original layout; original data intact.</summary>
    FailedRecoverable,

    /// <summary>Switch confirmed and the backup disposition was decided by a separate authorized step.</summary>
    Committed
}

/// <summary>What should happen to the source backup once the switch is confirmed.</summary>
public enum BackupDisposition
{
    /// <summary>Keep the backup in place. The default: space is not reclaimed yet.</summary>
    Retained,

    /// <summary>Remove the backup after a retention window, reclaiming the space.</summary>
    RecycledAfterRetention,

    /// <summary>Never remove it automatically.</summary>
    ManualOnly
}

public sealed class OperationRecord
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>The exact path the user confirmed. Never silently rewritten (AUDIT A10).</summary>
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Task-owned staging directory, created new for this task only (AUDIT A03).</summary>
    [JsonPropertyName("staging_path")]
    public string StagingPath { get; set; } = string.Empty;

    /// <summary>Where the source is parked while the anchor is re-linked.</summary>
    [JsonPropertyName("source_backup_path")]
    public string SourceBackupPath { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperationState State { get; set; } = OperationState.Planned;

    /// <summary>AUDIT W08: which mechanism actually moved the data.</summary>
    [JsonPropertyName("mechanism")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RelocationMechanism Mechanism { get; set; } = RelocationMechanism.JunctionCompat;

    [JsonPropertyName("backup_disposition")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupDisposition BackupDisposition { get; set; } = BackupDisposition.Retained;

    // --- identity of the resources, captured at plan time ---

    /// <summary>Resolved real source path (reparse points followed) recorded during preflight.</summary>
    [JsonPropertyName("source_identity")]
    public string SourceIdentity { get; set; } = string.Empty;

    [JsonPropertyName("target_identity")]
    public string TargetIdentity { get; set; } = string.Empty;

    /// <summary>Expected content manifest captured before copying, for the verification step.</summary>
    [JsonPropertyName("expected_file_count")]
    public int ExpectedFileCount { get; set; }

    [JsonPropertyName("expected_total_bytes")]
    public long ExpectedTotalBytes { get; set; }

    [JsonPropertyName("expected_manifest_complete")]
    public bool ExpectedManifestComplete { get; set; }

    /// <summary>Environment variable and its pre-change value, so a config change is reversible.</summary>
    [JsonPropertyName("config_variable")]
    public string ConfigVariable { get; set; } = string.Empty;

    [JsonPropertyName("config_previous_value")]
    public string? ConfigPreviousValue { get; set; }

    [JsonPropertyName("config_was_set")]
    public bool ConfigWasSet { get; set; }

    [JsonPropertyName("config_applied_value")]
    public string ConfigAppliedValue { get; set; } = string.Empty;

    // --- bookkeeping ---

    [JsonPropertyName("conflicts")]
    public List<string> Conflicts { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<OperationStep> Steps { get; set; } = new();

    [JsonPropertyName("recovery_note")]
    public string RecoveryNote { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public void AddStep(string step, string detail)
    {
        Steps.Add(new OperationStep
        {
            Step = step,
            Detail = detail,
            AtUtc = DateTime.UtcNow
        });
        UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class OperationStep
{
    [JsonPropertyName("step")]
    public string Step { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("at_utc")]
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
