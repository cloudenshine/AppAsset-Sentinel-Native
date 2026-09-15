using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Models;

public enum MigrationStatus
{
    Pending,
    Scanning,
    Copying,
    Verifying,
    CreatingJunction,
    Completed,
    RollingBack,
    Failed
}

public class MigrationTask
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("software_id")]
    public string SoftwareId { get; set; } = string.Empty;

    [JsonPropertyName("software_name")]
    public string SoftwareName { get; set; } = string.Empty;

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; } = 0;

    [JsonPropertyName("migrated_bytes")]
    public long MigratedBytes { get; set; } = 0;

    [JsonPropertyName("file_count")]
    public int FileCount { get; set; } = 0;

    [JsonPropertyName("status")]
    public MigrationStatus Status { get; set; } = MigrationStatus.Pending;

    [JsonPropertyName("status_message")]
    public string StatusMessage { get; set; } = string.Empty;

    [JsonPropertyName("created_junction_path")]
    public string CreatedJunctionPath { get; set; } = string.Empty;

    [JsonPropertyName("backup_source_path")]
    public string BackupSourcePath { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
