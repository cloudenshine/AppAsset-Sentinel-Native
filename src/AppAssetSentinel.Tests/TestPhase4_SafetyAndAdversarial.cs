using AppAssetSentinel.Core.Safety;
using Xunit;

namespace AppAssetSentinel.Tests;

public class TestPhase4_SafetyAndAdversarial : IDisposable
{
    private readonly string _tempBackupDir;

    public TestPhase4_SafetyAndAdversarial()
    {
        _tempBackupDir = Path.Combine(Path.GetTempPath(), $"SentinelTest_P4_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempBackupDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempBackupDir))
            {
                Directory.Delete(_tempBackupDir, true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    public void TestCriticalDirectoryFuseInterceptsSystemPaths(string systemPath)
    {
        Assert.Throws<CriticalSystemDirectoryException>(() =>
        {
            CriticalDirectoryGuard.AssertSafeToDelete(systemPath);
        });
    }

    [Fact]
    public void TestUserHomeDirectoryProtection()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Throws<CriticalSystemDirectoryException>(() =>
        {
            CriticalDirectoryGuard.AssertSafeToDelete(userProfile);
        });
    }

    [Fact]
    public void TestSafeCustomDirectoryPassesGuard()
    {
        var safeDir = Path.Combine(_tempBackupDir, "SafeAppToUninstall");
        Directory.CreateDirectory(safeDir);

        // Should not throw
        CriticalDirectoryGuard.AssertSafeToDelete(safeDir);
    }

    [Fact]
    public void TestRegistryBackupExport()
    {
        // Export HKCU\Environment which is guaranteed to exist
        string backupFile = RegistryBackupService.BackupRegistryKey(@"HKEY_CURRENT_USER\Environment", _tempBackupDir);

        Assert.False(string.IsNullOrEmpty(backupFile));
        Assert.True(File.Exists(backupFile));

        var content = File.ReadAllText(backupFile);
        Assert.Contains("Windows Registry Editor", content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"C:\..\..\..\Windows\System32")]
    public void TestAdversarialPathsHandling(string? malformedPath)
    {
        Assert.True(CriticalDirectoryGuard.IsCriticalDirectory(malformedPath!));
    }
}
