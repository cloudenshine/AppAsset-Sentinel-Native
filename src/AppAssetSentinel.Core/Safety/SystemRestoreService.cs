using System.Diagnostics;
using System.Text.Json.Serialization;

namespace AppAssetSentinel.Core.Safety;

public sealed class RegistryBackupResult
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; } = -1;

    /// <summary>Scope actually exported — never claim a full-system snapshot (AUDIT A24).</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Exports registry keys as an append-only archive. The result is reported as successful
/// only when reg.exe really exited 0 and produced a non-empty file (AUDIT A24).
/// </summary>
public static class RegistryBackupService
{
    public static RegistryBackupResult BackupRegistryKey(string registryPath, string backupDir)
    {
        var result = new RegistryBackupResult { Scope = registryPath };

        if (string.IsNullOrWhiteSpace(registryPath))
        {
            result.Error = "未指定注册表路径。";
            return result;
        }

        try
        {
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }
        }
        catch (Exception ex)
        {
            result.Error = $"无法创建备份目录：{ex.Message}";
            return result;
        }

        string fileName = $"reg_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.reg";
        string backupFilePath = Path.Combine(backupDir, fileName);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{registryPath}\" \"{backupFilePath}\" /y",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                result.Error = "无法启动 reg.exe。";
                return result;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            result.ExitCode = proc.ExitCode;

            if (proc.ExitCode != 0)
            {
                result.Error = $"reg.exe 退出码 {proc.ExitCode}：{stderr.Trim()} {stdout.Trim()}".Trim();
                return result;
            }

            if (!File.Exists(backupFilePath))
            {
                result.Error = "reg.exe 返回成功但未生成备份文件。";
                return result;
            }

            long size = new FileInfo(backupFilePath).Length;
            if (size <= 0)
            {
                result.Error = "备份文件为空。";
                return result;
            }

            result.Succeeded = true;
            result.Path = backupFilePath;
            result.Bytes = size;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }
}

public sealed class RestorePointResult
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; } = -1;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("requires_elevation")]
    public bool RequiresElevation { get; set; }
}

/// <summary>
/// Creates a Windows System Restore checkpoint.
/// AUDIT A07: the description is never concatenated into a PowerShell command string;
/// it travels through an environment variable that a fixed script reads.
/// AUDIT A24: the outcome comes from the real exit code, not from an optimistic guess.
/// </summary>
public static class SystemRestoreService
{
    /// <summary>Fixed script. No caller data is ever interpolated into code.</summary>
    private const string FixedScript = """
        $ErrorActionPreference = 'Stop'
        $desc = $env:SENTINEL_RESTORE_POINT_DESC
        if ([string]::IsNullOrWhiteSpace($desc)) { $desc = 'AppAsset Sentinel snapshot' }
        try {
            Checkpoint-Computer -Description $desc -RestorePointType 'MODIFY_SETTINGS'
            Write-Output 'SENTINEL_OK'
            exit 0
        } catch {
            Write-Output ("SENTINEL_ERR " + $_.Exception.Message)
            exit 2
        }
        """;

    public static RestorePointResult CreateRestorePoint(string description)
    {
        var result = new RestorePointResult();
        string safeDescription = SanitizeDescription(description);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["SENTINEL_RESTORE_POINT_DESC"] = safeDescription;

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                result.Message = "无法启动 PowerShell。";
                return result;
            }

            proc.StandardInput.Write(FixedScript);
            proc.StandardInput.Close();

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000);
            result.ExitCode = proc.ExitCode;

            if (proc.ExitCode == 0 && stdout.Contains("SENTINEL_OK", StringComparison.Ordinal))
            {
                result.Succeeded = true;
                result.Message = "系统还原点已创建。";
                return result;
            }

            string detail = stderr.Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                int idx = stdout.IndexOf("SENTINEL_ERR", StringComparison.Ordinal);
                detail = idx >= 0 ? stdout[idx..].Trim() : stdout.Trim();
            }

            result.Message = $"系统还原点创建失败（退出码 {proc.ExitCode}）：{detail}";
            result.RequiresElevation =
                detail.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("管理员", StringComparison.Ordinal) ||
                detail.Contains("权限", StringComparison.Ordinal);
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            return result;
        }
    }

    private static string SanitizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "AppAsset Sentinel snapshot";
        }

        var buffer = new System.Text.StringBuilder();
        foreach (char c in description)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or ':' or '(' or ')')
            {
                buffer.Append(c);
            }
        }

        string cleaned = buffer.ToString().Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "AppAsset Sentinel snapshot";
        }

        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }
}
