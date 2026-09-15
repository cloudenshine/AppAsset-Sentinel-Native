using System.Text.Json.Serialization;
using Microsoft.Win32;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Migration;

namespace AppAssetSentinel.Core.Uninstaller;

public class ResidueAssetInfo
{
    [JsonPropertyName("contains_assets")]
    public bool ContainsAssets { get; set; } = false;

    [JsonPropertyName("asset_samples")]
    public List<string> AssetSamples { get; set; } = new();
}

public class ResidueItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "file"; // install_directory, appdata_folder, shortcut, registry_key

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("confidence_rating")]
    public string ConfidenceRating { get; set; } = "HIGH_CONFIDENCE_SAFE"; // HIGH_CONFIDENCE_SAFE, MEDIUM_REVIEW

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("is_preserved_data")]
    public bool IsPreservedData { get; set; } = false;

    [JsonPropertyName("asset_info")]
    public ResidueAssetInfo AssetInfo { get; set; } = new();
}

public static class ResidueRadar
{
    private static readonly string[] ProbeDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow")
    };

    private static readonly string[] ShortcutDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
    };

    public static List<ResidueItem> ScanResidues(SoftwareAsset app, bool preserveData = true)
    {
        var results = new List<ResidueItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string appName = app.DisplayName.Trim().ToLowerInvariant();
        string cleanName = System.Text.RegularExpressions.Regex.Replace(appName, @"[^a-zA-Z0-9\u4e00-\u9fa5]", "");
        string publisher = app.Publisher.Trim().ToLowerInvariant();

        // 1. Install Location
        if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            var aiAssets = AiAssetDetector.DetectAssetsInDirectory(app.InstallLocation, maxResults: 10);
            bool hasAi = aiAssets.Count > 0;
            var samples = aiAssets.Take(3).Select(a => $"{System.IO.Path.GetFileName(a.Path)} ({Math.Round((double)a.FileSizeBytes / (1024 * 1024), 1)} MB)").ToList();

            results.Add(new ResidueItem
            {
                Type = "install_directory",
                Path = app.InstallLocation,
                ConfidenceRating = "HIGH_CONFIDENCE_SAFE",
                Reason = "软件主安装目录",
                IsPreservedData = preserveData && hasAi,
                AssetInfo = new ResidueAssetInfo
                {
                    ContainsAssets = hasAi,
                    AssetSamples = samples
                }
            });
            seenPaths.Add(app.InstallLocation);
        }

        // 2. AppData Folders
        var searchKeywords = new List<string>();
        if (!string.IsNullOrEmpty(cleanName) && cleanName.Length >= 3) searchKeywords.Add(cleanName);
        if (!string.IsNullOrEmpty(appName)) searchKeywords.Add(appName);
        if (!string.IsNullOrEmpty(app.MainExecutable))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(app.MainExecutable).ToLowerInvariant();
            if (stem.Length >= 3) searchKeywords.Add(stem);
        }

        foreach (var probe in ProbeDirs)
        {
            if (!Directory.Exists(probe)) continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(probe))
                {
                    string subName = System.IO.Path.GetFileName(sub).ToLowerInvariant();
                    foreach (var kw in searchKeywords)
                    {
                        if (subName.Equals(kw, StringComparison.OrdinalIgnoreCase) || 
                            (kw.Length >= 4 && subName.Contains(kw)))
                        {
                            if (seenPaths.Add(sub))
                            {
                                var aiAssets = AiAssetDetector.DetectAssetsInDirectory(sub, maxResults: 5);
                                bool hasAi = aiAssets.Count > 0;
                                var samples = aiAssets.Take(2).Select(a => System.IO.Path.GetFileName(a.Path)).ToList();

                                results.Add(new ResidueItem
                                {
                                    Type = "appdata_folder",
                                    Path = sub,
                                    ConfidenceRating = "HIGH_CONFIDENCE_SAFE",
                                    Reason = $"匹配用户配置目录 ({System.IO.Path.GetFileName(probe)})",
                                    IsPreservedData = preserveData && hasAi,
                                    AssetInfo = new ResidueAssetInfo
                                    {
                                        ContainsAssets = hasAi,
                                        AssetSamples = samples
                                    }
                                });
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Shortcuts
        foreach (var scDir in ShortcutDirs)
        {
            if (!Directory.Exists(scDir)) continue;

            try
            {
                foreach (var lnk in Directory.EnumerateFiles(scDir, "*.lnk", SearchOption.AllDirectories))
                {
                    string lnkName = System.IO.Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                    foreach (var kw in searchKeywords)
                    {
                        if (lnkName.Contains(kw))
                        {
                            if (seenPaths.Add(lnk))
                            {
                                results.Add(new ResidueItem
                                {
                                    Type = "shortcut",
                                    Path = lnk,
                                    ConfidenceRating = "HIGH_CONFIDENCE_SAFE",
                                    Reason = "桌面或开始菜单残留快捷方式",
                                    IsPreservedData = false
                                });
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // 4. Registry Uninstall Key itself
        if (!string.IsNullOrEmpty(app.RegistryKeyPath))
        {
            results.Add(new ResidueItem
            {
                Type = "uninstall_registry_key",
                Path = app.RegistryKeyPath,
                ConfidenceRating = "HIGH_CONFIDENCE_SAFE",
                Reason = "Windows 系统已安装程序注册表注册项",
                IsPreservedData = false
            });
        }

        return results;
    }
}
