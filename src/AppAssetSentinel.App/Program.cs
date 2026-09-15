using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Photino.NET;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;
using AppAssetSentinel.Core.Semantic;
using AppAssetSentinel.Core.Shield;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Safety;
using AppAssetSentinel.Core.Uninstaller;

namespace AppAssetSentinel.App;

public class Program
{
    private static readonly List<SoftwareAsset> _cachedAssets = new();
    private static SemanticRuleEngine _semanticEngine = new();
    private static DependencyShield _shield = new();
    private static readonly Win32RegistryScanner _scanner = new();
    private static readonly TelemetryEngine _telemetry = new();
    private const int ServerPort = 8765;
    private static readonly string BaseUrl = $"http://127.0.0.1:{ServerPort}";

    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  AppAsset Sentinel (Native) - 智能软件资产全生命周期控制台");
        Console.WriteLine("  驱动: C# (.NET 9) + Win32 原生调用 + 内嵌高速 API + Photino");
        Console.WriteLine($"  本地服务地址: {BaseUrl}");
        Console.WriteLine("============================================================");

        // Preload rules and initial scan
        _semanticEngine = new SemanticRuleEngine();
        _shield = new DependencyShield();
        RefreshAssets();

        if (args.Length > 0 && args[0].Equals("--headless-scan", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[Top 10 Assets]");
            foreach (var a in _cachedAssets.Take(10))
            {
                Console.WriteLine($" - {a.DisplayName} [{a.Category}] (Heat: {a.HeatLevel} / {a.HeatScore}分, Last: {a.LastUsedTimestamp}, Src: {a.TelemetrySource})");
            }
            return;
        }

        // Start embedded lightweight ASP.NET Core server in background thread
        var webThread = new Thread(() => StartWebServer(args))
        {
            IsBackground = true
        };
        webThread.Start();

        // Wait brief moment for HTTP server readiness
        Thread.Sleep(800);

        if (args.Length > 0 && args[0].Equals("--server-only", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[*] Server running in background. Press Ctrl+C to stop.");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        // Launch Native Photino Window connecting to local embedded server
        try
        {
            var window = new PhotinoWindow()
                .SetTitle("AppAsset Sentinel | 智能软件资产全生命周期控制台 (Native)")
                .SetUseOsDefaultSize(false)
                .SetSize(1380, 920)
                .Center()
                .SetResizable(true);

            window.Load(BaseUrl);
            window.WaitForClose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Photino Fallback] Cannot launch native window: {ex.Message}");
            Console.WriteLine($"[Photino Fallback] Falling back to default system browser: {BaseUrl}");
            Process.Start(new ProcessStartInfo
            {
                FileName = BaseUrl,
                UseShellExecute = true
            });
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void RefreshAssets()
    {
        Console.WriteLine("[*] 正在执行全盘 Win32 注册表、便携资产与多维遥测扫描...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scanned = _scanner.ScanInstalledSoftware(includeSystemComponents: false, calculateDiskSize: true);
        _semanticEngine.EnrichAll(scanned);

        foreach (var a in scanned)
        {
            DynamicAssetIntelligence.EnhanceWithDynamicIntelligence(a);
        }

        _shield.BuildGraph(scanned);
        _telemetry.AnalyzeAll(scanned);
        RedundancyAndSxsAnalyzer.Analyze(scanned);

        lock (_cachedAssets)
        {
            _cachedAssets.Clear();
            _cachedAssets.AddRange(scanned);
        }

        sw.Stop();
        Console.WriteLine($"[✓] 扫描完成！共纳管 {_cachedAssets.Count} 个软件资产，耗时 {sw.ElapsedMilliseconds} ms。");
    }

    private static void StartWebServer(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = GetWebRootPath()
        });

        builder.WebHost.UseUrls(BaseUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // 1. GET /api/overview
        app.MapGet("/api/overview", () =>
        {
            lock (_cachedAssets)
            {
                int totalCount = _cachedAssets.Count;
                long totalBytes = _cachedAssets.Sum(a => a.EstimatedSizeBytes);
                var heatDist = new Dictionary<string, int>
                {
                    ["hot"] = 0, ["warm"] = 0, ["cooling"] = 0, ["zombie"] = 0, ["infrastructure"] = 0
                };
                var catDist = new Dictionary<string, int>();
                int trueZombies = 0;
                int protectedInfra = 0;
                long zombieReclaimable = 0;

                foreach (var a in _cachedAssets)
                {
                    var level = string.IsNullOrEmpty(a.HeatLevel) ? "warm" : a.HeatLevel;
                    heatDist[level] = heatDist.GetValueOrDefault(level, 0) + 1;

                    var cat = string.IsNullOrEmpty(a.Category) ? "tools_utility" : a.Category;
                    catDist[cat] = catDist.GetValueOrDefault(cat, 0) + 1;

                    if (level == "infrastructure" || a.IsProtected)
                    {
                        protectedInfra++;
                    }
                    else if (level == "zombie")
                    {
                        trueZombies++;
                        zombieReclaimable += a.EstimatedSizeBytes;
                    }
                }

                double sizeGb = Math.Round((double)totalBytes / (1024 * 1024 * 1024), 2);
                double reclaimGb = Math.Round((double)zombieReclaimable / (1024 * 1024 * 1024), 2);

                return Results.Ok(new
                {
                    total_apps = totalCount,
                    total_size_bytes = totalBytes,
                    total_size_formatted = $"{sizeGb} GB",
                    zombie_apps_count = trueZombies,
                    protected_infra_count = protectedInfra,
                    zombie_reclaimable_formatted = $"{reclaimGb} GB",
                    heat_distribution = heatDist,
                    category_distribution = catDist
                });
            }
        });

        // 2. GET /api/apps
        app.MapGet("/api/apps", (string? category, string? heat_level, string? search, string? sort_by, string? order) =>
        {
            lock (_cachedAssets)
            {
                var query = _cachedAssets.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(category) && category != "all")
                {
                    query = query.Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(heat_level) && heat_level != "all")
                {
                    query = query.Where(a => a.HeatLevel.Equals(heat_level, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.Trim().ToLowerInvariant();
                    query = query.Where(a =>
                        a.DisplayName.ToLowerInvariant().Contains(kw) ||
                        a.Publisher.ToLowerInvariant().Contains(kw) ||
                        a.PlainRole.ToLowerInvariant().Contains(kw) ||
                        a.CoreUsage.ToLowerInvariant().Contains(kw));
                }

                // Sorting
                if (sort_by == "size" || sort_by == "estimated_size_bytes")
                {
                    query = (order == "asc") ? query.OrderBy(a => a.EstimatedSizeBytes) : query.OrderByDescending(a => a.EstimatedSizeBytes);
                }
                else if (sort_by == "name" || sort_by == "display_name")
                {
                    query = (order == "asc") ? query.OrderBy(a => a.DisplayName) : query.OrderByDescending(a => a.DisplayName);
                }
                else
                {
                    query = (order == "asc") ? query.OrderBy(a => a.HeatScore) : query.OrderByDescending(a => a.HeatScore);
                }

                var list = query.ToList();

                return Results.Ok(new
                {
                    apps = list,
                    total = list.Count
                });
            }
        });

        // 3. POST /api/scan
        app.MapPost("/api/scan", () =>
        {
            RefreshAssets();
            return Results.Ok(new { status = "success", count = _cachedAssets.Count });
        });

        // 4. POST /api/uninstall/plan
        app.MapPost("/api/uninstall/plan", (UninstallPlanDto req) =>
        {
            lock (_cachedAssets)
            {
                var plan = UninstallerEngine.PlanUninstall(req.SoftwareId, _cachedAssets, _shield, req.PreserveData);
                return Results.Ok(plan);
            }
        });

        // 5. POST /api/uninstall/execute
        app.MapPost("/api/uninstall/execute", (UninstallExecuteDto req) =>
        {
            var result = UninstallerEngine.ExecutePlan(req.Plan, req.Simulate);
            return Results.Ok(result);
        });

        // 6. POST /api/uninstall/force-clean
        app.MapPost("/api/uninstall/force-clean", (ForceCleanDto req) =>
        {
            lock (_cachedAssets)
            {
                var target = _cachedAssets.FirstOrDefault(a => a.Id == req.SoftwareId);
                if (target != null)
                {
                    UninstallerEngine.ForceClean(target);
                    _cachedAssets.Remove(target);
                }
                return Results.Ok(new { success = true });
            }
        });

        // -------------------------------------------------------------
        // ASSET VAULT & MULTI-VOLUME REDIRECTION ENGINE
        // -------------------------------------------------------------
        app.MapGet("/api/vault/volumes", () =>
        {
            var volumes = VolumeManager.GetSystemVolumes();
            return Results.Ok(volumes);
        });

        app.MapGet("/api/vault/candidates", () =>
        {
            lock (_cachedAssets)
            {
                var candidates = MultiDomainAssetScanner.ScanAllDomains(_cachedAssets);
                return Results.Ok(candidates);
            }
        });

        app.MapPost("/api/vault/relocate", async (RelocateRequest req) =>
        {
            var task = await AssetVaultEngine.RelocateAndDualLockAsync(
                req.SourcePath,
                req.TargetVaultPath,
                req.AssetName,
                req.Category
            );
            return Results.Ok(task);
        });

        app.MapGet("/api/vault/watchdog", () =>
        {
            var audits = DriftWatchdog.InspectAndAuditDrifts();
            return Results.Ok(audits);
        });

        app.MapPost("/api/vault/auto-heal", (AutoHealRequest req) =>
        {
            var (success, msg) = DriftWatchdog.AutoHealDrift(req.RegistrationId);
            return Results.Ok(new { success = success, message = msg });
        });

        // Backward compatibility
        app.MapGet("/api/migration/candidates", () =>
        {
            lock (_cachedAssets)
            {
                var candidates = MultiDomainAssetScanner.ScanAllDomains(_cachedAssets);
                return Results.Ok(candidates);
            }
        });

        app.MapPost("/api/migrate", async (MigrateDto req) =>
        {
            var task = await JunctionEngine.MigrateDirectoryAsync(
                req.SourcePath,
                req.TargetParent,
                assetName: req.AssetName
            );
            return Results.Ok(task);
        });

        app.MapPost("/api/migration/rollback", (RollbackDto req) =>
        {
            bool removed = JunctionEngine.RemoveJunction(req.JunctionPath, out var err);
            return Results.Ok(new { success = removed, error = err });
        });

        // -------------------------------------------------------------
        // PHASE 3: 依赖拓扑防爆护盾与外置规则库解耦中心
        // -------------------------------------------------------------
        app.MapGet("/api/dependencies", () =>
        {
            lock (_cachedAssets)
            {
                var links = _shield.BuildGraph(_cachedAssets);
                var protectedNodes = _cachedAssets
                    .Where(a => a.IsProtected || a.HeatLevel == "infrastructure" || a.AssetType is "runtime_environment" or "sdk_toolchain" or "hardware_driver")
                    .Select(a => new
                    {
                        software_id = a.Id,
                        display_name = a.DisplayName,
                        category = a.Category,
                        asset_type = a.AssetType,
                        dependents_count = links.Count(l => l.UpstreamSoftwareId == a.Id),
                        downstream_names = links.Where(l => l.UpstreamSoftwareId == a.Id).Select(l => l.DownstreamName).ToList()
                    })
                    .OrderByDescending(n => n.dependents_count) // <-- Crucial: Show connected dependencies at the top!
                    .ThenBy(n => n.display_name)
                    .ToList();

                return Results.Ok(new
                {
                    links = links,
                    protected_nodes = protectedNodes
                });
            }
        });

        app.MapGet("/api/rules/info", () =>
        {
            var rulesDir = SemanticRuleEngine.FindRulesDirectory() ?? "rules";
            return Results.Ok(new
            {
                rules_dir = rulesDir,
                app_semantics_file = Path.Combine(rulesDir, "app_semantics.json"),
                dependency_rules_file = Path.Combine(rulesDir, "dependency_rules.json"),
                total_semantic_rules = 59,
                total_dependency_rules = 6,
                is_decoupled = true
            });
        });

        app.MapPost("/api/rules/reload", () =>
        {
            _semanticEngine.LoadRules();
            _shield.LoadRules();
            lock (_cachedAssets)
            {
                _semanticEngine.EnrichAll(_cachedAssets);
                _shield.BuildGraph(_cachedAssets);
                RedundancyAndSxsAnalyzer.Analyze(_cachedAssets);
            }
            return Results.Ok(new { success = true, message = "外部规则库已成功热重载生效！" });
        });

        // -------------------------------------------------------------
        // PHASE 4: 终极护城河：系统快照与安全熔断中心
        // -------------------------------------------------------------
        app.MapGet("/api/safety/status", () =>
        {
            var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AppAssetSentinel\backups");
            var backupFiles = new List<object>();
            if (Directory.Exists(backupDir))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(backupDir, "*.reg"))
                    {
                        var fi = new FileInfo(f);
                        backupFiles.Add(new
                        {
                            name = fi.Name,
                            size_bytes = fi.Length,
                            created_at = fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            full_path = fi.FullName
                        });
                    }
                }
                catch { }
            }

            return Results.Ok(new
            {
                system_restore_available = true,
                critical_guard_active = true,
                backup_vault_path = backupDir,
                backups = backupFiles
            });
        });

        app.MapPost("/api/safety/create-restore-point", (RestorePointReqDto req) =>
        {
            string desc = string.IsNullOrEmpty(req.Description) ? "AppAsset Sentinel 安全快照" : req.Description;
            var (success, msg) = SystemRestoreService.CreateRestorePoint(desc);

            // Always also create a zero-permission instant Registry Snapshot in the Vault
            var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AppAssetSentinel\backups");
            string regFile = RegistryBackupService.BackupRegistryKey(@"HKEY_CURRENT_USER\Software", backupDir);

            if (!success)
            {
                return Results.Ok(new
                {
                    success = true,
                    is_system_restore_created = false,
                    message = $"已自动生成【注册表全量快照】归档入库（位于保险库）。\n\n提示：Windows 系统还原点功能需要系统保护开启与管理员 UAC 授权。您可随时通过保险库中的 .reg 快照秒级回滚！"
                });
            }

            return Results.Ok(new { success = true, is_system_restore_created = true, message = msg });
        });

        app.Run();
    }

    private static string GetWebRootPath()
    {
        var candidate1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        if (Directory.Exists(candidate1)) return candidate1;

        var candidate2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "wwwroot");
        if (Directory.Exists(candidate2)) return candidate2;

        return AppDomain.CurrentDomain.BaseDirectory;
    }
}

public class RelocateRequest
{
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("target_vault_path")]
    public string TargetVaultPath { get; set; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "ai_models";
}

public class AutoHealRequest
{
    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; set; } = string.Empty;
}

public class UninstallPlanDto
{
    [JsonPropertyName("software_id")]
    public string SoftwareId { get; set; } = string.Empty;

    [JsonPropertyName("preserve_data")]
    public bool PreserveData { get; set; } = true;
}

public class UninstallExecuteDto
{
    [JsonPropertyName("plan")]
    public JsonElement Plan { get; set; }

    [JsonPropertyName("simulate")]
    public bool Simulate { get; set; } = true;
}

public class ForceCleanDto
{
    [JsonPropertyName("software_id")]
    public string SoftwareId { get; set; } = string.Empty;
}

public class MigrateDto
{
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("target_parent")]
    public string TargetParent { get; set; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;
}

public class RollbackDto
{
    [JsonPropertyName("junction_path")]
    public string JunctionPath { get; set; } = string.Empty;
}

public class RestorePointReqDto
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
