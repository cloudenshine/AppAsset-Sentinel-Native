using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using AppAssetSentinel.Core.Policy;
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

    /// <summary>Single capability gate. Every write route consults this (AUDIT W01).</summary>
    private static readonly CapabilityPolicy _policy = CapabilityPolicy.SafeObservationDefault();

    /// <summary>
    /// Server-authored plans only. The client may reference a plan by id but can never
    /// hand back an executable plan document (AUDIT W03).
    /// </summary>
    private static readonly ConcurrentDictionary<string, UninstallPlanResult> _planStore = new();

    private const int ServerPort = 8765;
    private static readonly string BaseUrl = $"http://127.0.0.1:{ServerPort}";

    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  AppAsset Sentinel (Native) - 安全观察版 R0");
        Console.WriteLine("  能力门控: 只读观察 / 计划预览已开放，写入能力按审计门槛关闭");
        Console.WriteLine($"  本地服务地址: {BaseUrl}");
        Console.WriteLine("============================================================");

        _semanticEngine = new SemanticRuleEngine();
        _shield = new DependencyShield();
        RefreshAssets();

        if (args.Length > 0 && args[0].Equals("--headless-scan", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[Top 10 Assets]");
            foreach (var a in _cachedAssets.Take(10))
            {
                Console.WriteLine($" - {a.DisplayName} [{a.Category}] (Heat: {a.HeatLevel}, Last: {a.LastUsedTimestamp})");
            }
            return;
        }

        var webThread = new Thread(() => StartWebServer(args)) { IsBackground = true };
        webThread.Start();

        if (args.Length > 0 && args[0].Equals("--server-only", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[*] Server running. Press Ctrl+C to stop.");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        try
        {
            var window = new PhotinoWindow()
                .SetTitle("AppAsset Sentinel | Windows 本地 AI 资产证据与安全保全控制台")
                .SetUseOsDefaultSize(false)
                .SetSize(1380, 920)
                .Center()
                .SetResizable(true);

            window.Load(BaseUrl);
            window.WaitForClose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Photino Fallback] {ex.Message}");
            Console.WriteLine($"[Photino Fallback] Falling back to default browser: {BaseUrl}");
            Process.Start(new ProcessStartInfo { FileName = BaseUrl, UseShellExecute = true });
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void RefreshAssets()
    {
        Console.WriteLine("[*] 正在执行全盘 Win32 注册表、便携资产与多维遥测扫描...");
        var sw = Stopwatch.StartNew();
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
        Console.WriteLine($"[OK] 扫描完成：{_cachedAssets.Count} 项，耗时 {sw.ElapsedMilliseconds} ms。");
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

        // -------------------------------------------------------------
        // AUDIT A08: local origin / host / session gate for state-changing calls.
        // Read-only GETs stay open so the UI can render before it holds a token.
        // -------------------------------------------------------------
        app.Use(async (context, next) =>
        {
            bool isStateChanging = HttpMethods.IsPost(context.Request.Method)
                                   || HttpMethods.IsPut(context.Request.Method)
                                   || HttpMethods.IsPatch(context.Request.Method)
                                   || HttpMethods.IsDelete(context.Request.Method);

            if (isStateChanging)
            {
                if (!LocalSecurity.IsAcceptableHost(context.Request.Host.Value, ServerPort))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "Blocked",
                        code = "host_not_allowed",
                        message = $"Host 头不被接受：{context.Request.Host.Value}"
                    });
                    return;
                }

                string? origin = context.Request.Headers.Origin;
                if (!LocalSecurity.IsAcceptableOrigin(origin, ServerPort))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "Blocked",
                        code = "origin_not_allowed",
                        message = $"跨站来源被拒绝：{origin}"
                    });
                    return;
                }

                if (!LocalSecurity.IsTokenValid(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "Blocked",
                        code = "session_required",
                        message = $"缺少或无效的 {LocalSecurity.SessionHeader} 会话令牌。"
                    });
                    return;
                }
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // The page fetches this on load; a remote origin cannot read the response body.
        app.MapGet("/api/session", () => Results.Ok(new
        {
            header = LocalSecurity.SessionHeader,
            token = LocalSecurity.Token,
            profile = "R0-safe-observation"
        }));

        // -------------------------------------------------------------
        // Capability posture (read-only, always safe)
        // -------------------------------------------------------------
        app.MapGet("/api/policy", () => Results.Ok(new
        {
            profile = "R0-safe-observation",
            capabilities = _policy.Describe()
        }));

        // -------------------------------------------------------------
        // Read-only observation
        // -------------------------------------------------------------
        app.MapGet("/api/overview", () =>
        {
            lock (_cachedAssets)
            {
                long totalBytes = _cachedAssets.Sum(a => a.EstimatedSizeBytes);
                var heatDist = new Dictionary<string, int>
                {
                    ["hot"] = 0, ["warm"] = 0, ["cooling"] = 0, ["zombie"] = 0, ["unknown"] = 0, ["infrastructure"] = 0
                };
                var catDist = new Dictionary<string, int>();
                int zombies = 0, protectedInfra = 0, unknownUsage = 0, incompleteSizeCount = 0;
                long zombieReclaimable = 0;

                foreach (var a in _cachedAssets)
                {
                    var level = string.IsNullOrEmpty(a.HeatLevel) ? "unknown" : a.HeatLevel;
                    heatDist[level] = heatDist.GetValueOrDefault(level, 0) + 1;

                    var cat = string.IsNullOrEmpty(a.Category) ? "tools_utility" : a.Category;
                    catDist[cat] = catDist.GetValueOrDefault(cat, 0) + 1;

                    if (level == "infrastructure" || a.IsProtected)
                    {
                        protectedInfra++;
                    }
                    else if (level == "unknown")
                    {
                        unknownUsage++;
                    }
                    else if (level == "zombie")
                    {
                        zombies++;
                        zombieReclaimable += a.EstimatedSizeBytes;
                    }

                    // AUDIT A22: surface how many figures could not be fully measured.
                    if (!a.SizeMeasurementComplete) incompleteSizeCount++;
                }

                return Results.Ok(new
                {
                    total_apps = _cachedAssets.Count,
                    total_size_bytes = totalBytes,
                    total_size_formatted = $"{Math.Round((double)totalBytes / (1024 * 1024 * 1024), 2)} GB",
                    // AUDIT A22: this is the sum of measurably-sized install directories,
                    // not the volume's allocated bytes for the application.
                    size_scope = "sum_of_measured_install_directories",
                    size_measurement_incomplete_count = incompleteSizeCount,
                    zombie_apps_count = zombies,
                    protected_infra_count = protectedInfra,
                    unknown_usage_count = unknownUsage,
                    zombie_reclaimable_formatted = $"{Math.Round((double)zombieReclaimable / (1024 * 1024 * 1024), 2)} GB",
                    heat_distribution = heatDist,
                    category_distribution = catDist
                });
            }
        });

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
                return Results.Ok(new { apps = list, total = list.Count });
            }
        });

        app.MapPost("/api/scan", () =>
        {
            RefreshAssets();
            return Results.Ok(new { status = "observed", count = _cachedAssets.Count, did_mutate = false });
        });

        // -------------------------------------------------------------
        // Planning (read-only) + simulation (no mutation)
        // -------------------------------------------------------------
        app.MapPost("/api/uninstall/plan", (UninstallPlanDto req) =>
        {
            lock (_cachedAssets)
            {
                var plan = UninstallerEngine.PlanUninstall(req.SoftwareId, _cachedAssets, _shield, _policy, req.PreserveData);
                if (plan.Success && !string.IsNullOrEmpty(plan.PlanId))
                {
                    _planStore[plan.PlanId] = plan;
                }
                return Results.Ok(plan);
            }
        });

        app.MapPost("/api/uninstall/execute", (JsonElement body) =>
        {
            string? planId = PlanRequestGuard.ExtractPlanId(body);
            bool simulate = true;
            try
            {
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("simulate", out var simEl))
                {
                    simulate = simEl.ValueKind != JsonValueKind.False;
                }
            }
            catch { }

            UninstallPlanResult? stored = null;
            if (!string.IsNullOrEmpty(planId))
            {
                _planStore.TryGetValue(planId, out stored);
            }

            var outcome = UninstallerEngine.ExecutePlan(_policy, stored, planId ?? string.Empty, simulate);
            return Results.Ok(outcome);
        });

        // AUDIT A02: no unguarded recursive delete route remains.
        app.MapPost("/api/uninstall/force-clean", (ForceCleanDto req) =>
        {
            var outcome = UninstallerEngine.ForceClean(_policy, null);
            return Results.Ok(outcome);
        });

        // -------------------------------------------------------------
        // Vault (W01: relocation is Blocked; observation is open)
        // -------------------------------------------------------------
        app.MapGet("/api/vault/volumes", () => Results.Ok(VolumeManager.GetSystemVolumes()));

        app.MapGet("/api/vault/candidates", () =>
        {
            lock (_cachedAssets)
            {
                return Results.Ok(MultiDomainAssetScanner.ScanAllDomains(_cachedAssets));
            }
        });

        app.MapPost("/api/vault/relocate", (RelocateRequest req) =>
        {
            var outcome = AssetVaultEngine.RelocateAndDualLock(
                _policy, req.SourcePath, req.TargetVaultPath, req.AssetName, req.Category);
            return Results.Ok(outcome);
        });

        app.MapGet("/api/vault/watchdog", () => Results.Ok(DriftWatchdog.InspectAndAuditDrifts(_policy)));

        app.MapPost("/api/vault/auto-heal", (AutoHealRequest req) =>
        {
            var outcome = DriftWatchdog.RepairDrift(_policy, req.RegistrationId);
            return Results.Ok(outcome);
        });

        // Legacy compatibility route: same gate, same result vocabulary.
        app.MapPost("/api/migrate", (MigrateDto req) =>
        {
            var outcome = AssetVaultEngine.RelocateAndDualLock(
                _policy, req.SourcePath, req.TargetParent, req.AssetName, "general");
            return Results.Ok(outcome);
        });

        app.MapPost("/api/migration/rollback", (RollbackDto req) =>
        {
            var decision = _policy.Check(Capability.JunctionUnlink);
            return Results.Ok(OperationOutcome.Blocked(Capability.JunctionUnlink, decision.Reason));
        });

        // -------------------------------------------------------------
        // Dependencies & rules
        // -------------------------------------------------------------
        app.MapGet("/api/dependencies", () =>
        {
            lock (_cachedAssets)
            {
                var links = _shield.BuildGraph(_cachedAssets);
                var protectedNodes = _cachedAssets
                    .Where(a => a.IsProtected || a.HeatLevel == "infrastructure" ||
                                a.AssetType is "runtime_environment" or "sdk_toolchain" or "hardware_driver")
                    .Select(a => new
                    {
                        software_id = a.Id,
                        display_name = a.DisplayName,
                        category = a.Category,
                        asset_type = a.AssetType,
                        dependents_count = links.Count(l => l.UpstreamSoftwareId == a.Id),
                        downstream_names = links.Where(l => l.UpstreamSoftwareId == a.Id).Select(l => l.DownstreamName).ToList()
                    })
                    .OrderByDescending(n => n.dependents_count)
                    .ThenBy(n => n.display_name)
                    .ToList();

                return Results.Ok(new
                {
                    links,
                    protected_nodes = protectedNodes,
                    // AUDIT A16: the evidence graph keeps every edge; this is informational.
                    cyclic_edges_preserved = DependencyShield.CountCyclicEdges(links)
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
                is_decoupled = true
            });
        });

        app.MapPost("/api/rules/reload", () =>
        {
            var decision = _policy.Check(Capability.RulesReload);
            if (!decision.IsAllowed)
            {
                return Results.Ok(OperationOutcome.Blocked(Capability.RulesReload, decision.Reason));
            }

            // AUDIT A17: validate the new snapshot before replacing the known-good one.
            var candidate = SemanticRuleEngine.TryLoadCandidate();
            if (!candidate.Succeeded)
            {
                return Results.Ok(OperationOutcome.Failed(Capability.RulesReload, "invalid_rules",
                    $"新规则未能通过校验，已保留上一版有效规则：{candidate.Error}"));
            }

            _semanticEngine.ReplaceRules(candidate.Rules);
            _shield.LoadRules();
            lock (_cachedAssets)
            {
                _semanticEngine.EnrichAll(_cachedAssets);
                _shield.BuildGraph(_cachedAssets);
                RedundancyAndSxsAnalyzer.Analyze(_cachedAssets);
            }

            return Results.Ok(new OperationOutcome
            {
                Status = OperationStatus.Succeeded,
                Capability = Capability.RulesReload,
                DidMutate = false,
                Code = "rules_reloaded",
                Message = $"已加载 {candidate.Rules.Count} 条画像规则（上一版在失败时可回退）。"
            });
        });

        // -------------------------------------------------------------
        // Safety
        // -------------------------------------------------------------
        app.MapGet("/api/safety/status", () =>
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"AppAssetSentinel\backups");

            var backups = new List<object>();
            if (Directory.Exists(backupDir))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(backupDir, "*.reg"))
                    {
                        var fi = new FileInfo(f);
                        backups.Add(new
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
                restore_point_state = _policy.Check(Capability.RestorePointCreate).State.ToString(),
                restore_point_reason = _policy.Check(Capability.RestorePointCreate).Reason,
                critical_guard_active = true,
                backup_vault_path = backupDir,
                backups
            });
        });

        app.MapPost("/api/safety/create-restore-point", (RestorePointReqDto req) =>
        {
            var decision = _policy.Check(Capability.RestorePointCreate);

            // The registry archive is evidence we can actually verify, so always attempt it.
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"AppAssetSentinel\backups");
            var backup = RegistryBackupService.BackupRegistryKey(@"HKEY_CURRENT_USER\Software", backupDir);

            if (!decision.IsAllowed)
            {
                return Results.Ok(new
                {
                    status = OperationStatus.Blocked.ToString(),
                    did_mutate = backup.Succeeded,
                    restore_point_created = false,
                    registry_backup_succeeded = backup.Succeeded,
                    registry_backup_path = backup.Path,
                    registry_backup_scope = backup.Scope,
                    message = $"{decision.Reason} 已改用可验证的注册表归档作为恢复材料。"
                });
            }

            var rp = SystemRestoreService.CreateRestorePoint(req.Description);
            return Results.Ok(new
            {
                status = (rp.Succeeded ? OperationStatus.Succeeded : OperationStatus.Failed).ToString(),
                did_mutate = rp.Succeeded || backup.Succeeded,
                restore_point_created = rp.Succeeded,
                registry_backup_succeeded = backup.Succeeded,
                registry_backup_path = backup.Path,
                registry_backup_scope = backup.Scope,
                message = rp.Succeeded
                    ? "系统还原点已创建。"
                    : $"系统还原点未创建：{rp.Message}（注册表归档：{(backup.Succeeded ? "成功" : backup.Error)}）"
            });
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
    [JsonPropertyName("source_path")] public string SourcePath { get; set; } = string.Empty;
    [JsonPropertyName("target_vault_path")] public string TargetVaultPath { get; set; } = string.Empty;
    [JsonPropertyName("asset_name")] public string AssetName { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = "ai_models";
}

public class AutoHealRequest
{
    [JsonPropertyName("registration_id")] public string RegistrationId { get; set; } = string.Empty;
}

public class UninstallPlanDto
{
    [JsonPropertyName("software_id")] public string SoftwareId { get; set; } = string.Empty;
    [JsonPropertyName("preserve_data")] public bool PreserveData { get; set; } = true;
}

public class ForceCleanDto
{
    [JsonPropertyName("software_id")] public string SoftwareId { get; set; } = string.Empty;
}

public class MigrateDto
{
    [JsonPropertyName("source_path")] public string SourcePath { get; set; } = string.Empty;
    [JsonPropertyName("target_parent")] public string TargetParent { get; set; } = string.Empty;
    [JsonPropertyName("asset_name")] public string AssetName { get; set; } = string.Empty;
}

public class RollbackDto
{
    [JsonPropertyName("junction_path")] public string JunctionPath { get; set; } = string.Empty;
}

public class RestorePointReqDto
{
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
}
