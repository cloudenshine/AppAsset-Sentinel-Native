using System.Diagnostics;
using System.Security.Cryptography;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

public class JunctionEngine
{
    public static bool CreateJunction(string junctionPath, string targetPath, out string error)
    {
        error = string.Empty;
        junctionPath = Path.GetFullPath(junctionPath).TrimEnd('\\');
        targetPath = Path.GetFullPath(targetPath).TrimEnd('\\');

        if (!Directory.Exists(targetPath))
        {
            error = $"Target directory does not exist: {targetPath}";
            return false;
        }

        if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
        {
            error = $"Junction source path already exists: {junctionPath}";
            return false;
        }

        try
        {
            // Use cmd.exe /c mklink /J which works on any NTFS drive without requiring Developer Mode or elevation
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                error = "Failed to launch cmd process for mklink.";
                return false;
            }

            proc.WaitForExit(5000);
            var output = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            if (proc.ExitCode != 0 || !Directory.Exists(junctionPath))
            {
                error = $"mklink /J failed: {output} {stderr}".Trim();
                return false;
            }

            // Verify it is recognized as a reparse point
            var info = FastDirectorySizer.GetJunctionInfo(junctionPath);
            if (!info.IsJunction)
            {
                error = "Created path is not recognized as an NTFS ReparsePoint.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool RemoveJunction(string junctionPath, out string error)
    {
        error = string.Empty;
        if (!Directory.Exists(junctionPath)) return true;

        var info = FastDirectorySizer.GetJunctionInfo(junctionPath);
        if (!info.IsJunction)
        {
            error = $"Path is not a Junction. Safety fuse aborted deletion: {junctionPath}";
            return false;
        }

        try
        {
            // Directory.Delete on a reparse point removes the junction link without touching target directory files
            Directory.Delete(junctionPath, false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static async Task<MigrationTask> MigrateDirectoryAsync(
        string sourceDir, 
        string targetParentDir, 
        string assetId = "", 
        string assetName = "",
        IProgress<(long bytesMigrated, long totalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        sourceDir = Path.GetFullPath(sourceDir).TrimEnd('\\');
        targetParentDir = Path.GetFullPath(targetParentDir).TrimEnd('\\');

        var task = new MigrationTask
        {
            SoftwareId = assetId,
            SoftwareName = string.IsNullOrEmpty(assetName) ? Path.GetFileName(sourceDir) : assetName,
            SourcePath = sourceDir,
            TargetPath = Path.Combine(targetParentDir, Path.GetFileName(sourceDir)),
            Status = MigrationStatus.Scanning,
            StatusMessage = "Scanning source folder size..."
        };

        if (!Directory.Exists(sourceDir))
        {
            task.Status = MigrationStatus.Failed;
            task.Error = $"Source directory does not exist: {sourceDir}";
            return task;
        }

        if (FastDirectorySizer.IsReparsePoint(sourceDir))
        {
            task.Status = MigrationStatus.Failed;
            task.Error = $"Source directory is already a Junction/Symlink: {sourceDir}";
            return task;
        }

        // 1. Calculate size and check drive capacity
        task.TotalBytes = FastDirectorySizer.CalculateDirectorySize(sourceDir);
        var targetDrive = new DriveInfo(Path.GetPathRoot(targetParentDir) ?? "C:\\");
        if (targetDrive.AvailableFreeSpace < (long)(task.TotalBytes * 1.1))
        {
            task.Status = MigrationStatus.Failed;
            task.Error = $"Target drive {targetDrive.Name} does not have sufficient space. Required: {task.TotalBytes * 1.1:N0} bytes, Available: {targetDrive.AvailableFreeSpace:N0} bytes.";
            return task;
        }

        // 2. Prepare target directory
        if (!Directory.Exists(task.TargetPath))
        {
            Directory.CreateDirectory(task.TargetPath);
        }

        task.Status = MigrationStatus.Copying;
        task.StatusMessage = "Copying files to target destination...";

        string backupPath = $"{sourceDir}.sentinel_backup_{Guid.NewGuid():N}";
        task.BackupSourcePath = backupPath;

        try
        {
            // Recursive Copy with progress
            await CopyDirectoryRecursivelyAsync(sourceDir, task.TargetPath, task, progress, cancellationToken);

            task.Status = MigrationStatus.Verifying;
            task.StatusMessage = "Verifying target copy integrity...";

            long targetSize = FastDirectorySizer.CalculateDirectorySize(task.TargetPath);
            if (targetSize < task.TotalBytes)
            {
                throw new InvalidOperationException($"Verification failed: copied size {targetSize} is less than source size {task.TotalBytes}");
            }

            // 3. Rename source to backup
            Directory.Move(sourceDir, backupPath);

            // 4. Create Junction
            task.Status = MigrationStatus.CreatingJunction;
            task.StatusMessage = "Creating atomic NTFS Junction...";

            if (!CreateJunction(sourceDir, task.TargetPath, out var juncError))
            {
                // Rollback immediately
                Directory.Move(backupPath, sourceDir);
                throw new InvalidOperationException($"Junction creation failed: {juncError}");
            }

            // 5. Test junction access
            if (!Directory.Exists(sourceDir))
            {
                // Rollback
                RemoveJunction(sourceDir, out _);
                Directory.Move(backupPath, sourceDir);
                throw new InvalidOperationException("Created junction is not accessible.");
            }

            task.Status = MigrationStatus.Completed;
            task.StatusMessage = "Migration successfully completed. Source directory seamlessly replaced with NTFS Junction.";
            task.CreatedJunctionPath = sourceDir;
            return task;
        }
        catch (Exception ex)
        {
            task.Status = MigrationStatus.Failed;
            task.Error = ex.Message;
            task.StatusMessage = $"Migration aborted: {ex.Message}";

            // Safety Rollback: ensure original source is restored
            try
            {
                if (Directory.Exists(sourceDir) && FastDirectorySizer.IsReparsePoint(sourceDir))
                {
                    RemoveJunction(sourceDir, out _);
                }

                if (Directory.Exists(backupPath) && !Directory.Exists(sourceDir))
                {
                    Directory.Move(backupPath, sourceDir);
                }
            }
            catch { }

            return task;
        }
    }

    private static async Task CopyDirectoryRecursivelyAsync(
        string source, 
        string target, 
        MigrationTask task, 
        IProgress<(long, long)>? progress, 
        CancellationToken ct)
    {
        var dirInfo = new DirectoryInfo(source);

        foreach (var file in dirInfo.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            var targetFilePath = Path.Combine(target, file.Name);

            using (var srcStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            using (var dstStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await dstStream.WriteAsync(buffer, 0, read, ct);
                    task.MigratedBytes += read;
                    progress?.Report((task.MigratedBytes, task.TotalBytes));
                }
            }
            task.FileCount++;
        }

        foreach (var subDir in dirInfo.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                continue;

            var nextTarget = Path.Combine(target, subDir.Name);
            Directory.CreateDirectory(nextTarget);
            await CopyDirectoryRecursivelyAsync(subDir.FullName, nextTarget, task, progress, ct);
        }
    }
}
