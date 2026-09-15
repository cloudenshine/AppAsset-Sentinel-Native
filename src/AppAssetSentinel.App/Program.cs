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
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Operations;
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

    /// <summary>
    /// Single capability gate. Every write route consults this (AUDIT W01).
    /// Default is the safe R0 posture; the R1 relocation profile is opt-in via --profile r1
    /// because R1 acceptance additionally requires an isolated account and two real test volumes.
    /// </summary>
    private static CapabilityPolicy _policy = CapabilityPolicy.SafeObservationDefault();

    /// <summary>Write-ahead operation log; the recovery source of truth (AUDIT W06).</summary>
    private static readonly OperationLog _operationLog = new(OperationLog.DefaultDirectory);

    /// <summary>AUDIT A28: inventory runs in the background; the UI reads this state.</summary>
    private static readonly ScanStatus _scanStatus = new();

    private static readonly object _scanGate = new();
    private static bool _scanRunning;

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
        // Select the posture first: both the CLI and the HTTP surface must report and enforce
        // the same policy, so it is resolved before either can observe it.
        if (args.Any(a => a.Equals("--profile=r1", StringComparison.OrdinalIgnoreCase)))
        {
            _policy = CapabilityPolicy.RelocationVerifiedProfile();
            Console.WriteLine("[!] R1 迁移档已启用：仅开放已验收的写入能力。");
        }

        // AUDIT W12: read-only command surface, sharing this process's capability policy so a
        // scripted caller cannot obtain a capability the GUI would refuse.
        if (args.Any(IsCommand))
        {
            Environment.ExitCode = CommandLine.Run(args, _policy);
            return;
        }

        Console.WriteLine("============================================================");
        Console.WriteLine("  AppAsset Sentinel (Native) - 安全观察版 R0");
        Console.WriteLine("  能力门控: 只读观察 / 计划预览已开放，写入能力按审计门槛关闭");
        Console.WriteLine($"  本地服务地址: {BaseUrl}");
        Console.WriteLine("============================================================");

        _semanticEngine = new SemanticRuleEngine();
        _shield = new DependencyShield();
        ReportPackagingIntegrity();

        // AUDIT A28: serve the last known inventory immediately instead of blocking the
        // window on a full scan, then refresh in the background.
        LoadCachedInventory();
        StartBackgroundScan();

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

        // AUDIT A28: wait for a *real* readiness signal instead of a fixed delay. The probe
        // asks the server whether it is accepting connections, so a slow machine no longer
        // opens the window against a port that is not listening yet.
        bool ready = WaitForServerReady(TimeSpan.FromSeconds(20));
        Console.WriteLine(ready
            ? "[OK] 本地服务已就绪。"
            : "[!] 本地服务在超时内未就绪；界面可能无法加载数据。");

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

    /// <summary>
    /// AUDIT A26: the program loads wwwroot/ and rules/ from disk. A missing one must be
    /// reported plainly at startup, because a source-tree run can otherwise mask a broken
    /// package and the failure would only appear as silently missing behaviour.
    /// </summary>
    /// <summary>
    /// True when the invocation is a read-only command rather than the GUI or server.
    /// Global flags such as --profile are allowed alongside a verb.
    /// </summary>
    private static bool IsCommand(string arg) =>
        CommandLine.IsCommand(arg) || CommandLine.IsGlobalFlag(arg);

    private static void ReportPackagingIntegrity()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string webRoot = GetWebRootPath();
        string indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            Console.WriteLine($"[!] 分发包不完整：未找到界面文件 {indexPath}");
        }
        else
        {
            Console.WriteLine($"[OK] 界面资源: {webRoot}");
        }

        var rulesDir = SemanticRuleEngine.FindRulesDirectory();
        if (rulesDir == null)
        {
            Console.WriteLine($"[!] 分发包不完整：在 {baseDir} 及其父目录中未找到 rules/app_semantics.json");
            Console.WriteLine($"    软件画像规则将无法加载，界面会显示通用说明而不是真实画像。");
        }
        else
        {
            string rulesFile = Path.Combine(rulesDir, "app_semantics.json");
            var candidate = SemanticRuleEngine.TryLoadCandidate(rulesFile);
            Console.WriteLine(candidate.Succeeded
                ? $"[OK] 规则库: {rulesDir}（{candidate.Rules.Count} 条画像规则）"
                : $"[!] 规则库存在但校验未通过：{candidate.Error}");
        }

        var vendor = Path.Combine(webRoot, "vendor", "tailwind.min.js");
        Console.WriteLine(File.Exists(vendor)
            ? "[OK] 本地前端资源: wwwroot/vendor"
            : "[!] 缺少本地前端资源 wwwroot/vendor/tailwind.min.js（界面将无样式）");
    }

    /// <summary>
    /// AUDIT A28/A13: loads the previous inventory so the UI has something honest to show
    /// while a fresh scan runs. The snapshot is labelled as cached in the scan status.
    /// </summary>
    private static void LoadCachedInventory()
    {
        var loaded = ScanSnapshotCache.Load();
        if (!loaded.Succeeded)
        {
            Console.WriteLine($"[!] 缓存清单损坏，将忽略并重新扫描：{loaded.Error}");
            return;
        }

        if (loaded.FileMissing || loaded.Assets.Count == 0)
        {
            Console.WriteLine("[*] 无可用缓存清单，等待首次扫描完成。");
            _scanStatus.Phase = ScanPhase.NeverScanned;
            return;
        }

        lock (_cachedAssets)
        {
            _cachedAssets.Clear();
            _cachedAssets.AddRange(loaded.Assets);
        }

        _scanStatus.ServingCachedSnapshot = true;
        _scanStatus.AssetCount = loaded.Assets.Count;
        Console.WriteLine($"[OK] 已载入缓存清单：{loaded.Assets.Count} 项（采集于 {loaded.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC），后台正在刷新。");
    }

    /// <summary>Starts a scan without blocking the caller. A second call while running is a no-op.</summary>
    private static bool StartBackgroundScan()
    {
        lock (_scanGate)
        {
            if (_scanRunning)
            {
                return false;
            }

            _scanRunning = true;
        }

        _scanStatus.Phase = ScanPhase.Scanning;
        _scanStatus.StartedAtUtc = DateTime.UtcNow;
        _scanStatus.LastError = string.Empty;

        var thread = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Console.WriteLine("[*] 后台扫描：Win32 注册表、便携资产与多维遥测...");

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

                // Persist so the next start can render immediately.
                try
                {
                    ScanSnapshotCache.Save(scanned);
                }
                catch (Exception ex)
                {
                    _scanStatus.LastError = $"快照保存失败：{ex.Message}";
                }

                sw.Stop();
                _scanStatus.Phase = ScanPhase.Completed;
                _scanStatus.AssetCount = scanned.Count;
                _scanStatus.DurationMs = sw.ElapsedMilliseconds;
                _scanStatus.CompletedAtUtc = DateTime.UtcNow;
                _scanStatus.ServingCachedSnapshot = false;
                Console.WriteLine($"[OK] 扫描完成：{scanned.Count} 项，耗时 {sw.ElapsedMilliseconds} ms。");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _scanStatus.Phase = ScanPhase.Failed;
                _scanStatus.DurationMs = sw.ElapsedMilliseconds;
                _scanStatus.LastError = ex.Message;
                Console.WriteLine($"[!] 扫描失败：{ex.Message}（继续提供上一次结果）");
            }
            finally
            {
                lock (_scanGate)
                {
                    _scanRunning = false;
                }
            }
        })
        {
            IsBackground = true,
            Name = "AppAssetSentinel.Scan"
        };

        thread.Start();
        return true;
    }

    /// <summary>Blocking scan used by the headless command, where nothing is waiting on a window.</summary>
    private static void RefreshAssets()
    {
        StartBackgroundScan();

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_scanGate)
            {
                if (!_scanRunning)
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }
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
        // AUDIT A28: the launcher polls this instead of sleeping a fixed 800 ms.
        // Reaching it at all means the HTTP server is accepting connections.
        app.MapGet("/api/ready", () => Results.Ok(new
        {
            ready = true,
            scan_phase = _scanStatus.Phase.ToString(),
            serving_cached_snapshot = _scanStatus.ServingCachedSnapshot,
            asset_count = _scanStatus.AssetCount
        }));

        app.MapGet("/api/scan/status", () => Results.Ok(_scanStatus));

        app.MapGet("/api/policy", () => Results.Ok(new
        {
            profile = _policy.AllowsMutation(Capability.VaultRelocate) ? "R1-relocation-verified" : "R0-safe-observation",
            capabilities = _policy.Describe()
        }));

        // -------------------------------------------------------------
        // AUDIT W06: on restart, the log — not any cached status — says what
        // actually happened and which resources are still mid-operation.
        // -------------------------------------------------------------
        app.MapGet("/api/operations", () =>
        {
            var records = _operationLog.LoadAll();
            return Results.Ok(new
            {
                log_directory = OperationLog.DefaultDirectory,
                total = records.Count,
                unfinished = records.Count(r => r.State is OperationState.Copying
                    or OperationState.Copied or OperationState.Verified
                    or OperationState.Switched or OperationState.NeedsAttention),
                records = records.Select(r => new
                {
                    task_id = r.TaskId,
                    asset_name = r.AssetName,
                    state = r.State.ToString(),
                    source_path = r.SourcePath,
                    target_path = r.TargetPath,
                    source_backup_path = r.SourceBackupPath,
                    staging_path = r.StagingPath,
                    backup_disposition = r.BackupDisposition.ToString(),
                    conflicts = r.Conflicts.Count,
                    recovery_note = r.RecoveryNote,
                    created_at = r.CreatedAt,
                    updated_at = r.UpdatedAt
                })
            });
        });

        app.MapGet("/api/operations/{taskId}", (string taskId) =>
        {
            var loaded = _operationLog.Load(taskId);
            if (!loaded.Succeeded)
            {
                return Results.Ok(new { status = "Failed", code = "log_corrupt", message = loaded.Error });
            }

            return loaded.FileMissing
                ? Results.Ok(new { status = "Failed", code = "log_missing", message = "不存在该任务记录。" })
                : Results.Ok(loaded.Record);
        });

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
                    // AUDIT A28/W04: say whether this is cached or current, so the UI never
                    // presents stale data as if it were freshly verified.
                    scan_phase = _scanStatus.Phase.ToString(),
                    serving_cached_snapshot = _scanStatus.ServingCachedSnapshot,
                    scan_error = _scanStatus.LastError,
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
            // AUDIT A28: return immediately and let the UI follow /api/scan/status, so a
            // running scan never freezes the interface.
            bool started = StartBackgroundScan();

            return Results.Ok(new
            {
                status = started ? "scanning" : "already_running",
                did_mutate = false,
                scan_phase = _scanStatus.Phase.ToString(),
                serving_cached_snapshot = _scanStatus.ServingCachedSnapshot,
                asset_count = _cachedAssets.Count
            });
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
            // AUDIT W07: the confirmed target path is passed through verbatim; the kernel
            // enforces boundary, staging exclusivity, per-file verification and conflict
            // preservation, and every step is written to the log before it happens.
            var result = MigrationKernel.Execute(_policy, _operationLog, new MigrationRequest
            {
                SourcePath = req.SourcePath,
                TargetPath = req.TargetVaultPath,
                AssetName = req.AssetName,
                Category = req.Category
            });

            return Results.Ok(result.Outcome);
        });

        // -------------------------------------------------------------
        // AUDIT W08: the first production adapter. Discovery and the service probe are
        // strictly read-only; the response states the mechanism that would be used and
        // whether the required acceptance environment is present.
        // -------------------------------------------------------------
        app.MapGet("/api/adapters/ollama", () =>
        {
            var instance = OllamaAdapter.Discover();
            var (mechanism, reason) = OllamaAdapter.ChooseMechanism(instance);

            return Results.Ok(new
            {
                instance,
                mechanism = mechanism.ToString(),
                mechanism_reason = reason,
                relocation_enabled = _policy.AllowsMutation(Capability.VaultRelocate),
                // Never claim a verified migration without a real, isolated run.
                acceptance = new
                {
                    discovery = "performed_read_only",
                    service_probe = instance.ServiceRunning ? "reachable" : "unreachable",
                    config_redirect_switch = "unit_verified_in_temp_tree",
                    cross_volume_run = "not_run",
                    real_app_restart_inference = "not_run",
                    note = "跨卷迁移与真实应用重启推理验收需要隔离账户与两个测试卷，尚未执行。"
                }
            });
        });

        // -------------------------------------------------------------
        // AUDIT W06/W07: Commit and Recover are separate, separately authorized actions.
        // A successful switch alone never reclaims space and is never treated as a recovery.
        // -------------------------------------------------------------
        app.MapPost("/api/vault/commit", (CommitRequest req) =>
        {
            var decision = _policy.Check(Capability.VaultCommit);
            if (!decision.IsAllowed)
            {
                return Results.Ok(OperationOutcome.Blocked(Capability.VaultCommit, decision.Reason));
            }

            var result = MigrationCommit.Commit(_operationLog, req.TaskId, req.Authorized);
            return Results.Ok(result);
        });

        app.MapPost("/api/vault/recover", (RecoverRequest req) =>
        {
            var decision = _policy.Check(Capability.VaultRecover);
            if (!decision.IsAllowed)
            {
                return Results.Ok(OperationOutcome.Blocked(Capability.VaultRecover, decision.Reason));
            }

            var result = MigrationCommit.Recover(_operationLog, req.TaskId, req.Authorized);
            return Results.Ok(result);
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

    /// <summary>
    /// Polls /api/ready until the embedded server answers. Reaching the endpoint at all is the
    /// readiness condition, so this measures the real thing rather than assuming a duration.
    /// </summary>
    private static bool WaitForServerReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = client.GetAsync($"{BaseUrl}/api/ready").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Not listening yet; keep waiting until the deadline.
            }

            Thread.Sleep(100);
        }

        return false;
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

public class CommitRequest
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = string.Empty;
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
}

public class RecoverRequest
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = string.Empty;
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
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
