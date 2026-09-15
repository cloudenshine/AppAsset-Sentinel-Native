using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Scanner;

/// <summary>
/// AUDIT W05: an application's relationship to Python is not a yes/no question.
///
/// These two cases have opposite safety consequences and must never be collapsed into one flag:
///
///   Embedded      - the application ships the runtime inside its own directory. It owns it, and
///                   removing that application also removes the runtime. Nobody else is affected.
///   ExternalShared- the application needs a Python it does not ship, so the runtime lives
///                   somewhere else and is used by other software too. Treating it as this
///                   application's residue would authorise destroying something shared.
///
/// Unknown is a real answer, and destructive automation must not proceed on it.
/// </summary>
public enum PythonRuntimeRelationship
{
    /// <summary>No Python signal was found.</summary>
    None,

    /// <summary>The runtime is inside the application's own directory.</summary>
    Embedded,

    /// <summary>Python is needed but not shipped, so the runtime is shared with other software.</summary>
    ExternalShared,

    /// <summary>A signal exists but ownership could not be established. Must not authorise deletion.</summary>
    Unknown
}

public sealed class PythonRuntimeFinding
{
    [JsonPropertyName("relationship")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PythonRuntimeRelationship Relationship { get; set; } = PythonRuntimeRelationship.None;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = new();

    /// <summary>
    /// True only when the runtime demonstrably belongs to this application. Anything else must be
    /// treated as not owned, which is the conservative direction.
    /// </summary>
    [JsonPropertyName("runtime_owned_by_application")]
    public bool RuntimeOwnedByApplication { get; set; }

    /// <summary>True when the relationship is too unclear to act on destructively.</summary>
    [JsonPropertyName("blocks_destructive_automation")]
    public bool BlocksDestructiveAutomation =>
        Relationship == PythonRuntimeRelationship.ExternalShared
        || Relationship == PythonRuntimeRelationship.Unknown;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public static class PythonRuntimeDetector
{
    /// <summary>
    /// Classifies the relationship from the install location alone. Read-only.
    /// </summary>
    public static PythonRuntimeFinding Detect(string installLocation, string mainExecutable = "", string displayName = "")
    {
        var finding = new PythonRuntimeFinding();

        bool hasInstallDir = !string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation);

        // A launcher script is itself the signal: the user runs a .py, so some Python must exist,
        // but that Python is not inside the application directory.
        bool launcherIsScript =
            EndsWith(mainExecutable, ".py") || EndsWith(mainExecutable, ".pyw");

        if (launcherIsScript)
        {
            finding.Evidence.Add($"入口是脚本而非可执行文件：{Path.GetFileName(mainExecutable)}");
        }

        if (!hasInstallDir)
        {
            // No directory to inspect. If there is any signal at all, the answer is Unknown rather
            // than None: absence of a readable directory is not evidence of absence of a runtime.
            if (launcherIsScript || LooksLikePythonName(displayName))
            {
                finding.Relationship = PythonRuntimeRelationship.Unknown;
                finding.Summary = "存在 Python 迹象，但安装目录不可读，无法确认运行时归属。";
                return finding;
            }

            finding.Relationship = PythonRuntimeRelationship.None;
            finding.Summary = "未发现 Python 相关迹象。";
            return finding;
        }

        bool shippedRuntime = false;
        bool shippedPackages = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(installLocation, "*.*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();

                if (name is "python.exe" or "pythonw.exe" or "python3.exe")
                {
                    shippedRuntime = true;
                    finding.Evidence.Add($"安装目录内含运行时：{name}");
                }
                else if (name.Equals("python.exe.manifest", StringComparison.OrdinalIgnoreCase))
                {
                    shippedRuntime = true;
                }
            }

            // A bundled package tree implies the runtime is bundled with it.
            string[] packageMarkers = { "site-packages", "Lib", "python", "Scripts" };
            foreach (var marker in packageMarkers)
            {
                string candidate = Path.Combine(installLocation, marker);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (marker == "python" || marker == "site-packages" || marker == "Lib")
                {
                    shippedPackages = true;
                    finding.Evidence.Add($"安装目录内含包目录：{marker}");
                }
            }

            // A .py entry script alongside a shipped runtime is still Embedded; without one it is
            // the classic "please install Python separately" layout.
            bool hasPyFiles = false;
            foreach (var file in Directory.EnumerateFiles(installLocation, "*.py", SearchOption.TopDirectoryOnly))
            {
                hasPyFiles = true;
                break;
            }

            if (shippedRuntime || shippedPackages)
            {
                finding.Relationship = PythonRuntimeRelationship.Embedded;
                finding.RuntimeOwnedByApplication = true;
                finding.Summary = "应用自带 Python 运行时，运行时属于该应用本身。";
                return finding;
            }

            if (hasPyFiles || launcherIsScript)
            {
                finding.Relationship = PythonRuntimeRelationship.ExternalShared;
                finding.Evidence.Add("存在脚本但安装目录内没有运行时，依赖外部 Python。");
                finding.Summary = "应用依赖外部 Python，运行时由其他软件共享，不属于该应用。";
                return finding;
            }
        }
        catch
        {
            finding.Relationship = PythonRuntimeRelationship.Unknown;
            finding.Summary = "读取安装目录失败，无法确认 Python 运行时归属。";
            return finding;
        }

        // Any leftover signal we cannot act on is Unknown, never None.
        if (launcherIsScript || LooksLikePythonName(displayName))
        {
            finding.Relationship = PythonRuntimeRelationship.Unknown;
            finding.Summary = "存在 Python 迹象但无法确认归属。";
            return finding;
        }

        finding.Relationship = PythonRuntimeRelationship.None;
        finding.Summary = "未发现 Python 相关迹象。";
        return finding;
    }

    private static bool EndsWith(string value, string suffix) =>
        !string.IsNullOrEmpty(value) && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePythonName(string name) =>
        !string.IsNullOrEmpty(name) && name.Contains("python", StringComparison.OrdinalIgnoreCase);
}