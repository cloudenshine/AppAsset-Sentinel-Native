using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Scanner;

public static class DynamicAssetIntelligence
{
    private static readonly string[] ShortcutDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
    };

    public static void EnhanceWithDynamicIntelligence(SoftwareAsset app)
    {
        // If already has high-precision curated rule from semantic DB, only fill missing blanks
        bool hasCuratedRole = !string.IsNullOrEmpty(app.PlainRole) && !app.PlainRole.Contains("官方发布的 Windows 桌面应用程序");
        bool hasCuratedUsage = !string.IsNullOrEmpty(app.CoreUsage) && !app.CoreUsage.Contains("专属业务功能支持");

        if (hasCuratedRole && hasCuratedUsage)
        {
            return;
        }

        string installLoc = app.InstallLocation;
        string mainExe = app.MainExecutable;
        string name = app.DisplayName;
        string pub = string.IsNullOrWhiteSpace(app.Publisher) ? "" : app.Publisher.Trim();

        // 1. Harvest PE FileVersionInfo
        string peDescription = "";
        string peProduct = "";
        string peCompany = "";
        string peComments = "";

        if (!string.IsNullOrEmpty(mainExe) && File.Exists(mainExe))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(mainExe);
                peDescription = CleanString(vi.FileDescription);
                peProduct = CleanString(vi.ProductName);
                peCompany = CleanString(vi.CompanyName);
                peComments = CleanString(vi.Comments);
            }
            catch { }
        }

        // 2. Harvest Desktop & Start Menu Shortcut Description
        string shortcutDesc = FindShortcutDescription(name, app.MainExecutable);

        // 3. Harvest package.json description / name
        string packageJsonDesc = "";
        string packageJsonName = "";
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            foreach (var cand in new[]
            {
                Path.Combine(installLoc, "package.json"),
                Path.Combine(installLoc, "resources", "app", "package.json"),
                Path.Combine(installLoc, "resources", "package.json")
            })
            {
                if (File.Exists(cand))
                {
                    try
                    {
                        var text = File.ReadAllText(cand);
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("description", out var dEl)) packageJsonDesc = CleanString(dEl.GetString());
                        if (root.TryGetProperty("name", out var nEl)) packageJsonName = CleanString(nEl.GetString());
                        break;
                    }
                    catch { }
                }
            }
        }

        // 4. Harvest README documentation first paragraph
        string readmeSnippet = "";
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            try
            {
                var dirInfo = new DirectoryInfo(installLoc);
                var readmeFile = dirInfo.EnumerateFiles("*readme*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (readmeFile != null && readmeFile.Length < 1024 * 1024)
                {
                    var lines = File.ReadLines(readmeFile.FullName)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Trim().StartsWith("#"))
                        .Take(2);
                    readmeSnippet = CleanString(string.Join(" ", lines));
                    if (readmeSnippet.Length > 120) readmeSnippet = readmeSnippet[..120] + "...";
                }
            }
            catch { }
        }

        // 5. Detect Technology Stack & Deep Runtime Capabilities
        bool hasCuda = false;
        bool hasGgml = false;
        bool hasElectron = false;
        bool hasPython = false;
        bool hasNetworkDriver = false;

        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(installLoc, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string fName = Path.GetFileName(file).ToLowerInvariant();
                    if (fName.Contains("cuda") || fName.Contains("cublas")) hasCuda = true;
                    if (fName.Contains("ggml") || fName.Contains("llama")) hasGgml = true;
                    if (fName.Contains("electron") || fName.Contains("chrome_100_percent")) hasElectron = true;
                    if (fName.Contains("python") || fName.EndsWith(".py")) hasPython = true;
                    if (fName.Contains("wintun") || fName.EndsWith(".sys")) hasNetworkDriver = true;
                }

                if (Directory.Exists(Path.Combine(installLoc, "resources"))) hasElectron = true;
                if (Directory.Exists(Path.Combine(installLoc, "site-packages")) || Directory.Exists(Path.Combine(installLoc, "Lib"))) hasPython = true;
            }
            catch { }
        }

        // 6. Synthesize Professional, Context-Rich Semantic Dimensions
        string effectivePublisher = !string.IsNullOrEmpty(pub) ? pub :
                                    !string.IsNullOrEmpty(peCompany) ? peCompany : "开源社区 / 独立软件开发者";

        // ---【定位】PLAIN ROLE ---
        if (!hasCuratedRole)
        {
            if (hasCuda || hasGgml)
            {
                app.PlainRole = $"由「{effectivePublisher}」打造的高性能本地 AI 硬件加速与模型推理引擎。";
                app.Category = "ai_compute";
                app.AssetType = "runtime_environment";
                app.IsProtected = true;
                app.RiskLevel = "important_runtime";
            }
            else if (hasNetworkDriver)
            {
                app.PlainRole = $"由「{effectivePublisher}」提供的网络通信、虚拟网卡驱动或底层数据通道。";
                app.Category = "system_network";
                app.AssetType = "system_service";
            }
            else if (!string.IsNullOrEmpty(peDescription) && !peDescription.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                app.PlainRole = $"由「{effectivePublisher}」发布的「{peDescription}」官方桌面套件。";
            }
            else if (hasElectron)
            {
                app.PlainRole = $"基于现代跨平台渲染架构打造的「{name}」桌面交互式工作台。";
            }
            else
            {
                app.PlainRole = $"由「{effectivePublisher}」发布的独立应用程序（{name}）。";
            }
        }

        // ---【用途】CORE USAGE ---
        if (!hasCuratedUsage)
        {
            if (!string.IsNullOrEmpty(shortcutDesc) && !shortcutDesc.Equals(name, StringComparison.OrdinalIgnoreCase) && shortcutDesc.Length >= 5)
            {
                app.CoreUsage = shortcutDesc;
            }
            else if (!string.IsNullOrEmpty(packageJsonDesc) && packageJsonDesc.Length >= 6)
            {
                app.CoreUsage = packageJsonDesc;
            }
            else if (!string.IsNullOrEmpty(readmeSnippet))
            {
                app.CoreUsage = readmeSnippet;
            }
            else if (!string.IsNullOrEmpty(peComments))
            {
                app.CoreUsage = peComments;
            }
            else if (hasCuda || hasGgml)
            {
                app.CoreUsage = "调用本地 GPU/CPU 硬件算力，为开源大语言模型与神经网络提供高速量化计算与推理调度。";
            }
            else if (hasNetworkDriver)
            {
                app.CoreUsage = "建立底层数据转发路由、网络适配器虚拟化与加密通信通道支持。";
            }
            else
            {
                app.CoreUsage = $"提供「{name}」核心业务逻辑执行、数据本地存储与功能交互服务。";
            }
        }

        // ---【生态】ECOSYSTEM ---
        if (string.IsNullOrEmpty(app.Ecosystem) || app.Ecosystem.Contains("独立工具生态体系") || app.Ecosystem.Contains("硬件/软件厂商"))
        {
            if (hasCuda || hasGgml)
            {
                app.Ecosystem = "开源本地 AI、GGML 与深度学习基础设施生态链。";
            }
            else if (hasElectron)
            {
                app.Ecosystem = $"基于 Chromium + Node.js 现代桌面软件生态链（{effectivePublisher}）。";
            }
            else if (hasPython)
            {
                app.Ecosystem = "Python 科学计算与本地自动化工程生态。";
            }
            else
            {
                app.Ecosystem = $"{effectivePublisher} 桌面软件生态体系。";
            }
        }

        // ---【关联】DEPENDENCY CHAIN ---
        if (string.IsNullOrEmpty(app.DependencyChain) || app.DependencyChain.Contains("独立运行，卸载不影响其他软件"))
        {
            if (hasCuda)
            {
                app.DependencyChain = "【GPU硬件计算依赖】包含 CUDA 动态加速模块，依赖 NVIDIA 独立显卡驱动；卸载将导致下游依赖的本地模型计算失效。";
            }
            else if (hasNetworkDriver)
            {
                app.DependencyChain = "【网络适配器关联】涉及系统底层网卡或路由配置；卸载将清理虚拟适配器与通信规则。";
            }
            else if (hasPython)
            {
                app.DependencyChain = "【Python环境依赖】内部集成 Python 脚本或解释器环境；卸载仅清理自身包目录，不破坏系统全局 Python。";
            }
            else
            {
                app.DependencyChain = "独立运行的桌面应用程序，数据保存在本地安装与配置目录中；卸载通常不影响操作系统底层。";
            }
        }

        // Add dynamically extracted tags if none exist
        if (app.Tags.Count == 0)
        {
            if (hasCuda) app.Tags.Add("CUDA加速");
            if (hasGgml) app.Tags.Add("大模型推理");
            if (hasElectron) app.Tags.Add("现代化应用");
            if (hasPython) app.Tags.Add("Python底座");
            if (hasNetworkDriver) app.Tags.Add("网络服务");
            if (app.Tags.Count == 0) app.Tags.Add("桌面工具");
        }
    }

    private static string FindShortcutDescription(string appName, string mainExe)
    {
        appName = appName.ToLowerInvariant();
        string exeName = string.IsNullOrEmpty(mainExe) ? "" : Path.GetFileNameWithoutExtension(mainExe).ToLowerInvariant();

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "";
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return "";

            foreach (var dir in ShortcutDirs)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    string lnkLower = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (lnkLower.Contains(appName) || (!string.IsNullOrEmpty(exeName) && lnkLower.Contains(exeName)))
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(file);
                            string desc = CleanString(shortcut.Description);
                            if (!string.IsNullOrWhiteSpace(desc) && desc.Length >= 5)
                            {
                                return desc;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        return "";
    }

    private static string CleanString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Trim().Replace("\r", " ").Replace("\n", " ");
    }
}
