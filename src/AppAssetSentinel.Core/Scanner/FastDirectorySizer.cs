using AppAssetSentinel.Core.Models;

namespace AppAssetSentinel.Core.Scanner;

public static class FastDirectorySizer
{
    /// <summary>Check if a path is a Reparse Point (Junction or Symlink).</summary>
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

    /// <summary>Inspect a Junction's target path.</summary>
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
    /// AUDIT A22: the old API returned a bare long, so a partial walk (denied folders,
    /// skipped reparse points, depth limits) was indistinguishable from a complete total
    /// and could be advertised as a "real physical space" figure.
    /// </summary>
    public sealed record DirectorySizeResult
    {
        public long Bytes { get; init; }
        public long FileCount { get; init; }
        public long SkippedDirectories { get; init; }
        public long SkippedFiles { get; init; }
        public bool HitDepthLimit { get; init; }

        /// <summary>True only when every entry beneath the root contributed to Bytes.</summary>
        public bool Complete => SkippedDirectories == 0 && SkippedFiles == 0 && !HitDepthLimit;
    }

    public static DirectorySizeResult CalculateDirectorySizeDetailed(string path, int maxDepth = 64)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new DirectorySizeResult();
        }

        // A reparse-point root belongs to another volume; counting it would double-count.
        if (IsReparsePoint(path))
        {
            return new DirectorySizeResult { SkippedDirectories = 1 };
        }

        long totalSize = 0;
        long fileCount = 0;
        long skippedDirs = 0;
        long skippedFiles = 0;
        bool depthLimit = false;

        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            var (currentDir, depth) = stack.Pop();

            if (depth > maxDepth)
            {
                depthLimit = true;
                continue;
            }

            string normalizedDir;
            try
            {
                normalizedDir = Path.GetFullPath(currentDir).TrimEnd('\\');
            }
            catch
            {
                skippedDirs++;
                continue;
            }

            if (!visitedDirectories.Add(normalizedDir))
            {
                continue; // Cycle detected, skip
            }

            try
            {
                var dirInfo = new DirectoryInfo(currentDir);

                foreach (var file in dirInfo.EnumerateFiles())
                {
                    try
                    {
                        // Reparse points for individual files (cloud placeholders / symlinks)
                        if ((file.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                        {
                            totalSize += file.Length;
                            fileCount++;
                        }
                        else
                        {
                            skippedFiles++;
                        }
                    }
                    catch
                    {
                        skippedFiles++;
                    }
                }

                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        // Crucial defence: never traverse reparse points; their bytes live elsewhere.
                        if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            skippedDirs++;
                            continue;
                        }

                        stack.Push((subDir.FullName, depth + 1));
                    }
                    catch
                    {
                        skippedDirs++;
                    }
                }
            }
            catch
            {
                skippedDirs++;
            }
        }

        return new DirectorySizeResult
        {
            Bytes = totalSize,
            FileCount = fileCount,
            SkippedDirectories = skippedDirs,
            SkippedFiles = skippedFiles,
            HitDepthLimit = depthLimit
        };
    }

    /// <summary>
    /// Calculates directory total size in bytes safely. Traversal will NEVER cross
    /// ReparsePoints (Junctions/Symlinks) to avoid infinite recursion cycles.
    /// Max depth: 64 levels. Prefer CalculateDirectorySizeDetailed when the caller must
    /// know whether the figure is complete.
    /// </summary>
    public static long CalculateDirectorySize(string path, int maxDepth = 64)
    {
        return CalculateDirectorySizeDetailed(path, maxDepth).Bytes;
    }
}