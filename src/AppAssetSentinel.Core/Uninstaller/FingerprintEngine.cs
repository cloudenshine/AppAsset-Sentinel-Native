using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Uninstaller;

public class SilentInfo
{
    [JsonPropertyName("installer_type")]
    public string InstallerType { get; set; } = "CustomFallback";

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("is_silent")]
    public bool IsSilent { get; set; } = false;

    [JsonPropertyName("command_display")]
    public string CommandDisplay { get; set; } = string.Empty;
}

public static class FingerprintEngine
{
    public static SilentInfo IdentifyAndMakeSilent(SoftwareAsset app)
    {
        if (app.IsPortable)
        {
            return new SilentInfo
            {
                InstallerType = "Portable",
                IsSilent = true,
                CommandDisplay = "便携式软件：直接安全移除程序目录并清理配置"
            };
        }

        string uninst = app.UninstallString.Trim();
        string quietUninst = app.QuietUninstallString.Trim();
        string targetCmd = !string.IsNullOrEmpty(quietUninst) ? quietUninst : uninst;

        var (exe, rawArgs) = ParseCommandLine(targetCmd);

        // 1. MSI Executable
        if (uninst.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || 
            rawArgs.Any(a => a.StartsWith("/i{", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/x{", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(uninst, @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
            string productCode = match.Success ? match.Value : "";

            var msiArgs = new List<string> { $"/X{productCode}", "/qn", "/norestart" };
            return new SilentInfo
            {
                InstallerType = "MSI",
                Executable = "msiexec.exe",
                Arguments = msiArgs,
                IsSilent = true,
                CommandDisplay = $"msiexec.exe /X{productCode} /qn /norestart"
            };
        }

        // 2. Inno Setup (unins000.exe)
        if (exe.Contains("unins", StringComparison.OrdinalIgnoreCase) && !exe.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            var innoArgs = new List<string> { "/SILENT", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" };
            return new SilentInfo
            {
                InstallerType = "InnoSetup",
                Executable = exe,
                Arguments = innoArgs,
                IsSilent = true,
                CommandDisplay = $"\"{exe}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
            };
        }

        // 3. NSIS (uninstall.exe /S)
        if (exe.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            var nsisArgs = new List<string> { "/S" };
            return new SilentInfo
            {
                InstallerType = "NSIS",
                Executable = exe,
                Arguments = nsisArgs,
                IsSilent = true,
                CommandDisplay = $"\"{exe}\" /S"
            };
        }

        // 4. Fallback: custom
        return new SilentInfo
        {
            InstallerType = "Custom",
            Executable = exe,
            Arguments = rawArgs,
            IsSilent = !string.IsNullOrEmpty(quietUninst),
            CommandDisplay = targetCmd
        };
    }

    private static (string Exe, List<string> Args) ParseCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return ("", new List<string>());

        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end != -1)
            {
                string exe = cmd[1..end].Trim();
                string rest = cmd[(end + 1)..].Trim();
                return (exe, rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList());
            }
        }

        // Check if the entire string or unquoted path exists on disk
        if (File.Exists(cmd))
        {
            return (cmd, new List<string>());
        }

        // Try greedily matching an existing executable with spaces
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string candidate = "";
        for (int i = 0; i < parts.Length; i++)
        {
            candidate = string.IsNullOrEmpty(candidate) ? parts[i] : $"{candidate} {parts[i]}";
            if (File.Exists(candidate) || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, parts.Skip(i + 1).ToList());
            }
        }

        if (parts.Length > 0)
        {
            return (parts[0], parts.Skip(1).ToList());
        }

        return (cmd, new List<string>());
    }
}
