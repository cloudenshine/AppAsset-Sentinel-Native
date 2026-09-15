using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

public class TestPhase1_ScannerAndSizer : IDisposable
{
    private readonly string _testDir;

    public TestPhase1_ScannerAndSizer()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"SentinelTest_P1_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void TestRealSystemRegistryScan()
    {
        var scanner = new Win32RegistryScanner();
        var apps = scanner.ScanInstalledSoftware(includeSystemComponents: false, calculateDiskSize: false);

        Assert.NotNull(apps);
        Assert.NotEmpty(apps);
        Assert.True(apps.Count >= 5, $"Expected at least 5 installed apps, got {apps.Count}");

        // Validate basic properties
        foreach (var app in apps.Take(10))
        {
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(app.RegistryKeyPath));
            Assert.Contains(app.Architecture, new[] { "x64", "x86" });
        }
    }

    [Fact]
    public void TestDirectorySizerNormalCalculation()
    {
        var subDir = Path.Combine(_testDir, "sub");
        Directory.CreateDirectory(subDir);

        var file1 = Path.Combine(_testDir, "file1.bin");
        var file2 = Path.Combine(subDir, "file2.bin");

        File.WriteAllBytes(file1, new byte[1024]); // 1 KB
        File.WriteAllBytes(file2, new byte[2048]); // 2 KB

        long calculatedSize = FastDirectorySizer.CalculateDirectorySize(_testDir);
        Assert.Equal(3072, calculatedSize);
    }

    [Fact]
    public void TestDirectorySizerNonexistentPath()
    {
        long size = FastDirectorySizer.CalculateDirectorySize(@"C:\Nonexistent_Dir_" + Guid.NewGuid());
        Assert.Equal(0, size);
    }

    [Fact]
    public void TestDirectorySizerReparsePointCycleProtection()
    {
        // Create folder A
        var folderA = Path.Combine(_testDir, "FolderA");
        Directory.CreateDirectory(folderA);
        File.WriteAllBytes(Path.Combine(folderA, "data.bin"), new byte[5000]);

        // Create folder B inside A
        var folderB = Path.Combine(folderA, "FolderB");
        Directory.CreateDirectory(folderB);
        File.WriteAllBytes(Path.Combine(folderB, "subdata.bin"), new byte[3000]);

        // Create a junction inside folder B pointing back to folder A (infinite loop trap!)
        var loopJunction = Path.Combine(folderB, "LoopToA");
        
        bool juncCreated = AppAssetSentinel.Core.Migration.JunctionEngine.CreateJunction(loopJunction, folderA, out var err);
        if (juncCreated)
        {
            // Sizer MUST NOT enter an infinite loop or double count!
            long size = FastDirectorySizer.CalculateDirectorySize(folderA);
            
            // Total should be 5000 + 3000 = 8000 bytes, ignoring the loop junction
            Assert.Equal(8000, size);

            // Clean up junction first
            AppAssetSentinel.Core.Migration.JunctionEngine.RemoveJunction(loopJunction, out _);
        }
    }
}
