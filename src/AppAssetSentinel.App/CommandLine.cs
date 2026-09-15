using System.Text.Json;
using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using AppAssetSentinel.Core.Semantic;
using AppAssetSentinel.Core.Shield;

namespace AppAssetSentinel.App;

/// <summary>
/// AUDIT W12: a read-only command surface that shares the same capability policy as the GUI and
/// the HTTP API, so an automated caller cannot obtain a capability the interface refuses.
///
/// Only non-mutating commands exist here. Anything that changes user data must go through the
/// HTTP API under its capability gate and an explicit authorization flag, so this file contains
/// no code path that writes outside its own operation log.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// The command verbs this surface understands. Kept next to Run so the dispatcher in
    /// Program and the parser here cannot drift apart.
    /// </summary>
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--policy", "policy",
        "--scan", "scan",
        "--adapters", "adapters",
        "--operations", "operations",
        "--dependencies", "dependencies",
        "--help", "-h", "help"
    };

    public static bool IsCommand(string arg) => Verbs.Contains(arg);

    /// <summary>True for global flags that are not verbs but are still valid on a CLI invocation.</summary>
    public static bool IsGlobalFlag(string arg) =>
        arg.StartsWith("--profile", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, CapabilityPolicy policy)
    {
        // Global flags such as --profile=r1 may appear anywhere, so pick the verb rather than
        // assuming it is the first argument.
        string? commandArg = args.FirstOrDefault(IsCommand);

        if (commandArg == null)
        {
            PrintHelp();
            return 2;
        }

        string command = commandArg.ToLowerInvariant();

        switch (command)
        {
            case "--policy":
            case "policy":
                return PrintPolicy(policy);

            case "--scan":
            case "scan":
                return PrintInventory(policy);

            case "--adapters":
            case "adapters":
                return PrintAdapters(policy);

            case "--operations":
            case "operations":
                return PrintOperations();

            case "--dependencies":
            case "dependencies":
                return PrintDependencies(policy);

            case "--help":
            case "-h":
            case "help":
                PrintHelp();
                return 0;

            default:
                Console.Error.WriteLine($"未知命令：{commandArg}");
                PrintHelp();
                return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AppAsset Sentinel（只读命令面）

            用法：AppAssetSentinel.App.exe <命令>

              --policy        显示当前能力姿态：哪些写入已开放、哪些仍被关闭及原因
              --scan          执行一次全盘扫描并列出资产（不写入任何用户数据）
              --adapters      显示各领域的适配器状态与支持的写入能力
              --operations    列出操作记录，用于判断上一次迁移的真实状态
              --dependencies  显示拓扑依赖图谱摘要
              --help          显示本帮助

            本命令面为只读。任何会改变用户数据的操作都必须通过 HTTP API，
            并在对应能力门控与显式授权下执行。
            """);
    }

    private static int PrintPolicy(CapabilityPolicy policy)
    {
        Console.WriteLine($"策略档位：{(policy.AllowsMutation(Capability.VaultRelocate) ? "R1-relocation-verified" : "R0-safe-observation")}");
        Console.WriteLine();
        Console.WriteLine($"{"能力",-22}{"状态",-14}{"允许写入",-10}原因");
        Console.WriteLine(new string('-', 110));

        foreach (var posture in policy.Describe())
        {
            Console.WriteLine($"{posture.Capability,-22}{posture.State,-14}{(posture.AllowsMutation ? "是" : "否"),-10}{posture.Reason}");
        }

        return 0;
    }

    private static int PrintInventory(CapabilityPolicy policy)
    {
        var engine = new SemanticRuleEngine();
        var candidate = SemanticRuleEngine.TryLoadCandidate();
        if (candidate.Succeeded)
        {
            engine.ReplaceRules(candidate.Rules);
        }
        else
        {
            Console.Error.WriteLine($"[!] 规则库不可用：{candidate.Error}");
            return 1;
        }

        var scanner = new Win32RegistryScanner();
        var telemetry = new TelemetryEngine();

        var assets = scanner.ScanInstalledSoftware(includeSystemComponents: false, calculateDiskSize: false);
        engine.EnrichAll(assets);

        foreach (var asset in assets)
        {
            DynamicAssetIntelligence.EnhanceWithDynamicIntelligence(asset);
        }

        telemetry.AnalyzeAll(assets);

        var output = new
        {
            generated_at_utc = DateTime.UtcNow,
            read_only = true,
            counts = new
            {
                total = assets.Count,
                unknown_usage = assets.Count(a => a.HeatLevel == "unknown"),
                incomplete_size = assets.Count(a => !a.SizeMeasurementComplete)
            },
            assets = assets
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(a => new
                {
                    name = a.DisplayName,
                    version = a.DisplayVersion,
                    publisher = a.Publisher,
                    category = a.Category,
                    asset_type = a.AssetType,
                    heat_level = a.HeatLevel,
                    usage_confidence = a.UsageConfidence,
                    days_since_last_use = a.DaysSinceLastUse,
                    size_bytes = a.EstimatedSizeBytes,
                    size_complete = a.SizeMeasurementComplete,
                    install_location = a.InstallLocation,
                    is_protected = a.IsProtected
                })
        };

        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        }));

        return 0;
    }

    private static int PrintAdapters(CapabilityPolicy policy)
    {
        Console.WriteLine($"写入姿态：{(policy.AllowsMutation(Capability.VaultRelocate) ? "R1（仅已验收领域可写）" : "R0（全部只读）")}");
        Console.WriteLine();
        Console.WriteLine($"{"领域",-20}{"载荷形态",-14}{"写能力",-16}说明");
        Console.WriteLine(new string('-', 110));

        foreach (var descriptor in AdapterRegistry.Describe())
        {
            Console.WriteLine($"{descriptor.Domain,-20}{descriptor.PayloadKind,-14}{descriptor.WriteCapability,-16}{descriptor.ReadOnlyReason}");
        }

        Console.WriteLine();
        Console.WriteLine("--- HuggingFace 缓存（只读探测）---");
        var hf = HuggingFaceAdapter.Discover();
        Console.WriteLine($"置信度={hf.Confidence} 层级来源={hf.Origin}");
        Console.WriteLine($"有效 hub 路径={hf.EffectiveHubPath}");
        Console.WriteLine($"布局={hf.Layout} blob={hf.BlobCount} 仓库={hf.RepositoryCount}");
        Console.WriteLine($"迁移应改写的变量={hf.RecommendedVariable}={hf.RecommendedValueFor}");

        Console.WriteLine();
        Console.WriteLine("--- Ollama（只读探测）---");
        var ollama = OllamaAdapter.Discover();
        var (mechanism, reason) = OllamaAdapter.ChooseMechanism(ollama);
        Console.WriteLine($"发现={ollama.Found} 置信度={ollama.Confidence} 版本={ollama.Version}");
        Console.WriteLine($"模型路径={ollama.EffectiveModelsPath}（来源：{ollama.ModelsPathSource}）");
        Console.WriteLine($"服务可达={ollama.ServiceRunning} 可提供模型={ollama.ServedModels.Count}");
        Console.WriteLine($"推荐机制={mechanism}：{reason}");

        return 0;
    }

    private static int PrintOperations()
    {
        var log = new OperationLog(OperationLog.DefaultDirectory);
        var records = log.LoadAll();

        Console.WriteLine($"操作记录目录：{OperationLog.DefaultDirectory}");
        Console.WriteLine($"记录数：{records.Count}");
        Console.WriteLine();

        if (records.Count == 0)
        {
            Console.WriteLine("没有操作记录。");
            return 0;
        }

        foreach (var record in records)
        {
            Console.WriteLine($"[{record.State}] {record.AssetName}  task={record.TaskId}");
            Console.WriteLine($"  源  : {record.SourcePath}");
            Console.WriteLine($"  目标: {record.TargetPath}");
            Console.WriteLine($"  备份: {record.SourceBackupPath}（{record.BackupDisposition}）");
            if (!string.IsNullOrEmpty(record.RecoveryNote))
            {
                Console.WriteLine($"  注意: {record.RecoveryNote}");
            }
            Console.WriteLine();
        }

        return 0;
    }

    private static int PrintDependencies(CapabilityPolicy policy)
    {
        var engine = new SemanticRuleEngine();
        var shield = new DependencyShield();

        var scanner = new Win32RegistryScanner();
        List<SoftwareAsset> assets = scanner.ScanInstalledSoftware(includeSystemComponents: false, calculateDiskSize: false);
        engine.EnrichAll(assets);

        var links = shield.BuildGraph(assets);

        Console.WriteLine($"依赖边总数：{links.Count}（事实图保留全部观测到的边，含环）");
        Console.WriteLine($"参与环的边：{DependencyShield.CountCyclicEdges(links)}");
        Console.WriteLine();

        var top = assets
            .Where(a => a.IsProtected || a.HeatLevel == "infrastructure")
            .Select(a => new
            {
                a.DisplayName,
                Dependents = links.Count(l => l.UpstreamSoftwareId == a.Id)
            })
            .OrderByDescending(x => x.Dependents)
            .ThenBy(x => x.DisplayName)
            .Take(15);

        Console.WriteLine($"{"上游底座",-50}下游依赖数");
        Console.WriteLine(new string('-', 66));
        foreach (var item in top)
        {
            Console.WriteLine($"{item.DisplayName,-50}{item.Dependents}");
        }

        _ = policy;
        return 0;
    }
}
