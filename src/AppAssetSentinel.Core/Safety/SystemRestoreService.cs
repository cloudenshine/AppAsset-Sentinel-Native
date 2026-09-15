using System.Diagnostics;

namespace AppAssetSentinel.Core.Safety;

public static class RegistryBackupService
{
    public static string BackupRegistryKey(string registryPath, string backupDir)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return string.Empty;

        if (!Directory.Exists(backupDir))
        {
            Directory.CreateDirectory(backupDir);
        }

        var fileName = $"reg_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.reg";
        var backupFilePath = Path.Combine(backupDir, fileName);

        try
        {
            // Use reg.exe export
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
            if (proc != null)
            {
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && File.Exists(backupFilePath))
                {
                    return backupFilePath;
                }
            }
        }
        catch { }

        return string.Empty;
    }
}

public static class SystemRestoreService
{
    public static (bool Success, string Message) CreateRestorePoint(string description = "AppAsset Sentinel Pre-Uninstall Snapshot")
    {
        try
        {
            var psCmd = $"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction SilentlyContinue; $?";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(15000);
                var stdout = proc.StandardOutput.ReadToEnd();
                if (stdout.Contains("True"))
                {
                    return (true, "System restore point successfully created.");
                }
            }
            return (false, "System restore is disabled or requires elevated administrator privileges.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
