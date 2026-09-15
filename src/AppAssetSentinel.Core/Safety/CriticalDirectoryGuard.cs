namespace AppAssetSentinel.Core.Safety;

public class CriticalSystemDirectoryException : Exception
{
    public CriticalSystemDirectoryException(string message) : base(message) { }
}

public static class CriticalDirectoryGuard
{
    private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows",
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Program Files\Common Files",
        @"C:\Program Files (x86)\Common Files",
        @"C:\ProgramData",
        @"C:\ProgramData\Microsoft",
        @"C:\Users",
        @"C:\Users\Default",
        @"C:\Users\Public"
    };

    public static bool IsCriticalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return true;
        }

        // 1. Root drive protection (e.g. C:, D:)
        if (normalized.Length <= 3 && normalized.EndsWith(":")) return true;
        var root = Path.GetPathRoot(normalized)?.TrimEnd('\\');
        if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)) return true;

        // 2. Fixed protected system folders
        if (ProtectedFolders.Contains(normalized)) return true;

        // 3. Directly in Windows directory
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (normalized.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            // Any path inside C:\Windows is strictly critical
            return true;
        }

        // 4. User home root (e.g. C:\Users\Admin)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (string.Equals(normalized, userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. System special folders
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        if (string.Equals(normalized, appData, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, localAppData, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static void AssertSafeToDelete(string path)
    {
        if (IsCriticalDirectory(path))
        {
            throw new CriticalSystemDirectoryException(
                $"【系统熔断拦截】绝对禁止删除关键系统目录或根盘符：'{path}'。防御盾已强行终止操作！");
        }
    }
}
