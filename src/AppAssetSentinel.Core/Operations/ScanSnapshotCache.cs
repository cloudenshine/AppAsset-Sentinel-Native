using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Operations;

/// <summary>What the inventory pipeline is doing right now.</summary>
public enum ScanPhase
{
    /// <summary>Nothing has run in this process yet and no usable cache exists.</summary>
    NeverScanned,

    /// <summary>A scan is running in the background.</summary>
    Scanning,

    /// <summary>A scan finished and the results are current.</summary>
    Completed,

    /// <summary>A scan failed. Previous results, if any, are still being served.</summary>
    Failed
}

public sealed class ScanStatus
{
    [JsonPropertyName("phase")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScanPhase Phase { get; set; } = ScanPhase.NeverScanned;

    /// <summary>True when the data being served came from disk rather than this run.</summary>
    [JsonPropertyName("serving_cached_snapshot")]
    public bool ServingCachedSnapshot { get; set; }

    [JsonPropertyName("asset_count")]
    public int AssetCount { get; set; }

    [JsonPropertyName("started_at_utc")]
    public DateTime? StartedAtUtc { get; set; }

    [JsonPropertyName("completed_at_utc")]
    public DateTime? CompletedAtUtc { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = string.Empty;

    [JsonPropertyName("is_in_progress")]
    public bool IsInProgress => Phase == ScanPhase.Scanning;
}

/// <summary>Result of loading a cached snapshot, so corruption is visible rather than silent.</summary>
public sealed class SnapshotLoadResult
{
    public bool Succeeded { get; init; }
    public bool FileMissing { get; init; }
    public string Error { get; init; } = string.Empty;
    public List<Models.SoftwareAsset> Assets { get; init; } = new();
    public DateTime? CapturedAtUtc { get; init; }
}

/// <summary>
/// AUDIT A28: the previous build ran a full scan before showing any window and then waited a
/// fixed 800 ms hoping the server was up. This snapshot lets the UI render the last known
/// inventory immediately while a fresh scan runs in the background, and gives the launcher a
/// real readiness signal instead of a sleep.
/// </summary>
public static class ScanSnapshotCache
{
    private static readonly object Gate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"AppAssetSentinel\inventory_snapshot.json");

    public sealed class SnapshotDocument
    {
        [JsonPropertyName("captured_at_utc")]
        public DateTime CapturedAtUtc { get; set; }

        [JsonPropertyName("asset_count")]
        public int AssetCount { get; set; }

        [JsonPropertyName("assets")]
        public List<Models.SoftwareAsset> Assets { get; set; } = new();
    }

    /// <summary>Atomically writes the snapshot. Throws on failure so callers cannot claim success.</summary>
    public static void Save(IEnumerable<Models.SoftwareAsset> assets, string? path = null)
    {
        string target = path ?? DefaultPath;
        string? dir = Path.GetDirectoryName(target);

        lock (Gate)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var document = new SnapshotDocument
            {
                CapturedAtUtc = DateTime.UtcNow,
                Assets = assets.ToList(),
                AssetCount = 0
            };
            document.AssetCount = document.Assets.Count;

            string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false });

            string temp = target + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, target, overwrite: true);
        }
    }

    public static SnapshotLoadResult Load(string? path = null)
    {
        string target = path ?? DefaultPath;

        if (!File.Exists(target))
        {
            return new SnapshotLoadResult { Succeeded = true, FileMissing = true };
        }

        try
        {
            var document = JsonSerializer.Deserialize<SnapshotDocument>(File.ReadAllText(target));
            if (document == null)
            {
                return new SnapshotLoadResult { Succeeded = false, Error = "快照内容为空，判定为损坏。" };
            }

            return new SnapshotLoadResult
            {
                Succeeded = true,
                Assets = document.Assets,
                CapturedAtUtc = document.CapturedAtUtc
            };
        }
        catch (JsonException ex)
        {
            return new SnapshotLoadResult { Succeeded = false, Error = $"快照 JSON 损坏：{ex.Message}" };
        }
        catch (Exception ex)
        {
            return new SnapshotLoadResult { Succeeded = false, Error = ex.Message };
        }
    }

    public static void Delete(string? path = null)
    {
        string target = path ?? DefaultPath;
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch { }
    }
}
