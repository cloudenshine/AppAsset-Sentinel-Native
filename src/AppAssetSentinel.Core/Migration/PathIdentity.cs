using System.Text.Json.Serialization;
using AppAssetSentinel.Core.Scanner;

namespace AppAssetSentinel.Core.Migration;

/// <summary>Why a pair of paths is rejected before any copy happens.</summary>
public enum PathBoundaryVerdict
{
    Ok,
    EmptyInput,
    NotRooted,
    SamePath,
    TargetInsideSource,
    SourceInsideTarget,
    SourceIsReparsePoint,
    TargetIsReparsePoint,
    Unresolvable
}

public sealed record PathBoundaryResult(
    PathBoundaryVerdict Verdict,
    string Reason,
    string NormalizedSource,
    string NormalizedTarget)
{
    public bool IsOk => Verdict == PathBoundaryVerdict.Ok;
}

/// <summary>
/// Canonical path identity and containment checks (AUDIT A05). Windows has many spellings
/// for the same directory — short names, trailing separators, case, and reparse points that
/// make a child path resolve somewhere else entirely. Every comparison here works on the
/// resolved final path so aliases cannot smuggle an operation past the guard.
/// </summary>
public static class PathIdentity
{
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Resolves a path to its real location, following any reparse point in the final
    /// segment. Returns null when the path cannot be resolved at all.
    /// </summary>
    public static string? ResolveFinal(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                // Non-existent paths still have a meaningful canonical spelling.
                return Normalize(path);
            }

            var info = Directory.Exists(path)
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);

            if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                string? linkTarget = info.LinkTarget;
                if (!string.IsNullOrEmpty(linkTarget))
                {
                    string parent = Path.GetDirectoryName(Normalize(path)) ?? string.Empty;
                    string resolved = Path.IsPathRooted(linkTarget)
                        ? linkTarget
                        : Path.Combine(parent, linkTarget);
                    return Normalize(resolved);
                }
            }

            return Normalize(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or sits beneath it.</summary>
    public static bool IsSameOrInside(string root, string candidate)
    {
        string? r = ResolveFinal(root);
        string? c = ResolveFinal(candidate);
        if (r == null || c == null)
        {
            return false;
        }

        if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full boundary validation for a relocate operation. Refuses every unsupported shape
    /// rather than attempting a best-effort move (AUDIT A05).
    /// </summary>
    public static PathBoundaryResult ValidateRelocation(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.EmptyInput,
                "源路径与目标路径都必须提供。", source ?? string.Empty, target ?? string.Empty);
        }

        string normSource, normTarget;
        try
        {
            normSource = Normalize(source);
            normTarget = Normalize(target);
        }
        catch (Exception ex)
        {
            return new PathBoundaryResult(PathBoundaryVerdict.NotRooted,
                $"路径不是合法的绝对路径：{ex.Message}", source, target);
        }

        if (!Path.IsPathRooted(normSource) || !Path.IsPathRooted(normTarget))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.NotRooted,
                "源路径与目标路径都必须是绝对路径。", normSource, normTarget);
        }

        string? resSource = ResolveFinal(normSource);
        string? resTarget = ResolveFinal(normTarget);

        if (resSource == null || resTarget == null)
        {
            return new PathBoundaryResult(PathBoundaryVerdict.Unresolvable,
                "无法解析路径的真实身份（可能存在断链或权限不足）。", normSource, normTarget);
        }

        if (string.Equals(resSource, resTarget, StringComparison.OrdinalIgnoreCase))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.SamePath,
                "目标路径与源路径相同，操作没有意义。", resSource, resTarget);
        }

        if (resTarget.StartsWith(resSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.TargetInsideSource,
                "目标路径位于源目录内部，复制会自我递归。", resSource, resTarget);
        }

        if (resSource.StartsWith(resTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.SourceInsideTarget,
                "源目录位于目标路径内部，切换后会产生别名越界。", resSource, resTarget);
        }

        if (Directory.Exists(normSource) && !FastDirectorySizer.IsReparsePoint(normSource) &&
            (new DirectoryInfo(normSource).Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
        {
            return new PathBoundaryResult(PathBoundaryVerdict.SourceIsReparsePoint,
                "源目录本身已是重解析点，不能再次迁移。", resSource, resTarget);
        }

        if (Directory.Exists(normTarget) && FastDirectorySizer.IsReparsePoint(normTarget))
        {
            return new PathBoundaryResult(PathBoundaryVerdict.TargetIsReparsePoint,
                "目标路径已是重解析点，拒绝在其上叠加迁移。", resSource, resTarget);
        }

        return new PathBoundaryResult(PathBoundaryVerdict.Ok, "路径边界校验通过。", resSource, resTarget);
    }
}

