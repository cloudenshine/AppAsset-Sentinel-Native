using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

public class DomainAssetCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "ai_models"; // ai_models, social_docs, creative_media, dev_containers

    [JsonPropertyName("domain_label")]
    public string DomainLabel { get; set; } = "AI大模型权重";

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("current_drive")]
    public string CurrentDrive { get; set; } = "C:";

    [JsonPropertyName("is_c_drive")]
    public bool IsCDrive { get; set; } = true;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; } = 0;

    [JsonPropertyName("size_formatted")]
    public string SizeFormatted { get; set; } = "0 MB";

    [JsonPropertyName("is_junction")]
    public bool IsJunction { get; set; } = false;

    [JsonPropertyName("junction_target")]
    public string JunctionTarget { get; set; } = string.Empty;

    [JsonPropertyName("recommended_vault_path")]
    public string RecommendedVaultPath { get; set; } = string.Empty;

    [JsonPropertyName("recommendation_reason")]
    public string RecommendationReason { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("drift_detected")]
    public bool DriftDetected { get; set; } = false;

    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; set; } = string.Empty;
}

public static class MultiDomainAssetScanner
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string MyDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static List<DomainAssetCandidate> ScanAllDomains(List<SoftwareAsset> apps)
    {
        var candidates = new List<DomainAssetCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeRegs = AssetVaultEngine.GetActiveRegistrations();

        // -------------------------------------------------------------
        // DOMAIN 1: AI大模型权重与向量库 (AI Models Vault)
        // -------------------------------------------------------------
        try
        {
            var ollama = Adapters.OllamaAdapter.Discover();
            if (ollama.Found && !string.IsNullOrWhiteSpace(ollama.EffectiveModelsPath))
            {
                AddIfPresent(candidates, seenPaths, activeRegs,
                    "Ollama 活跃模型权重库", "ai_models", "AI大模型权重",
                    ollama.EffectiveModelsPath,
                    "包含 GGUF/Safetensors 大模型物理权重文件，提供本地推理算力");
            }
        }
        catch { }

        AddIfPresent(candidates, seenPaths, activeRegs,
            "Ollama C盘默认模型库", "ai_models", "AI大模型权重",
            Path.Combine(UserProfile, @".ollama\models"),
            "Ollama 默认在 C 盘用户目录下下载的大模型权重，极易撑爆系统盘");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "HuggingFace 权重 Hub 缓存", "ai_models", "AI大模型权重",
            Path.Combine(UserProfile, @".cache\huggingface\hub"),
            "通过 Python transformers/diffusers 下载的开源大模型权重集合");

        // -------------------------------------------------------------
        // DOMAIN 2: 社交与办公通讯资料归档 (Social & Communication Vault)
        // -------------------------------------------------------------
        AddIfPresent(candidates, seenPaths, activeRegs,
            "微信个人文件与聊天媒体库", "social_docs", "社交通讯归档",
            Path.Combine(MyDocs, @"WeChat Files"),
            "包含微信接收的所有历史大文件、音视频聊天记录与相册，占用巨大");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "QQ 用户附件与群聊天文件", "social_docs", "社交通讯归档",
            Path.Combine(MyDocs, @"Tencent Files"),
            "QQ 接收的群文件、图片与个人聊天记录");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "飞书多维表格与会议媒体缓存", "social_docs", "社交通讯归档",
            Path.Combine(AppData, @"LarkShell"),
            "飞书桌面端缓存的音视频会议录屏、离线文档与临时数据");

        // -------------------------------------------------------------
        // DOMAIN 3: 影视剪辑工程与渲染素材 (Creative & Media Projects Vault)
        // -------------------------------------------------------------
        AddIfPresent(candidates, seenPaths, activeRegs,
            "剪映专业版 个人剪辑草稿工程", "creative_media", "影视工程草稿",
            Path.Combine(LocalAppData, @"JianyingPro\User Data\Projects"),
            "【无价数字资产】包含所有未导出的视频剪辑时间线工程草稿与分段，绝不可丢！");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "剪映专业版 代理视频与渲染缓存", "creative_media", "影视工程草稿",
            Path.Combine(LocalAppData, @"JianyingPro\User Data\Cache"),
            "4K 剪辑过程中产生的巨大代理视频切片与特效临时渲染缓存");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "Blender 渲染工程与临时模型缓存", "creative_media", "影视工程草稿",
            Path.Combine(AppData, @"Blender Foundation"),
            "3D 渲染中间材质贴图、几何节点缓存与历史版本");

        // -------------------------------------------------------------
        // DOMAIN 4: 全栈开发容器与虚拟磁盘 (Dev & Containers Vault)
        // -------------------------------------------------------------
        AddIfPresent(candidates, seenPaths, activeRegs,
            "Docker Desktop WSL2 虚拟硬盘", "dev_containers", "容器虚拟盘",
            Path.Combine(LocalAppData, @"Docker\wsl\data\ext4.vhdx"),
            "包含 Docker 所有 Linux 镜像与数据库容器的虚拟磁盘文件 (ext4.vhdx)");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "Android 模拟器虚拟机镜像 (AVD)", "dev_containers", "容器虚拟盘",
            Path.Combine(UserProfile, @".android\avd"),
            "Android Studio 手机模拟器虚拟机完整磁盘镜像文件");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "uv 全局编译缓存", "dev_containers", "容器虚拟盘",
            Path.Combine(LocalAppData, @"uv\cache"),
            "Python 高速包管理器预编译 Wheel 包与二进制缓存");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "npm 全局模块离线缓存", "dev_containers", "容器虚拟盘",
            Path.Combine(AppData, "npm-cache"),
            "Node.js 全局安装与开发构建过程中缓存的 npm 依赖包");

        AddIfPresent(candidates, seenPaths, activeRegs,
            "pip 全局构建缓存", "dev_containers", "容器虚拟盘",
            Path.Combine(LocalAppData, @"pip\cache"),
            "Python pip 包安装与构建缓存目录");

        return candidates.OrderByDescending(c => c.SizeBytes).ToList();
    }

    private static void AddIfPresent(
        List<DomainAssetCandidate> list,
        HashSet<string> seenPaths,
        List<VaultRegistration> activeRegs,
        string name,
        string domain,
        string domainLabel,
        string path,
        string desc)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        if (!seenPaths.Add(path)) return;

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
        string drive = (Path.GetPathRoot(path) ?? "C:").TrimEnd('\\').ToUpperInvariant();
        bool isC = drive.StartsWith("C");

        // Check if there is an active watchdog registration for this path
        var reg = activeRegs.FirstOrDefault(r => r.VirtualAnchorPath.Equals(path, StringComparison.OrdinalIgnoreCase));

        var (recPath, recReason) = VolumeManager.RecommendVaultPathWithReason(domain, Path.GetFileName(path));

        list.Add(new DomainAssetCandidate
        {
            Name = name,
            Domain = domain,
            DomainLabel = domainLabel,
            SourcePath = path,
            CurrentDrive = drive,
            IsCDrive = isC,
            SizeBytes = size,
            SizeFormatted = sizeStr,
            IsJunction = junc.IsJunction,
            JunctionTarget = junc.TargetPath,
            RecommendedVaultPath = recPath,
            RecommendationReason = recReason,
            Description = desc,
            DriftDetected = reg != null && reg.DriftDetected,
            RegistrationId = reg?.Id ?? ""
        });
    }
}
