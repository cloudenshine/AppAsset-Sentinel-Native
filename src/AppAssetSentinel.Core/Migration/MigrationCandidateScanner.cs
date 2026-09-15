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
    public string RecommendedTarget { get; set; } = @"D:\AIStack\migrated_assets";
}

public static class MigrationCandidateScanner
{
    public static List<MigrationCandidate> ScanCandidates(List<SoftwareAsset> apps)
    {
        var results = new List<MigrationCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Well-known AI and Large Data paths
        var knownTargets = new List<(string Name, string Path, string Category, string Desc)>
        {
            ("Ollama 本地大模型权重库", @"D:\AIStack\models\ollama", "ai_model", "包含 GGUF/Safetensors 大模型权重，占用海量存储"),
            ("Ollama 默认 C 盘权重库", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".ollama\models"), "ai_model", "Ollama 在 C 盘默认下载存储的大模型文件"),
            ("HuggingFace 本地模型缓存", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".cache\huggingface\hub"), "ai_model", "通过 transformers / diffusers 下载的开源模型权重"),
            ("Docker Desktop WSL2 虚拟磁盘", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Docker\wsl\data\ext4.vhdx"), "docker_disk", "Docker 容器镜像与数据卷虚拟硬盘文件"),
            ("ComfyUI 权重与模型目录", @"D:\Tools\ComfyUI\models", "ai_model", "Stable Diffusion / Flux Checkpoint 与 LoRA 权重库"),
            ("uv / pip 全局编译缓存", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"uv\cache"), "dev_cache", "Python 包管理器构建缓存与 Wheel 文件"),
            ("npm 全局模块缓存", @"D:\AIStack\tools\npm-cache", "dev_cache", "Node.js npm 全局下载包缓存")
        };

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
                        Description = desc
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
                        Description = $"{app.Publisher} · 占用 {sizeStr} 存储空间"
                    });
                }
            }
        }

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }
}
