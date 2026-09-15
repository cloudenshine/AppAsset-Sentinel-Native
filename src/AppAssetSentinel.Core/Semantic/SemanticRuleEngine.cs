using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Semantic;

public class SemanticRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "tools_utility";

    [JsonPropertyName("asset_type")]
    public string AssetType { get; set; } = "gui_app";

    [JsonPropertyName("plain_role")]
    public string PlainRole { get; set; } = string.Empty;

    [JsonPropertyName("core_usage")]
    public string CoreUsage { get; set; } = string.Empty;

    [JsonPropertyName("ecosystem")]
    public string Ecosystem { get; set; } = string.Empty;

    [JsonPropertyName("dependency_chain")]
    public string DependencyChain { get; set; } = string.Empty;

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = "regular_app";

    [JsonPropertyName("is_protected")]
    public bool IsProtected { get; set; } = false;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonIgnore]
    public Regex? CompiledRegex { get; set; }
}

public class SemanticRuleEngine
{
    private readonly List<SemanticRule> _rules = new();

    public SemanticRuleEngine(string? rulesJsonPath = null)
    {
        LoadRules(rulesJsonPath);
    }

    public static string? FindRulesDirectory()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "rules");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "app_semantics.json")))
            {
                return candidate;
            }
            current = current.Parent;
        }
        return null;
    }

    public void LoadRules(string? customPath = null)
    {
        _rules.Clear();
        string jsonContent = string.Empty;

        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            try { jsonContent = File.ReadAllText(customPath); } catch { }
        }
        else
        {
            var rulesDir = FindRulesDirectory();
            if (rulesDir != null)
            {
                var targetFile = Path.Combine(rulesDir, "app_semantics.json");
                if (File.Exists(targetFile))
                {
                    try { jsonContent = File.ReadAllText(targetFile); } catch { }
                }
            }
        }

        if (!string.IsNullOrEmpty(jsonContent))
        {
            try
            {
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                };
                var parsed = JsonSerializer.Deserialize<List<SemanticRule>>(jsonContent, options);
                if (parsed != null)
                {
                    foreach (var r in parsed)
                    {
                        if (!string.IsNullOrWhiteSpace(r.Pattern))
                        {
                            try
                            {
                                r.CompiledRegex = new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                                _rules.Add(r);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }

    public void EnrichAsset(SoftwareAsset asset)
    {
        if (asset == null) return;

        string searchTarget = $"{asset.DisplayName} {asset.Publisher} {asset.InstallLocation} {asset.MainExecutable}".ToLowerInvariant();

        // 1. Match against rich knowledge base
        foreach (var rule in _rules)
        {
            if (rule.CompiledRegex != null && rule.CompiledRegex.IsMatch(searchTarget))
            {
                asset.Category = rule.Category;
                asset.AssetType = rule.AssetType;
                asset.PlainRole = rule.PlainRole;
                asset.CoreUsage = rule.CoreUsage;
                asset.Ecosystem = rule.Ecosystem;
                asset.DependencyChain = rule.DependencyChain;
                asset.PurposeSummary = $"{rule.PlainRole} {rule.CoreUsage}";
                asset.RiskLevel = rule.RiskLevel;
                asset.IsProtected = rule.IsProtected;
                asset.Tags = new List<string>(rule.Tags);

                // Infrastructure protection
                if (asset.AssetType is "runtime_environment" or "hardware_driver" or "system_service" or "sdk_toolchain")
                {
                    asset.HeatLevel = "infrastructure";
                    asset.IsProtected = true;
                }
                return;
            }
        }

        // 2. Intelligent Heuristic Fallbacks (100% conforming to original system design)
        string cleanPub = string.IsNullOrWhiteSpace(asset.Publisher) ? "硬件/软件厂商" : asset.Publisher.Trim();
        string name = asset.DisplayName;

        // Fallback A: Hardware Driver & HAL / Lighting / Peripheral Add-on
        if (ContainsAny(searchTarget, "driver", "realtek", "nvidia", "intel(r)", "audio device", "sound card", "lighting", "aura", "armoury", "geforce", "radeon", "asustek", "asus"))
        {
            asset.Category = "system_runtime";
            asset.AssetType = "hardware_driver";
            asset.PlainRole = $"「{name}」硬件底层驱动或外设调控组件。";
            asset.CoreUsage = "负责操作系统底层与物理硬件之间的指令通信、主板传感器状态监控与硬件调度。";
            asset.Ecosystem = $"{cleanPub} 硬件驱动与控制生态链。";
            asset.DependencyChain = "【硬件设备驱动】直接管理物理硬件或外设通信，建议保留。";
            asset.PurposeSummary = $"「{name}」硬件驱动底层组件，负责操作系统与外部硬件通信。";
            asset.RiskLevel = "critical_core";
            asset.IsProtected = true;
            asset.HeatLevel = "infrastructure";
            asset.Tags = new List<string> { "硬件驱动", "系统组件", "底层支撑" };
            return;
        }

        // Fallback B: SDK / Toolchain / Build tools
        if (ContainsAny(searchTarget, "sdk", "headers", "toolchain", "compiler", "crt", "msbuild", "visual studio", "redistributable", "kits", "debugging tools"))
        {
            asset.Category = "system_runtime";
            asset.AssetType = "sdk_toolchain";
            asset.PlainRole = $"「{name}」软件开发构建套件或底层支持库。";
            asset.CoreUsage = "供开发工具、本地编译引擎和依赖打包使用，支撑代码编译与原生二进制执行。";
            asset.Ecosystem = $"{cleanPub} 构建工具链体系。";
            asset.DependencyChain = "【开发工具链保护】卸载可能导致本地工程构建或动态链接失败，建议保留。";
            asset.PurposeSummary = $"「{name}」软件开发构建套件或头文件库，支撑本地编译与工程构建。";
            asset.RiskLevel = "important_runtime";
            asset.IsProtected = true;
            asset.HeatLevel = "infrastructure";
            asset.Tags = new List<string> { "SDK套件", "开发支持库", "系统组件" };
            return;
        }

        // Fallback C: Background System Services / Add-on / Agents
        if (ContainsAny(searchTarget, "service", "daemon", "agent", "hal", "helper", "add-on", "framework", "extension"))
        {
            asset.Category = "system_runtime";
            asset.AssetType = "system_service";
            asset.PlainRole = $"「{name}」Windows 后台常驻服务或软硬件抽象扩展组件。";
            asset.CoreUsage = "在后台静默运行，负责特定软硬件设备的状态监控、灯效同步或自动化协调服务。";
            asset.Ecosystem = $"{cleanPub} 服务体系。";
            asset.DependencyChain = "【系统服务】卸载可能导致关联的前台程序或硬件功能失效。";
            asset.PurposeSummary = $"「{name}」Windows 后台常驻服务或硬件抽象管理组件。";
            asset.RiskLevel = "important_runtime";
            asset.IsProtected = true;
            asset.HeatLevel = "infrastructure";
            asset.Tags = new List<string> { "后台服务", "系统组件" };
            return;
        }

        // Fallback D: General Interactive GUI App
        asset.Category = "tools_utility";
        asset.AssetType = "gui_app";
        asset.PlainRole = $"由「{cleanPub}」官方发布的 Windows 桌面应用程序。";
        asset.CoreUsage = $"为用户提供「{name}」对应的专属业务功能支持与日常桌面操作。";
        asset.Ecosystem = $"{cleanPub} 桌面应用生态链。";
        asset.DependencyChain = "常规桌面端独立软件，卸载通常不影响系统底层稳定性或其他开发环境。";
        asset.PurposeSummary = $"由「{cleanPub}」发布的 Windows 工具软件（{name}）。";
        asset.RiskLevel = "regular_app";
        asset.IsProtected = false;
        asset.Tags = new List<string> { "桌面应用" };
    }

    private static bool ContainsAny(string target, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (target.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void EnrichAll(IEnumerable<SoftwareAsset> assets)
    {
        foreach (var a in assets)
        {
            EnrichAsset(a);
        }
    }
}
