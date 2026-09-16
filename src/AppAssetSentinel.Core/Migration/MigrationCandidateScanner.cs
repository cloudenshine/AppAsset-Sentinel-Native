using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;
using AppAssetSentinel.Core.Safety;

namespace AppAssetSentinel.Core.Migration;

public class MigrationCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "ai_model"; // ai_model, docker_disk, dev_cache, heavy_app

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("is_junction")]
    public bool IsJunction { get; set; } = false;

    [JsonPropertyName("junction_target")]
    public string JunctionTarget { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; } = 0;

    [JsonPropertyName("size_formatted")]
    public string SizeFormatted { get; set; } = "0 MB";

    [JsonPropertyName("drive")]
    public string Drive { get; set; } = "C:";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("recommended_target")]
    public string RecommendedTarget { get; set; } = string.Empty;
}

public static class MigrationCandidateScanner
{
    public static List<MigrationCandidate> ScanCandidates(List<SoftwareAsset> apps)
    {
        var results = new List<MigrationCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Standard AI and Development Cache paths
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var knownTargets = new List<(string Name, string Path, string Category, string Desc)>
        {
            ("Ollama C盘默认权重库", Path.Combine(userProfile, @".ollama\models"), "ai_model", "Ollama 在 C 盘默认下载存储的大模型文件"),
            ("HuggingFace 本地模型缓存", Path.Combine(userProfile, @".cache\huggingface\hub"), "ai_model", "HuggingFace Hub 下载的开源模型权重"),
            ("Docker Desktop WSL2 虚拟磁盘", Path.Combine(localAppData, @"Docker\wsl\data\ext4.vhdx"), "docker_disk", "Docker 容器镜像与数据卷虚拟硬盘文件"),
            ("uv 全局编译缓存", Path.Combine(localAppData, @"uv\cache"), "dev_cache", "uv 包管理器构建缓存与 Wheel 文件"),
            ("npm 全局模块缓存", Path.Combine(appData, @"npm-cache"), "dev_cache", "Node.js npm 全局下载包缓存"),
            ("pip 全局缓存", Path.Combine(localAppData, @"pip\cache"), "dev_cache", "Python pip 构建与下载缓存")
        };

        // Dynamically add Ollama's active models path if configured and distinct
        try
        {
            var ollama = Adapters.OllamaAdapter.Discover();
            if (ollama.Found && !string.IsNullOrWhiteSpace(ollama.EffectiveModelsPath))
            {
                knownTargets.Insert(0, ("Ollama 活跃模型权重库", ollama.EffectiveModelsPath, "ai_model", "Ollama 当前生效的大模型物理权重库"));
            }
        }
        catch { }

        foreach (var (name, path, cat, desc) in knownTargets)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (seenPaths.Add(path))
                {
                    var junc = FastDirectorySizer.GetJunctionInfo(path);
                    long size = 0;
                    if (File.Exists(path))
                    {
                        try { size = new FileInfo(path).Length; } catch { }
                    }
                    else if (!junc.IsJunction)
                    {
                        size = FastDirectorySizer.CalculateDirectorySize(path);
                    }

                    double sizeMb = Math.Round((double)size / (1024 * 1024), 1);
                    string sizeStr = sizeMb > 1024 ? $"{Math.Round(sizeMb / 1024, 2)} GB" : $"{sizeMb} MB";
                    string drive = Path.GetPathRoot(path) ?? "C:";

                    results.Add(new MigrationCandidate
                    {
                        Name = name,
                        Category = cat,
                        SourcePath = path,
                        IsJunction = junc.IsJunction,
                        JunctionTarget = junc.TargetPath,
                        SizeBytes = size,
                        SizeFormatted = sizeStr,
                        Drive = drive.TrimEnd('\\'),
                        Description = desc,
                        RecommendedTarget = VolumeManager.RecommendVaultPath(cat, name)
                    });
                }
            }
        }

        // 2. Scan installed software with size > 500 MB (excluding critical system directories!)
        foreach (var app in apps)
        {
            if (!string.IsNullOrEmpty(app.InstallLocation) && 
                Directory.Exists(app.InstallLocation) && 
                app.EstimatedSizeBytes >= 500L * 1024 * 1024 &&
                !CriticalDirectoryGuard.IsCriticalDirectory(app.InstallLocation))
            {
                if (seenPaths.Add(app.InstallLocation))
                {
                    var junc = FastDirectorySizer.GetJunctionInfo(app.InstallLocation);
                    double sizeMb = Math.Round((double)app.EstimatedSizeBytes / (1024 * 1024), 1);
                    string sizeStr = sizeMb > 1024 ? $"{Math.Round(sizeMb / 1024, 2)} GB" : $"{sizeMb} MB";
                    string drive = Path.GetPathRoot(app.InstallLocation) ?? "C:";

                    results.Add(new MigrationCandidate
                    {
                        Name = app.DisplayName,
                        Category = app.Category.StartsWith("ai") ? "ai_model" : "heavy_app",
                        SourcePath = app.InstallLocation,
                        IsJunction = junc.IsJunction,
                        JunctionTarget = junc.TargetPath,
                        SizeBytes = app.EstimatedSizeBytes,
                        SizeFormatted = sizeStr,
                        Drive = drive.TrimEnd('\\'),
                        Description = $"{app.Publisher} · 占用 {sizeStr} 存储空间",
                        RecommendedTarget = VolumeManager.RecommendVaultPath("heavy_app", app.DisplayName)
                    });
                }
            }
        }

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }
}
