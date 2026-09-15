using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;

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

public static class AssetVaultEngine
{
    private static readonly string RegistryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"AppAssetSentinel\vault_registry.json");

    private static readonly Dictionary<string, string> KnownEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ollama", "OLLAMA_MODELS" },
        { "huggingface", "HF_HOME" },
        { "torch", "TORCH_HOME" },
        { "uv", "UV_CACHE_DIR" }
    };

    public static List<VaultRegistration> GetActiveRegistrations()
    {
        if (!File.Exists(RegistryFile)) return new List<VaultRegistration>();

        try
        {
            var json = File.ReadAllText(RegistryFile);
            return JsonSerializer.Deserialize<List<VaultRegistration>>(json) ?? new List<VaultRegistration>();
        }
        catch
        {
            return new List<VaultRegistration>();
        }
    }

    public static void SaveRegistrations(List<VaultRegistration> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(RegistryFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(RegistryFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Executes Relocation with Dual-Lock Anti-Drift:
    /// Lock 1: Kernel NTFS Junction Point
    /// Lock 2: Windows User Environment Variable Lock
    /// </summary>
    public static async Task<MigrationTask> RelocateAndDualLockAsync(
        string sourceDir,
        string targetVaultDir,
        string assetName,
        string category,
        IProgress<(long bytesMigrated, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        sourceDir = Path.GetFullPath(sourceDir).TrimEnd('\\');
        targetVaultDir = Path.GetFullPath(targetVaultDir).TrimEnd('\\');

        string targetParent = Path.GetDirectoryName(targetVaultDir) ?? @"D:\AIStack_Vault";
        string targetFolderName = Path.GetFileName(targetVaultDir);

        // 1. Perform atomic copy and create Junction
        var task = await JunctionEngine.MigrateDirectoryAsync(
            sourceDir,
            targetParent,
            assetName: assetName,
            progress: progress,
            cancellationToken: ct
        );

        if (task.Status != MigrationStatus.Completed)
        {
            return task;
        }

        // 2. Perform Lock 2: Sync Windows User Environment Variable if applicable
        string matchedEnv = "";
        foreach (var (key, envVar) in KnownEnvironmentVariables)
        {
            if (assetName.Contains(key, StringComparison.OrdinalIgnoreCase) || sourceDir.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Environment.SetEnvironmentVariable(envVar, task.TargetPath, EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable(envVar, task.TargetPath, EnvironmentVariableTarget.Process);
                    matchedEnv = $"{envVar}={task.TargetPath}";
                }
                catch { }
                break;
            }
        }

        // 3. Register with Vault Watchdog
        var registrations = GetActiveRegistrations();
        registrations.RemoveAll(r => r.VirtualAnchorPath.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));

        registrations.Add(new VaultRegistration
        {
            AssetName = assetName,
            Category = category,
            VirtualAnchorPath = sourceDir,
            PhysicalVaultPath = task.TargetPath,
            SyncedEnvVar = matchedEnv,
            IsJunctionIntact = true,
            DriftDetected = false,
            CreatedAt = DateTime.UtcNow,
            LastVerifiedAt = DateTime.UtcNow
        });

        SaveRegistrations(registrations);

        task.StatusMessage = $"双向锁死归仓成功！已建立 NTFS 目录联接并同步环境锁定：{matchedEnv}";
        return task;
    }
}
