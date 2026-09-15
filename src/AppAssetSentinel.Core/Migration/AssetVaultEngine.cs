using System.Text.Json;
using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Policy;

namespace AppAssetSentinel.Core.Migration;

public class VaultRegistration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("virtual_anchor_path")]
    public string VirtualAnchorPath { get; set; } = string.Empty;

    [JsonPropertyName("physical_vault_path")]
    public string PhysicalVaultPath { get; set; } = string.Empty;

    [JsonPropertyName("synced_env_var")]
    public string SyncedEnvVar { get; set; } = string.Empty;

    /// <summary>Value that existed before we touched the variable, so it can be restored (A19).</summary>
    [JsonPropertyName("env_var_previous_value")]
    public string? EnvVarPreviousValue { get; set; }

    [JsonPropertyName("env_var_was_set")]
    public bool EnvVarWasSet { get; set; }

    [JsonPropertyName("is_junction_intact")]
    public bool IsJunctionIntact { get; set; } = true;

    [JsonPropertyName("drift_detected")]
    public bool DriftDetected { get; set; } = false;

    [JsonPropertyName("drift_details")]
    public string DriftDetails { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("last_verified_at")]
    public DateTime LastVerifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Result of loading the registry file, so corruption is visible (AUDIT A18).</summary>
public sealed class VaultRegistryLoadResult
{
    public bool Succeeded { get; init; }
    public bool FileMissing { get; init; }
    public string Error { get; init; } = string.Empty;
    public List<VaultRegistration> Registrations { get; init; } = new();
}

/// <summary>
/// Persistence for vault registrations. Writes are atomic (temp file + move) and failures
/// are surfaced instead of swallowed (AUDIT A18). A corrupt file is reported as corruption
/// rather than being silently treated as "no registrations".
/// </summary>
public static class VaultRegistry
{
    private static readonly object Gate = new();

    public static string DefaultRegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"AppAssetSentinel\vault_registry.json");

    public static VaultRegistryLoadResult Load(string? registryPath = null)
    {
        string path = registryPath ?? DefaultRegistryPath;

        if (!File.Exists(path))
        {
            return new VaultRegistryLoadResult { Succeeded = true, FileMissing = true };
        }

        try
        {
            string json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<List<VaultRegistration>>(json);
            if (parsed == null)
            {
                return new VaultRegistryLoadResult
                {
                    Succeeded = false,
                    Error = "登记文件内容为 null，判定为损坏。"
                };
            }

            return new VaultRegistryLoadResult { Succeeded = true, Registrations = parsed };
        }
        catch (JsonException ex)
        {
            return new VaultRegistryLoadResult
            {
                Succeeded = false,
                Error = $"登记文件 JSON 损坏：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new VaultRegistryLoadResult { Succeeded = false, Error = ex.Message };
        }
    }

    /// <summary>Atomically persists the registry. Throws on failure so callers cannot claim success.</summary>
    public static void Save(List<VaultRegistration> registrations, string? registryPath = null)
    {
        string path = registryPath ?? DefaultRegistryPath;
        string? dir = Path.GetDirectoryName(path);

        lock (Gate)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(registrations,
                new JsonSerializerOptions { WriteIndented = true });

            string temp = path + ".tmp";
            File.WriteAllText(temp, json);

            // Atomic replace: a crash here leaves the previous good file untouched.
            File.Move(temp, path, overwrite: true);
        }
    }
}

/// <summary>
/// Relocation entry point. Under the R0 capability gate (AUDIT W01) relocation is Blocked,
/// so this returns an explicit Blocked outcome with no side effects. It is re-opened only
/// after the W07 migration-kernel acceptance gate.
/// </summary>
public static class AssetVaultEngine
{
    private static readonly Dictionary<string, string> KnownEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ollama", "OLLAMA_MODELS" },
        { "huggingface", "HF_HOME" },
        { "torch", "TORCH_HOME" },
        { "uv", "UV_CACHE_DIR" }
    };

    public static List<VaultRegistration> GetActiveRegistrations(string? registryPath = null)
    {
        var load = VaultRegistry.Load(registryPath);
        return load.Succeeded ? load.Registrations : new List<VaultRegistration>();
    }

    public static void SaveRegistrations(List<VaultRegistration> list, string? registryPath = null)
    {
        VaultRegistry.Save(list, registryPath);
    }

    public static string? MatchEnvironmentVariable(string assetName, string sourcePath)
    {
        foreach (var (key, envVar) in KnownEnvironmentVariables)
        {
            if (assetName.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return envVar;
            }
        }

        return null;
    }

    public static OperationOutcome RelocateAndDualLock(
        CapabilityPolicy policy,
        string sourceDir,
        string targetVaultDir,
        string assetName,
        string category)
    {
        var decision = policy.Check(Capability.VaultRelocate);
        if (!decision.IsAllowed)
        {
            return OperationOutcome.Blocked(Capability.VaultRelocate,
                $"{decision.Reason}（源 {sourceDir}，目标 {targetVaultDir}；未做任何修改。）");
        }

        return OperationOutcome.Unsupported(Capability.VaultRelocate,
            $"迁移内核尚未通过 W07 验收（源 {sourceDir}，目标 {targetVaultDir}），未做任何修改。");
    }
}