/// <summary>Per-file integrity evidence used to prove a copy actually matched (AUDIT A04).</summary>
public sealed class FileDigest
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class InventoryManifest
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FileDigest> Files { get; set; } = new();

    [JsonPropertyName("directories")]
    public List<string> Directories { get; set; } = new();

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    /// <summary>True when every entry was hashed. Partial scans must be reported as partial.</summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; set; } = true;

    [JsonPropertyName("skipped_entries")]
    public List<string> SkippedEntries { get; set; } = new();
}

public sealed record ManifestComparison(bool Identical, List<string> Differences);

/// <summary>
/// Builds and compares content manifests. The audit's probe showed that comparing only
/// total byte counts accepted "AAAA" against "BBBB" as identical, so equality is decided
/// per file by SHA-256 and file set, never by size alone.
/// </summary>
public static class FileIntegrity
{
    public static InventoryManifest BuildManifest(string root, int maxEntries = 200000)
    {
        var manifest = new InventoryManifest { Root = PathIdentity.Normalize(root) };

        if (!Directory.Exists(root))
        {
            manifest.Complete = false;
            manifest.SkippedEntries.Add(root);
            return manifest;
        }

        var stack = new Stack<string>();
        stack.Push(manifest.Root);
        int counted = 0;

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    if (++counted > maxEntries)
                    {
                        manifest.Complete = false;
                        manifest.SkippedEntries.Add("超出条目上限，清单不完整。");
                        return manifest;
                    }

                    try
                    {
                        var attr = File.GetAttributes(entry);
                        bool isReparse = (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;

                        if (Directory.Exists(entry))
                        {
                            manifest.Directories.Add(Path.GetRelativePath(manifest.Root, entry));

                            // Do not follow reparse points: their content belongs elsewhere.
                            if (!isReparse)
                            {
                                stack.Push(entry);
                            }
                            else
                            {
                                manifest.SkippedEntries.Add($"[reparse-skipped] {Path.GetRelativePath(manifest.Root, entry)}");
                            }
                        }
                        else if (File.Exists(entry))
                        {
                            var fi = new FileInfo(entry);
                            manifest.Files.Add(new FileDigest
                            {
                                RelativePath = Path.GetRelativePath(manifest.Root, entry),
                                Length = fi.Length,
                                Sha256 = isReparse ? "[reparse]" : ComputeSha256(entry)
                            });
                            manifest.TotalBytes += fi.Length;
                        }
                    }
                    catch (Exception ex)
                    {
                        manifest.Complete = false;
                        manifest.SkippedEntries.Add($"{entry}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                manifest.Complete = false;
                manifest.SkippedEntries.Add($"{dir}: {ex.Message}");
            }
        }

        manifest.Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        manifest.Directories.Sort(StringComparer.Ordinal);
        return manifest;
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Content-level comparison. Size equality alone is never sufficient.</summary>
    public static ManifestComparison Compare(InventoryManifest expected, InventoryManifest actual)
    {
        var differences = new List<string>();

        var expectedFiles = expected.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualFiles = actual.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, digest) in expectedFiles)
        {
            if (!actualFiles.TryGetValue(path, out var other))
            {
                differences.Add($"[缺失] {path}");
                continue;
            }

            if (!string.Equals(digest.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"[内容不一致] {path} 期望 {digest.Sha256[..Math.Min(12, digest.Sha256.Length)]}… 实际 {other.Sha256[..Math.Min(12, other.Sha256.Length)]}…");
            }
        }

        foreach (var path in actualFiles.Keys)
        {
            if (!expectedFiles.ContainsKey(path))
            {
                differences.Add($"[多余] {path}");
            }
        }

        if (!expected.Complete || !actual.Complete)
        {
            differences.Add("[不完整] 清单存在未能读取的条目，无法断言一致。");
        }

        return new ManifestComparison(differences.Count == 0, differences);
    }
}
