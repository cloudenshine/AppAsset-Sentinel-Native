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
    public string MediaType { get; set; } = "SATA HDD 💾 机械存储";

    [JsonPropertyName("is_ssd")]
    public bool IsSSD { get; set; } = false;

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
                string mediaTypeStr = "SATA HDD 💾 机械存储";
                string busTypeStr = "SATA";

                if (_mediaCache.TryGetValue(letter, out var cached))
                {
                    isSsd = cached.IsSSD;
                    mediaTypeStr = cached.MediaType;
                    busTypeStr = cached.BusType;
                }
                else if (isSys || letter == "D")
                {
                    isSsd = true;
                    mediaTypeStr = "NVMe SSD ⚡ 高速固态";
                    busTypeStr = "NVMe";
                }

                string role;
                string labelLower = label.ToLowerInvariant();

                if (isSys)
                {
                    role = "Windows 系统与运行时主盘";
                }
                else if (isSsd)
                {
                    role = "AI 算力与大模型高速仓 (NVMe SSD 极速加载)";
                }
                else if (labelLower.Contains("doc") || letter == "E")
                {
                    role = "文本文档与社交通讯归档仓 (大容量机械存储)";
                }
                else if (labelLower.Contains("work") || labelLower.Contains("dev") || letter == "F")
                {
                    role = "软件工程与代码归档仓 (大容量机械存储)";
                }
                else if (labelLower.Contains("vid") || labelLower.Contains("media") || letter == "G")
                {
                    role = "影视素材与渲染工程归档仓 (大容量机械存储)";
                }
                else
                {
                    role = "通用大容量存储仓";
                }

                results.Add(new SystemVolume
                {
                    DriveLetter = letter,
                    Label = label,
                    FileSystem = d.DriveFormat,
                    MediaType = mediaTypeStr,
                    IsSSD = isSsd,
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
            return (Path.Combine("C:\\", "AppAsset_Vault", assetName), "系统未检测到次级磁盘卷宗，默认回退至系统盘专属隔离仓。");
        }

        // Fast SSD Volume (e.g. D:)
        var ssdVolume = volumes.FirstOrDefault(v => v.IsSSD) ?? volumes.OrderByDescending(v => v.FreeSizeBytes).First();

        // Large Capacity HDD Volume (e.g. E:, F:, G:)
        var hddVolume = volumes.FirstOrDefault(v => !v.IsSSD) ?? volumes.OrderByDescending(v => v.FreeSizeBytes).First();

        switch (assetCategory.ToLowerInvariant())
        {
            case "ai_models":
            case "ai_model":
            case "ai_compute":
                return (
                    Path.Combine($"{ssdVolume.DriveLetter}:\\", "AIStack_Vault", "models", assetName),
                    $"【智能存储分层策略】检测到属于大模型权重。已优先推荐 {ssdVolume.DriveLetter} 盘 ({ssdVolume.MediaType})，保障数十 GB 模型秒级加载进显存！"
                );

            case "dev_containers":
            case "dev_container":
            case "docker_disk":
            case "dev_environment":
                return (
                    Path.Combine($"{ssdVolume.DriveLetter}:\\", "AIStack_Vault", "containers", assetName),
                    $"【智能存储分层策略】检测到属于容器虚拟硬盘/高频编译缓存。优先推荐 {ssdVolume.DriveLetter} 盘 ({ssdVolume.MediaType})，避免机械盘 4K 随机读写瓶颈卡死。"
                );

            case "social_docs":
            case "social_chat":
            case "office_collaboration":
                var docVol = volumes.FirstOrDefault(v => v.Label.Contains("DOC", StringComparison.OrdinalIgnoreCase) || v.DriveLetter == "E") ?? hddVolume;
                return (
                    Path.Combine($"{docVol.DriveLetter}:\\", "Documents_Vault", "Social_Chat", assetName),
                    $"【智能存储分层策略】检测到属于历史聊天附件与媒体数据（冷数据）。推荐沉淀至 {docVol.DriveLetter} 盘 ({docVol.MediaType})，为高速固态节省宝贵空间！"
                );

            case "creative_media":
            case "media_project":
            case "design_graphics":
                var vidVol = volumes.FirstOrDefault(v => v.Label.Contains("VID", StringComparison.OrdinalIgnoreCase) || v.DriveLetter == "G") ?? hddVolume;
                return (
                    Path.Combine($"{vidVol.DriveLetter}:\\", "Videos_Vault", "Projects", assetName),
                    $"【智能存储分层策略】推荐归仓至 {vidVol.DriveLetter} 盘 ({vidVol.MediaType})，利用大容量存储空间承载海量渲染与多媒体素材。"
                );

            default:
                var genVol = volumes.OrderByDescending(v => v.FreeSizeBytes).First();
                return (
                    Path.Combine($"{genVol.DriveLetter}:\\", "General_Vault", assetName),
                    $"推荐存放于当前空闲容量最大的 {genVol.DriveLetter} 盘 (余 {genVol.FreeFormatted})。"
                );
        }
    }

    public static string RecommendVaultPath(string assetCategory, string assetName)
    {
        return RecommendVaultPathWithReason(assetCategory, assetName).Path;
    }
}
