using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Safety;

namespace AppAssetSentinel.Core.Scanner;

public class TelemetryEngine
{
    private static readonly string[] ShortcutDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
    };

    private static readonly string[] AppDataDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow")
    };

    private static readonly HashSet<string> SystemProcessesBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "conhost", "svchost", "powershell", "pwsh", "explorer",
        "runtimebroker", "taskhostw", "sihost", "ctfmon", "services",
        "lsass", "csrss", "smss", "wininit", "dwm", "wscript", "cscript",
        "rundll32", "msiexec", "regsvr32", "searchhost", "startmenuexperiencehost",
        "smartscreen", "securityhealthsystray", "securityhealthservice"
    };

    private static readonly HashSet<string> IgnoredGenericAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "corporation", "technology", "software",
        "google", "nvidia", "intel", "installer", "update", "service",
        "system", "setup", "helper", "tools", "desktop", "app", "bin",
        "exe", "the", "and", "for", "x64", "x86", "64位", "32位", "专业版", "个人版",
        "community", "common", "package", "cache", "data"
    };

    public void AnalyzeAll(List<SoftwareAsset> assets)
    {
        // 1. Gather Live Processes snapshot
        var runningProcs = new Dictionary<string, (string ExePath, DateTime StartTime)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string pName = p.ProcessName.ToLowerInvariant();
                    if (SystemProcessesBlacklist.Contains(pName)) continue;

                    string mainExe = string.Empty;
                    DateTime startTime = DateTime.Now;

                    try
                    {
                        mainExe = p.MainModule?.FileName ?? string.Empty;
                        startTime = p.StartTime;
                    }
                    catch { }

                    if (!runningProcs.ContainsKey(pName))
                    {
                        runningProcs[pName] = (mainExe, startTime);
                    }
                }
                catch { }
            }
        }
        catch { }

        // 2. Gather Running Services
        var runningServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var s in ServiceController.GetServices())
            {
                if (s.Status == ServiceControllerStatus.Running)
                {
                    runningServices.Add(s.ServiceName);
                    runningServices.Add(s.DisplayName);
                }
            }
        }
        catch { }

        // 3. Index Shortcuts (Desktop, Public Desktop, Start Menu, Taskbar)
        // Uses LastWriteTime or CreationTime (NEVER LastAccessTime which is polluted by file enumerations)
        var shortcutsIndex = new List<(string Name, DateTime Time, string Path)>();
        foreach (var dir in ShortcutDirs)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            var t = fi.LastWriteTime > fi.CreationTime ? fi.LastWriteTime : fi.CreationTime;
                            shortcutsIndex.Add((fi.Name.ToLowerInvariant(), t, fi.FullName));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // 4. Index AppData Top-Level Folders
        var appdataIndex = new List<(string Name, DateTime Time, string Path)>();
        foreach (var dir in AppDataDirs)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            var di = new DirectoryInfo(sub);
                            var t = di.LastWriteTime > di.CreationTime ? di.LastWriteTime : di.CreationTime;
                            appdataIndex.Add((di.Name.ToLowerInvariant(), t, di.FullName));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // 5. Analyze each asset
        foreach (var app in assets)
        {
            AnalyzeAsset(app, runningProcs, runningServices, shortcutsIndex, appdataIndex);
        }
    }

    private void AnalyzeAsset(
        SoftwareAsset app,
        Dictionary<string, (string ExePath, DateTime StartTime)> runningProcs,
        HashSet<string> runningServices,
        List<(string Name, DateTime Time, string Path)> shortcutsIndex,
        List<(string Name, DateTime Time, string Path)> appdataIndex)
    {
        var aliases = BuildAppAliases(app);
        string mainExe = app.MainExecutable.ToLowerInvariant();
        string installLoc = app.InstallLocation.TrimEnd('\\').ToLowerInvariant();

        // TIER 1: Running Process RIGHT NOW
        foreach (var alias in aliases)
        {
            if (SystemProcessesBlacklist.Contains(alias) || IgnoredGenericAliases.Contains(alias))
                continue;

            if (runningProcs.TryGetValue(alias, out var p) || runningProcs.TryGetValue($"{alias}.exe", out p))
            {
                if (!string.IsNullOrEmpty(p.ExePath))
                {
                    string pBase = Path.GetFileNameWithoutExtension(p.ExePath);
                    if (!SystemProcessesBlacklist.Contains(pBase))
                    {
                        app.IsRunning = true;
                        app.DaysSinceLastUse = 0;
                        app.LastUsedTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        app.TelemetrySource = $"live_process ({Path.GetFileName(p.ExePath)})";
                        app.HeatScore = 100.0;
                        app.HeatLevel = "hot";
                        return;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(mainExe) && File.Exists(mainExe))
        {
            var exeName = Path.GetFileName(mainExe);
            var exeStem = Path.GetFileNameWithoutExtension(mainExe);
            if (!SystemProcessesBlacklist.Contains(exeStem) && runningProcs.TryGetValue(exeStem, out var p))
            {
                app.IsRunning = true;
                app.DaysSinceLastUse = 0;
                app.LastUsedTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                app.TelemetrySource = $"live_process ({exeName})";
                app.HeatScore = 100.0;
                app.HeatLevel = "hot";
                return;
            }
        }

        // Path containment ONLY for specific non-system install locations
        if (!string.IsNullOrEmpty(installLoc) && installLoc.Length > 10 && !CriticalDirectoryGuard.IsCriticalDirectory(installLoc))
        {
            foreach (var (_, (pExe, _)) in runningProcs)
            {
                if (!string.IsNullOrEmpty(pExe) && pExe.StartsWith(installLoc, StringComparison.OrdinalIgnoreCase))
                {
                    string pBase = Path.GetFileNameWithoutExtension(pExe);
                    if (!SystemProcessesBlacklist.Contains(pBase))
                    {
                        app.IsRunning = true;
                        app.DaysSinceLastUse = 0;
                        app.LastUsedTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        app.TelemetrySource = $"live_process ({Path.GetFileName(pExe)})";
                        app.HeatScore = 100.0;
                        app.HeatLevel = "hot";
                        return;
                    }
                }
            }
        }

        // TIER 2: Active Windows Service
        if (app.AssetType is "hardware_driver" or "system_service")
        {
            foreach (var alias in aliases)
            {
                if (IgnoredGenericAliases.Contains(alias) || alias.Length < 4) continue;

                if (runningServices.Contains(alias) || runningServices.Any(s => s.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                {
                    app.IsRunning = true;
                    app.DaysSinceLastUse = 0;
                    app.LastUsedTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    app.TelemetrySource = $"active_service ({alias})";
                    app.HeatScore = 95.0;
                    app.HeatLevel = "infrastructure";
                    return;
                }
            }
        }

        DateTime? latestTime = null;
        string evidenceSource = "no_recent_interaction";

        // TIER 3: Shortcuts Activity (Matches link stem)
        foreach (var (lnkName, lnkTime, lnkPath) in shortcutsIndex)
        {
            string lnkStem = Path.GetFileNameWithoutExtension(lnkName).ToLowerInvariant();
            if (IgnoredGenericAliases.Contains(lnkStem)) continue;

            foreach (var alias in aliases)
            {
                if (IgnoredGenericAliases.Contains(alias)) continue;

                if (lnkStem.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                    (alias.Length >= 3 && lnkStem.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) ||
                    (alias.Length >= 5 && lnkStem.Contains(alias)))
                {
                    if (latestTime == null || lnkTime > latestTime.Value)
                    {
                        latestTime = lnkTime;
                        evidenceSource = $"desktop_shortcut ({Path.GetFileName(lnkPath)})";
                    }
                }
            }
        }

        // TIER 4: AppData Activity (Exact folder matching or deep inspection for specific suite)
        foreach (var (folderName, folderTime, folderPath) in appdataIndex)
        {
            if (IgnoredGenericAliases.Contains(folderName)) continue;

            foreach (var alias in aliases)
            {
                if (IgnoredGenericAliases.Contains(alias)) continue;

                if (folderName.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    DateTime effectiveTime = folderTime;
                    try
                    {
                        var di = new DirectoryInfo(folderPath);
                        foreach (var f in di.EnumerateFiles("*.*", new EnumerationOptions { MaxRecursionDepth = 2, RecurseSubdirectories = true }).Take(30))
                        {
                            var fTime = f.LastWriteTime > f.CreationTime ? f.LastWriteTime : f.CreationTime;
                            if (fTime > effectiveTime && fTime <= DateTime.Now) effectiveTime = fTime;
                        }
                    }
                    catch { }

                    if (latestTime == null || effectiveTime > latestTime.Value)
                    {
                        latestTime = effectiveTime;
                        evidenceSource = $"appdata_activity ({Path.GetFileName(folderPath)})";
                    }
                }
            }
        }

        // TIER 5: Executable File Last Write Time (NEVER LastAccessTime)
        if (!string.IsNullOrEmpty(app.MainExecutable) && File.Exists(app.MainExecutable))
        {
            try
            {
                var fi = new FileInfo(app.MainExecutable);
                var fTime = fi.LastWriteTime > fi.CreationTime ? fi.LastWriteTime : fi.CreationTime;
                if (fTime <= DateTime.Now && (latestTime == null || fTime > latestTime.Value))
                {
                    latestTime = fTime;
                    evidenceSource = "executable_file_stamp";
                }
            }
            catch { }
        }

        // TIER 6: Install Date
        if (latestTime == null && !string.IsNullOrEmpty(app.InstallDate) && app.InstallDate.Length == 8)
        {
            if (DateTime.TryParseExact(app.InstallDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var idt))
            {
                if (idt <= DateTime.Now)
                {
                    latestTime = idt;
                    evidenceSource = "install_date_registration";
                }
            }
        }

        // AUDIT A13: never fabricate a date. The previous implementation hard-filled
        // "180 days ago" and then labelled the app a zombie, turning "we did not observe
        // any use" into the false factual claim "it has not been used for 180 days".
        if (latestTime == null)
        {
            app.HeatLevel = "unknown";
            app.HeatScore = 0.0;
            app.DaysSinceLastUse = null;
            app.LastUsedTimestamp = string.Empty;
            app.TelemetrySource = "no_evidence_observed";
            app.UsageConfidence = "unknown";
            app.UsageEvidence.Add("未观察到进程、服务、快捷方式或配置写入等活动证据");
            return;
        }

        int daysAgo = Math.Max(0, (int)(DateTime.Now - latestTime.Value).TotalDays);
        app.DaysSinceLastUse = daysAgo;
        app.LastUsedTimestamp = latestTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
        app.TelemetrySource = evidenceSource;

        // Rule for Infrastructure
        if (app.AssetType is "hardware_driver" or "system_service" or "sdk_toolchain" or "runtime_environment" || app.IsProtected)
        {
            app.HeatScore = 95.0;
            app.HeatLevel = "infrastructure";
            app.UsageConfidence = "confirmed";
            return;
        }

        // AUDIT A13: file mtimes prove *something touched a file*, not that a human used
        // the application. Treat them as inferred evidence so the UI can be honest, and
        // never let inferred evidence alone produce a "zombie" verdict.
        bool weakEvidenceOnly = evidenceSource == "executable_file_stamp"
                                || evidenceSource == "install_date_registration";
        app.UsageConfidence = weakEvidenceOnly ? "inferred" : "confirmed";
        app.UsageEvidence.Add(evidenceSource);

        // Normal applications: strict heat gradient
        if (daysAgo <= 3)
        {
            app.HeatScore = Math.Round(90.0 + (3 - daysAgo) * 3.3, 1);
            app.HeatLevel = "hot";
        }
        else if (daysAgo <= 14)
        {
            app.HeatScore = Math.Round(75.0 + (14 - daysAgo) * 1.3, 1);
            app.HeatLevel = "hot";
        }
        else if (daysAgo <= 30)
        {
            app.HeatScore = Math.Round(50.0 + (30 - daysAgo) * 1.5, 1);
            app.HeatLevel = "warm";
        }
        else if (daysAgo <= 90)
        {
            app.HeatScore = Math.Round(20.0 + (90 - daysAgo) * 0.5, 1);
            app.HeatLevel = "cooling";
        }
        else if (weakEvidenceOnly)
        {
            // Enough to say "likely idle", not enough to say "safe to delete".
            app.HeatScore = 15.0;
            app.HeatLevel = "cooling";
            app.UsageEvidence.Add("仅凭文件时间推断，不足以判定为可清理的僵尸应用");
        }
        else
        {
            app.HeatScore = Math.Max(0.0, 20.0 - (daysAgo - 90) * 0.1);
            app.HeatLevel = "zombie";
        }
    }

    private static List<string> BuildAppAliases(SoftwareAsset app)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string name = app.DisplayName.ToLowerInvariant().Trim();
        string clean = Regex.Replace(name, @"[^a-zA-Z0-9\u4e00-\u9fa5]", "");

        if (!string.IsNullOrEmpty(name)) aliases.Add(name);
        if (!string.IsNullOrEmpty(clean)) aliases.Add(clean);

        // Extract clean brand/product stem (strip version, year numbers, parentheses, editions)
        string stripped = Regex.Replace(name, @"(\d{4}|\bv?\d+(\.\d+)+\b|\([^)]*\)|（[^）]*）|专业版|个人版|社区版|企业版|x64|x86|64位|32位)", "").Trim();
        if (!string.IsNullOrEmpty(stripped) && stripped.Length >= 2 && !IgnoredGenericAliases.Contains(stripped))
        {
            aliases.Add(stripped.ToLowerInvariant());
        }

        // Special handling for major suites:
        if (name.Contains("wps"))
        {
            aliases.Add("wps");
            aliases.Add("kingsoft");
            aliases.Add("wps文字");
            aliases.Add("wps表格");
            aliases.Add("wps演示");
        }
        else if (name.Contains("jianying") || name.Contains("剪映"))
        {
            aliases.Add("jianying");
            aliases.Add("jianyingpro");
            aliases.Add("剪映");
            aliases.Add("剪映专业版");
        }

        // Main Executable name
        if (!string.IsNullOrEmpty(app.MainExecutable))
        {
            var stem = Path.GetFileNameWithoutExtension(app.MainExecutable).ToLowerInvariant();
            if (stem.Length >= 3 && !SystemProcessesBlacklist.Contains(stem) && !IgnoredGenericAliases.Contains(stem))
            {
                aliases.Add(stem);
            }
        }

        return aliases.Where(a => a.Length >= 2 && !IgnoredGenericAliases.Contains(a)).ToList();
    }
}
