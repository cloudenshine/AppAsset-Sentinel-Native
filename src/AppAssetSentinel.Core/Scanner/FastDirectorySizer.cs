using System.Runtime.InteropServices;
using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Scanner;

public static class FastDirectorySizer
{
    // Check if directory is a Reparse Point (Junction or Symlink)
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            var di = new DirectoryInfo(path);
            return (di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    // Inspect Junction Target Path using Win32 API
    public static JunctionInfo GetJunctionInfo(string path)
    {
        var info = new JunctionInfo();
        if (!Directory.Exists(path))
        {
            info.IsJunction = false;
            return info;
        }

        try
        {
            var di = new DirectoryInfo(path);
            if ((di.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
            {
                info.IsJunction = false;
                return info;
            }

            // In .NET, LinkTarget property is available on FileSystemInfo
            info.IsJunction = true;
            info.TargetPath = di.LinkTarget ?? string.Empty;
            return info;
        }
        catch (Exception ex)
        {
            info.IsJunction = false;
            info.Error = ex.Message;
            return info;
        }
    }

    /// <summary>
    /// Calculates directory total size in bytes safely.
    /// Traversal will NEVER cross ReparsePoints (Junctions/Symlinks) to avoid infinite recursion cycles.
    /// Max depth limit: 64 levels.
    /// </summary>
    public static long CalculateDirectorySize(string path, int maxDepth = 64)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return 0;

        // If root path itself is a reparse point, do not count foreign drive
        if (IsReparsePoint(path))
            return 0;

        long totalSize = 0;
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            var (currentDir, depth) = stack.Pop();
            if (depth > maxDepth) continue;

            string normalizedDir;
            try
            {
                normalizedDir = Path.GetFullPath(currentDir).TrimEnd('\\');
            }
            catch
            {
                continue;
            }

            if (!visitedDirectories.Add(normalizedDir))
                continue; // Cycle detected, skip

            // Get files
            try
            {
                var dirInfo = new DirectoryInfo(currentDir);
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    try
                    {
                        // Reparse points for individual files (e.g. cloud placeholders / symlinks)
                        if ((file.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                        {
                            totalSize += file.Length;
                        }
                    }
                    catch
                    {
                        // File locked or permission denied
                    }
                }

                // Get subdirectories
                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        // Crucial Defense: Skip reparse points (NTFS Junctions / Symlinks)
                        if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            continue;
                        }

                        stack.Push((subDir.FullName, depth + 1));
                    }
                    catch
                    {
                        // Access denied
                    }
                }
            }
            catch
            {
                // Permission or IO exception
            }
        }

        return totalSize;
    }
}
