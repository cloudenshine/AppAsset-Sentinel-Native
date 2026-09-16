using System.Diagnostics;
using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Migration;

public class SystemVolume
{
    [JsonPropertyName("drive_letter")]
    public string DriveLetter { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("file_system")]
    public string FileSystem { get; set; } = "NTFS";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "未知介质 (探测未完成)";

    [JsonPropertyName("is_ssd")]
    public bool IsSSD { get; set; } = false;

    /// <summary>AUDIT A14: false when the physical-media probe did not report this volume.</summary>
    [JsonPropertyName("media_known")]
    public bool MediaKnown { get; set; } = false;

    [JsonPropertyName("bus_type")]
    public string BusType { get; set; } = "SATA";

    [JsonPropertyName("total_size_bytes")]
    public long TotalSizeBytes { get; set; } = 0;

    [JsonPropertyName("free_size_bytes")]
    public long FreeSizeBytes { get; set; } = 0;

    [JsonPropertyName("total_formatted")]
    public string TotalFormatted { get; set; } = "0 GB";

    [JsonPropertyName("free_formatted")]
    public string FreeFormatted { get; set; } = "0 GB";

    [JsonPropertyName("percent_free")]
    public double PercentFree { get; set; } = 0.0;

    [JsonPropertyName("is_system")]
    public bool IsSystem { get; set; } = false;

    [JsonPropertyName("recommended_role")]
    public string RecommendedRole { get; set; } = string.Empty;
}

public static class VolumeManager
{
    private static readonly Dictionary<string, (bool IsSSD, string MediaType, string BusType)> _mediaCache = new(StringComparer.OrdinalIgnoreCase);

    static VolumeManager()
    {
        RefreshDiskMediaCache();
    }

    public static void RefreshDiskMediaCache()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-Partition | Where-Object { $_.DriveLetter } | ForEach-Object { $p = $_; $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq $p.DiskNumber }; [PSCustomObject]@{ Letter = $p.DriveLetter; Bus = $pd.BusType; Media = $pd.MediaType } } | ConvertTo-Json -Compress\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                    var root = doc.RootElement;

                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in root.EnumerateArray())
                        {
                            ParseDiskElement(el);
                        }
                    }
                    else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        ParseDiskElement(root);
                    }
                }
            }
        }
        catch { }
    }

    private static void ParseDiskElement(System.Text.Json.JsonElement el)
    {
        try
        {
            string letter = el.GetProperty("Letter").GetString() ?? "";
            string bus = el.GetProperty("Bus").GetString() ?? "";
            string media = el.GetProperty("Media").GetString() ?? "";

            bool isSsd = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ||
                         media.Equals("SSD", StringComparison.OrdinalIgnoreCase);

            string mediaType = isSsd
                ? (bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD ⚡ 高速固态" : "SATA SSD ⚡ 固态硬盘")
                : "SATA HDD 💾 机械存储";

            if (!string.IsNullOrEmpty(letter))
            {
                _mediaCache[letter] = (isSsd, mediaType, bus);
            }
        }
        catch { }
    }

    public static List<SystemVolume> GetSystemVolumes()
    {
        var results = new List<SystemVolume>();
        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        sysDrive = sysDrive.TrimEnd('\\').ToUpperInvariant();

        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable))
                continue;

            try
            {
                string letter = d.Name.TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel.Trim();
                bool isSys = d.Name.TrimEnd('\\').Equals(sysDrive, StringComparison.OrdinalIgnoreCase);

                double totalGb = Math.Round((double)d.TotalSize / (1024 * 1024 * 1024), 1);
                double freeGb = Math.Round((double)d.AvailableFreeSpace / (1024 * 1024 * 1024), 1);
                double pct = d.TotalSize > 0 ? Math.Round(((double)d.AvailableFreeSpace / d.TotalSize) * 100, 1) : 0.0;

                bool isSsd = false;
                string mediaTypeStr = "未知介质 (探测未完成)";
                string busTypeStr = "Unknown";
                bool mediaKnown = false;

                if (_mediaCache.TryGetValue(letter, out var cached))
                {
                    isSsd = cached.IsSSD;
                    mediaTypeStr = cached.MediaType;
                    busTypeStr = cached.BusType;
                    mediaKnown = true;
                }
                // AUDIT A14: when the probe did not report this volume we must not invent
                // "NVMe SSD". An unverified hardware claim would silently drive tiering
                // recommendations, so the volume stays explicitly Unknown.

                string role;
                string labelLower = label.ToLowerInvariant();

                if (isSys)
                {
                    role = "系统盘";
                }
                else if (!mediaKnown)
                {
                    role = "数据存储卷";
                }
                else if (isSsd)
                {
                    role = "固态存储卷";
                }
                else
                {
                    role = "大容量存储卷";
                }

                results.Add(new SystemVolume
                {
                    DriveLetter = letter,
                    Label = label,
                    FileSystem = d.DriveFormat,
                    MediaType = mediaTypeStr,
                    IsSSD = isSsd,
                    MediaKnown = mediaKnown,
                    BusType = busTypeStr,
                    TotalSizeBytes = d.TotalSize,
                    FreeSizeBytes = d.AvailableFreeSpace,
                    TotalFormatted = $"{totalGb} GB",
                    FreeFormatted = $"{freeGb} GB",
                    PercentFree = pct,
                    IsSystem = isSys,
                    RecommendedRole = role
                });
            }
            catch { }
        }

        return results.OrderBy(v => v.DriveLetter).ToList();
    }

    public static (string Path, string Reason) RecommendVaultPathWithReason(string assetCategory, string assetName)
    {
        var volumes = GetSystemVolumes().Where(v => !v.IsSystem).ToList();
        if (volumes.Count == 0)
        {
            return (Path.Combine("C:\\", "AppVault", assetName), "系统未检测到次级存储卷，默认回退至系统盘隔离仓。");
        }

        // Recommend non-system volume with the most free space (preferring SSD if confirmed for IO-intensive categories)
        var bestVolume = volumes.OrderByDescending(v => v.FreeSizeBytes).First();
        var ssdVolume = volumes.FirstOrDefault(v => v.MediaKnown && v.IsSSD);

        bool isIoHeavy = assetCategory.Contains("ai", StringComparison.OrdinalIgnoreCase) ||
                         assetCategory.Contains("container", StringComparison.OrdinalIgnoreCase);

        var targetVol = (isIoHeavy && ssdVolume != null) ? ssdVolume : bestVolume;

        string subFolder = assetCategory.ToLowerInvariant() switch
        {
            "ai_models" or "ai_model" or "ai_compute" => Path.Combine("Models", assetName),
            "dev_containers" or "dev_container" or "docker_disk" or "dev_environment" => Path.Combine("Containers", assetName),
            "social_docs" or "social_chat" or "office_collaboration" => Path.Combine("SocialDocs", assetName),
            "creative_media" or "media_project" or "design_graphics" => Path.Combine("MediaProjects", assetName),
            _ => Path.Combine("General", assetName)
        };

        string vaultPath = Path.Combine($"{targetVol.DriveLetter}:\\", "AppVault", subFolder);
        string reason = $"推荐存放于可用空间充裕的 {targetVol.DriveLetter} 盘（余 {targetVol.FreeFormatted}）。";

        return (vaultPath, reason);
    }

    public static string RecommendVaultPath(string assetCategory, string assetName)
    {
        return RecommendVaultPathWithReason(assetCategory, assetName).Path;
    }
}
