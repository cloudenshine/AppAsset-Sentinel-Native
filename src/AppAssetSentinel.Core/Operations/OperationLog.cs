using System.Collections.Concurrent;
using System.Text.Json;

namespace AppAssetSentinel.Core.Operations;

public sealed class OperationLogLoadResult
{
    public bool Succeeded { get; init; }
    public bool FileMissing { get; init; }
    public string Error { get; init; } = string.Empty;
    public OperationRecord? Record { get; init; }
}

/// <summary>
/// AUDIT W06: the write-ahead log. A record is persisted *before* the corresponding file
/// system action, so an abrupt termination always leaves a readable statement of what was
/// attempted. Recovery decisions read this log rather than trusting an in-memory "Completed".
///
/// The log is also the mutual-exclusion mechanism: a second task targeting an overlapping
/// resource is refused while an unfinished record holds it.
/// </summary>
public sealed class OperationLog
{
    private static readonly object FileGate = new();
    private static readonly ConcurrentDictionary<string, byte> ActiveResources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;

    public OperationLog(string directory)
    {
        _directory = directory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"AppAssetSentinel\operations");

    public string PathFor(string taskId) => Path.Combine(_directory, $"{Sanitize(taskId)}.json");

    // -----------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------

    /// <summary>Atomically persists the record. Throws on failure so the caller cannot proceed blind.</summary>
    public void Save(OperationRecord record)
    {
        record.UpdatedAt = DateTime.UtcNow;

        lock (FileGate)
        {
            Directory.CreateDirectory(_directory);

            string target = PathFor(record.TaskId);
            string temp = target + ".tmp";

            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json);

            // Atomic replace: an interruption here leaves the previous good record readable.
            File.Move(temp, target, overwrite: true);
        }
    }

    public OperationLogLoadResult Load(string taskId)
    {
        string path = PathFor(taskId);

        if (!File.Exists(path))
        {
            return new OperationLogLoadResult { Succeeded = true, FileMissing = true };
        }

        try
        {
            string json = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize<OperationRecord>(json);

            return record == null
                ? new OperationLogLoadResult { Succeeded = false, Error = "操作记录内容为空，判定为损坏。" }
                : new OperationLogLoadResult { Succeeded = true, Record = record };
        }
        catch (JsonException ex)
        {
            return new OperationLogLoadResult { Succeeded = false, Error = $"操作记录 JSON 损坏：{ex.Message}" };
        }
        catch (Exception ex)
        {
            return new OperationLogLoadResult { Succeeded = false, Error = ex.Message };
        }
    }

    public List<OperationRecord> LoadAll()
    {
        var results = new List<OperationRecord>();

        if (!Directory.Exists(_directory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<OperationRecord>(File.ReadAllText(file));
                if (record != null)
                {
                    results.Add(record);
                }
            }
            catch
            {
                // A damaged file is skipped here but still surfaces via Load(taskId).
            }
        }

        return results.OrderByDescending(r => r.CreatedAt).ToList();
    }

    // -----------------------------------------------------------------
    // Resource mutual exclusion (AUDIT W06: 重叠资源互斥)
    // -----------------------------------------------------------------

    /// <summary>
    /// Claims every resource a task touches. Returns false with the offending resource when
    /// another unfinished task already holds one, so two operations cannot race on the same paths.
    /// </summary>
    public static bool TryClaimResources(IEnumerable<string> resources, out string conflict)
    {
        conflict = string.Empty;
        var claimed = new List<string>();

        foreach (var resource in resources)
        {
            string key = NormalizeResource(resource);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!ActiveResources.TryAdd(key, 0))
            {
                foreach (var c in claimed)
                {
                    ActiveResources.TryRemove(c, out _);
                }

                conflict = resource;
                return false;
            }

            claimed.Add(key);
        }

        return true;
    }

    public static void ReleaseResources(IEnumerable<string> resources)
    {
        foreach (var resource in resources)
        {
            string key = NormalizeResource(resource);
            if (!string.IsNullOrEmpty(key))
            {
                ActiveResources.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Resources still held by unfinished records. Used at startup so a resumed process can
    /// reconstruct which paths are mid-operation.
    /// </summary>
    public static List<string> HeldByState(OperationState state) => state switch
    {
        OperationState.Copying or OperationState.Copied or OperationState.Verified
            or OperationState.Switched or OperationState.NeedsAttention => new List<string>(),
        _ => new List<string>()
    };

    private static string NormalizeResource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Sanitize(string taskId)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (char c in taskId)
        {
            buffer.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return buffer.Length == 0 ? "task" : buffer.ToString();
    }
}
