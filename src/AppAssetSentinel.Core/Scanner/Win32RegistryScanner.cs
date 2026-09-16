using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Scanner;

[SupportedOSPlatform("windows")]
public class Win32RegistryScanner
{
    /// <summary>
    /// Dynamically discover standard portable software folders across all ready fixed drives.
    /// Never hardcodes private machine paths.
    /// </summary>
    public static List<string> GetDefaultPortableRoots()
    {
        var roots = new List<string> { @"C:\Tools", @"C:\PortableApps", @"C:\Programs" };
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                {
                    foreach (var folder in new[] { "Tools", "PortableApps", "Programs" })
                    {
                        string p = Path.Combine(drive.RootDirectory.FullName, folder);
                        if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            roots.Add(p);
                        }
                    }
                }
            }
        }
        catch { }
        return roots;
    }

    public static readonly HashSet<string> IgnoredPortableSubdirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmp", "temp", "cache", ".git", "node_modules", "site-packages",
        "resources", "bin", "locales", "lib", "models", "checkpoints", "loras", "weights", "plugins"
    };

    private static readonly string[] LocationRegistryKeys =
    {
        "InstallLocation", "LocationRoot", "InstallPath", "InstallDir", "Path", "TargetDir", "UninstallPath", "AppPath"
    };

    /// <summary>
    /// AUDIT W11: the optional token makes a long scan stoppable. The check runs inside the
    /// per-asset size loop, which is where nearly all the wall-clock time is spent, so
    /// cancellation is honoured promptly rather than only between top-level phases.
    /// </summary>
    public List<SoftwareAsset> ScanInstalledSoftware(
        bool includeSystemComponents = false,
        bool calculateDiskSize = true,
        CancellationToken cancellationToken = default)
    {
        var regApps = ScanRegistry(includeSystemComponents, calculateDiskSize, cancellationToken);
        var portableApps = ScanPortable(calculateDiskSize, cancellationToken);

        // Deduplication against registry apps
        var registeredLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in regApps)
        {
            if (!string.IsNullOrEmpty(app.InstallLocation)) registeredLocations.Add(Path.GetFullPath(app.InstallLocation).TrimEnd('\\'));
            if (!string.IsNullOrEmpty(app.MainExecutable)) registeredExes.Add(Path.GetFullPath(app.MainExecutable));
            if (!string.IsNullOrEmpty(app.DisplayName)) registeredNames.Add(app.DisplayName.ToLowerInvariant().Trim());
        }

        foreach (var pApp in portableApps)
        {
            string loc = string.IsNullOrEmpty(pApp.InstallLocation) ? "" : Path.GetFullPath(pApp.InstallLocation).TrimEnd('\\');
            string exe = string.IsNullOrEmpty(pApp.MainExecutable) ? "" : Path.GetFullPath(pApp.MainExecutable);
            string name = pApp.DisplayName.ToLowerInvariant().Trim();

            if (!registeredLocations.Contains(loc) && !registeredExes.Contains(exe) && !registeredNames.Contains(name))
            {
                regApps.Add(pApp);
            }
        }

        return regApps.OrderBy(x => x.DisplayName).ToList();
    }

    public List<SoftwareAsset> ScanRegistry(
        bool includeSystemComponents = false,
        bool calculateDiskSize = true,
        CancellationToken cancellationToken = default)
    {
        var rawItems = new List<SoftwareAsset>();
        var seenSigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortcutIndex = LoadShortcutTargetIndex();

        var registryTargets = new List<(RegistryHive Hive, RegistryView View, string SubKey)>
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, view, subKey) in registryTargets)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(subKey);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        // Skip Windows Updates
                        if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase) || displayName.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Check SystemComponent flag
                        var systemComponent = appKey.GetValue("SystemComponent");
                        if (!includeSystemComponents && systemComponent is int sc && sc == 1)
                        {
                            continue;
                        }

                        // Skip parent-linked child components (unless standalone)
                        var parentKey = appKey.GetValue("ParentKeyName")?.ToString();
                        if (!string.IsNullOrEmpty(parentKey))
                        {
                            continue;
                        }

                        var publisher = appKey.GetValue("Publisher")?.ToString()?.Trim() ?? string.Empty;
                        var displayVersion = appKey.GetValue("DisplayVersion")?.ToString()?.Trim() ?? string.Empty;

                        // Deduplication signature
                        string dedupSig = $"{displayName.ToLowerInvariant()}:{displayVersion.ToLowerInvariant()}";
                        if (!seenSigs.Add(dedupSig))
                            continue;

                        // Universal Multi-Key Location Extraction (handles LocationRoot, InstallPath, etc.)
                        string installLocation = string.Empty;
                        foreach (var lk in LocationRegistryKeys)
                        {
                            var val = appKey.GetValue(lk)?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(val))
                            {
                                val = Environment.ExpandEnvironmentVariables(val.Trim('"'));
                                if (Directory.Exists(val))
                                {
                                    installLocation = val;
                                    break;
                                }
                            }
                        }

                        var uninstallString = appKey.GetValue("UninstallString")?.ToString()?.Trim() ?? string.Empty;
                        var quietUninstallString = appKey.GetValue("QuietUninstallString")?.ToString()?.Trim() ?? string.Empty;
                        var installDate = appKey.GetValue("InstallDate")?.ToString()?.Trim() ?? string.Empty;
                        var displayIcon = appKey.GetValue("DisplayIcon")?.ToString()?.Trim() ?? string.Empty;

                        long estimatedSize = 0;
                        var sizeVal = appKey.GetValue("EstimatedSize");
                        if (sizeVal is int intSize && intSize > 0) estimatedSize = (long)intSize * 1024;
                        else if (sizeVal is long longSize && longSize > 0) estimatedSize = longSize * 1024;

                        // Heuristic: Infer InstallLocation if empty
                        if (string.IsNullOrWhiteSpace(installLocation))
                        {
                            installLocation = InferInstallLocation(uninstallString, displayIcon);
                        }

                        // Infer Main Executable
                        var mainExe = InferMainExecutable(installLocation, displayIcon, uninstallString);

                        // Cross-reference Desktop & Start Menu Shortcuts if exe or location is missing
                        if (string.IsNullOrEmpty(mainExe) || !File.Exists(mainExe))
                        {
                            string cleanAppName = Regex.Replace(displayName.ToLowerInvariant(), @"[^a-zA-Z0-9\u4e00-\u9fa5]", "");
                            foreach (var (key, (targetExe, workDir)) in shortcutIndex)
                            {
                                if (cleanAppName.Contains(key) || key.Contains(cleanAppName) || subKeyName.ToLowerInvariant().Contains(key))
                                {
                                    mainExe = targetExe;
                                    if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation))
                                    {
                                        installLocation = !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)
                                            ? workDir
                                            : Path.GetDirectoryName(targetExe) ?? "";
                                    }
                                    break;
                                }
                            }
                        }

                        var fullRegPath = $@"{hive}\{(view == RegistryView.Registry32 ? "WOW6432Node\\" : "")}{subKey}\{subKeyName}";

                        // Check ghost entry
                        bool hasExe = !string.IsNullOrEmpty(mainExe) && File.Exists(mainExe);
                        bool hasLoc = !string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation);
                        bool isGhost = (!hasExe && !hasLoc);

                        var asset = new SoftwareAsset
                        {
                            Id = GenerateStableId(displayName, publisher, installLocation, subKeyName),
                            DisplayName = displayName,
                            Publisher = publisher,
                            DisplayVersion = displayVersion,
                            InstallLocation = installLocation,
                            UninstallString = uninstallString,
                            QuietUninstallString = quietUninstallString,
                            InstallDate = installDate,
                            EstimatedSizeBytes = estimatedSize,
                            RegistryKeyPath = fullRegPath,
                            Architecture = view == RegistryView.Registry32 ? "x86" : "x64",
                            MainExecutable = mainExe,
                            IsGhostEntry = isGhost,
                            GhostReason = isGhost ? "本地主程序与安装路径均已脱机，属于系统孤立残留条目。" : string.Empty
                        };

                        // Check if install location is a Junction / Symlink and calculate real disk size
                        if (!string.IsNullOrEmpty(asset.InstallLocation) && Directory.Exists(asset.InstallLocation))
                        {
                            var junc = FastDirectorySizer.GetJunctionInfo(asset.InstallLocation);
                            asset.IsJunction = junc.IsJunction;
                            asset.JunctionTarget = junc.TargetPath;

                            // Checkpoint per asset: this loop is the slow part.
                            cancellationToken.ThrowIfCancellationRequested();

                            if (calculateDiskSize && !asset.IsJunction)
                            {
                                // AUDIT A22: keep the completeness signal instead of a bare number.
                                var measured = FastDirectorySizer.CalculateDirectorySizeDetailed(asset.InstallLocation);
                                if (measured.Bytes > 0)
                                {
                                    asset.EstimatedSizeBytes = measured.Bytes;
                                }

                                asset.SizeMeasurementComplete = measured.Complete;
                                asset.SizeSkips = measured.SkippedDirectories + measured.SkippedFiles;
                            }
                        }

                        rawItems.Add(asset);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Aggregate Python Sub-features (Core, pip, Standard Library) into Suite
        return ConsolidatePythonSuite(rawItems);
    }

    public List<SoftwareAsset> ScanPortable(
        bool calculateDiskSize = true,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SoftwareAsset>();

        foreach (var rootDir in GetDefaultPortableRoots())
        {
            if (!Directory.Exists(rootDir)) continue;

            try
            {
                foreach (var folder in Directory.EnumerateDirectories(rootDir))
                {
                    string folderName = Path.GetFileName(folder);
                    if (IgnoredPortableSubdirs.Contains(folderName)) continue;

                    string mainExe = "";
                    foreach (var candidateDir in new[] { folder, Path.Combine(folder, "bin") })
                    {
                        if (Directory.Exists(candidateDir))
                        {
                            try
                            {
                                var exes = Directory.EnumerateFiles(candidateDir, "*.exe").ToList();
                                foreach (var ex in exes)
                                {
                                    string exName = Path.GetFileNameWithoutExtension(ex).ToLowerInvariant();
                                    if (folderName.ToLowerInvariant().Contains(exName) || exName.Contains(folderName.ToLowerInvariant()))
                                    {
                                        mainExe = ex;
                                        break;
                                    }
                                    if (string.IsNullOrEmpty(mainExe) && !exName.Contains("unins") && !exName.Contains("update") && !exName.Contains("helper"))
                                    {
                                        mainExe = ex;
                                    }
                                }
                            }
                            catch { }
                        }
                        if (!string.IsNullOrEmpty(mainExe)) break;
                    }

                    if (!string.IsNullOrEmpty(mainExe))
                    {
                        long sizeBytes = calculateDiskSize ? FastDirectorySizer.CalculateDirectorySize(folder) : 0;
                        results.Add(new SoftwareAsset
                        {
                            Id = GenerateStableId(folderName, "Portable", folder, folderName),
                            DisplayName = folderName,
                            DisplayVersion = "Portable",
                            Publisher = "便携独立资产 / Local Asset",
                            InstallLocation = folder,
                            MainExecutable = mainExe,
                            EstimatedSizeBytes = sizeBytes,
                            IsPortable = true,
                            Category = "tools_utility",
                            AssetType = "gui_app"
                        });
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private static Dictionary<string, (string ExePath, string WorkingDir)> LoadShortcutTargetIndex()
    {
        var dict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return dict;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return dict;

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        string lnkBase = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                        dynamic shortcut = shell.CreateShortcut(lnk);
                        string target = shortcut.TargetPath;
                        string workDir = shortcut.WorkingDirectory;
                        if (!string.IsNullOrEmpty(target) && File.Exists(target))
                        {
                            dict[lnkBase] = (target, workDir);
                            string exeName = Path.GetFileName(target).ToLowerInvariant();
                            dict[exeName] = (target, workDir);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return dict;
    }

    private static List<SoftwareAsset> ConsolidatePythonSuite(List<SoftwareAsset> rawItems)
    {
        var consolidated = new List<SoftwareAsset>();
        var pythonSubcomponents = new List<string>();
        long pythonExtraBytes = 0;
        int pythonParentIdx = -1;

        for (int i = 0; i < rawItems.Count; i++)
        {
            var item = rawItems[i];
            string name = item.DisplayName;

            if (name.StartsWith("Python 3.12") && name != "Python 3.12.10 (64-bit)" && !name.Contains("Launcher"))
            {
                pythonSubcomponents.Add(name.Replace("Python 3.12.10 ", "").Replace(" (64-bit)", ""));
                pythonExtraBytes += item.EstimatedSizeBytes;
            }
            else if (name == "Python 3.12.10 (64-bit)")
            {
                pythonParentIdx = consolidated.Count;
                consolidated.Add(item);
            }
            else
            {
                consolidated.Add(item);
            }
        }

        if (pythonParentIdx >= 0 && pythonSubcomponents.Count > 0)
        {
            var pApp = consolidated[pythonParentIdx];
            pApp.DisplayName = "Python 3.12.10 (官方完整开发套件)";
            pApp.EstimatedSizeBytes += pythonExtraBytes;
            pApp.SuiteInfo = new SuiteInfo
            {
                ComponentCount = pythonSubcomponents.Count,
                Components = pythonSubcomponents,
                Tip = $"官方开发套件，整合了 {pythonSubcomponents.Count} 个底层模块（{string.Join("、", pythonSubcomponents.Take(3))} 等）。"
            };
            if (string.IsNullOrEmpty(pApp.MainExecutable) || !File.Exists(pApp.MainExecutable))
            {
                pApp.MainExecutable = @"C:\Program Files\Python312\python.exe";
            }
            if (string.IsNullOrEmpty(pApp.InstallLocation))
            {
                pApp.InstallLocation = @"C:\Program Files\Python312";
            }
        }

        return consolidated;
    }

    private static string GenerateStableId(string name, string publisher, string location, string rawKey)
    {
        var guidMatch = Regex.Match(rawKey, @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
        if (guidMatch.Success) return guidMatch.Value.ToLowerInvariant();

        string seed = $"{name.Trim().ToLowerInvariant()}|{publisher.Trim().ToLowerInvariant()}|{location.Trim().ToLowerInvariant()}|{rawKey.Trim().ToLowerInvariant()}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
        return "app_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string InferInstallLocation(string uninstallString, string displayIcon)
    {
        var candidate = ExtractPathFromCommand(displayIcon);
        if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
            candidate = ExtractPathFromCommand(uninstallString);

        if (!string.IsNullOrEmpty(candidate))
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Check if parent or grandparent is the actual software root (e.g. utility/bin/setup/core/version)
                    string dirName = Path.GetFileName(dir).ToLowerInvariant();
                    if (dirName is "utility" or "bin" or "uninstall" or "setup" or "core")
                    {
                        var parent = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        {
                            var grandparent = Path.GetDirectoryName(parent);
                            if (!string.IsNullOrEmpty(grandparent) && Directory.Exists(grandparent) && Path.GetFileName(parent).Any(char.IsDigit))
                            {
                                return grandparent;
                            }
                            return parent;
                        }
                    }
                    return dir;
                }
            }
            catch { }
        }

        return string.Empty;
    }

    public static string InferMainExecutable(string installLocation, string displayIcon, string uninstallString)
    {
        var iconPath = ExtractPathFromCommand(displayIcon);
        if (!string.IsNullOrEmpty(iconPath) && iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
        {
            string iconName = Path.GetFileName(iconPath).ToLowerInvariant();
            if (!iconName.Contains("unins") && !iconName.Contains("setup") && !iconName.Contains("update"))
            {
                return iconPath;
            }
        }

        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var dirInfo = new DirectoryInfo(installLocation);
                var exes = dirInfo.EnumerateFiles("*.exe", new EnumerationOptions { MaxRecursionDepth = 2, RecurseSubdirectories = true }).ToList();
                if (exes.Count > 0)
                {
                    var mainCandidates = exes.Where(e =>
                        !e.Name.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.Contains("update", StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.Contains("crash", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (mainCandidates.Count > 0)
                    {
                        string folderName = dirInfo.Name.ToLowerInvariant();
                        var best = mainCandidates.FirstOrDefault(e => e.Name.ToLowerInvariant().Contains(folderName) ||
                                                                      e.Name.Equals("wps.exe", StringComparison.OrdinalIgnoreCase) ||
                                                                      e.Name.Equals("ksolaunch.exe", StringComparison.OrdinalIgnoreCase));
                        if (best != null) return best.FullName;
                        return mainCandidates[0].FullName;
                    }
                    return exes[0].FullName;
                }
            }
            catch { }
        }

        return string.Empty;
    }

    public static string ExtractPathFromCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return string.Empty;

        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());

        // 1. Quoted path: "C:\Program Files\..."
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end != -1)
            {
                return cmd[1..end].Trim();
            }
        }

        // 2. Icon with comma: C:\path\app.exe,0
        int commaIdx = cmd.IndexOf(',');
        if (commaIdx > 0 && !cmd.StartsWith("\""))
        {
            cmd = cmd[..commaIdx].Trim();
        }

        // 3. Find .exe (handles unquoted paths with spaces, e.g. D:\Program Files (x86)\WPS\...\uninst.exe)
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            string candidate = cmd[..(exeIdx + 4)].Trim().Trim('"');
            if (File.Exists(candidate)) return candidate;
            if (candidate.Contains('\\') || candidate.Contains('/')) return candidate;
        }

        return cmd.Trim().Trim('"');
    }
}
