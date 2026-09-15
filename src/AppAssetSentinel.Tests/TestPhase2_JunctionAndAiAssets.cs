using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

public class TestPhase2_JunctionAndAiAssets : IDisposable
{
    private readonly string _testRoot;

    public TestPhase2_JunctionAndAiAssets()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SentinelTest_P2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                // Unlink any junctions inside before deleting root
                foreach (var dir in Directory.GetDirectories(_testRoot, "*", SearchOption.AllDirectories))
                {
                    if (FastDirectorySizer.IsReparsePoint(dir))
                    {
                        JunctionEngine.RemoveJunction(dir, out _);
                    }
                }
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public void TestCreateAndInspectJunction()
    {
        var targetDir = Path.Combine(_testRoot, "RealDataFolder");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "sample.txt"), "AppAsset Sentinel Junction Test Content");

        var junctionPath = Path.Combine(_testRoot, "LinkFolder");

        bool created = JunctionEngine.CreateJunction(junctionPath, targetDir, out var err);
        Assert.True(created, $"Junction creation failed: {err}");

        // Inspect
        var info = FastDirectorySizer.GetJunctionInfo(junctionPath);
        Assert.True(info.IsJunction);

        // Verify transparency
        var readThroughJunction = File.ReadAllText(Path.Combine(junctionPath, "sample.txt"));
        Assert.Equal("AppAsset Sentinel Junction Test Content", readThroughJunction);

        // Delete junction link without affecting target
        bool removed = JunctionEngine.RemoveJunction(junctionPath, out var removeErr);
        Assert.True(removed, $"Junction removal failed: {removeErr}");
        Assert.False(Directory.Exists(junctionPath));
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(Path.Combine(targetDir, "sample.txt")));
    }

    [Fact]
    public void TestAiAssetDetection()
    {
        var aiDir = Path.Combine(_testRoot, "AIModels");
        Directory.CreateDirectory(aiDir);

        var blobDir = Path.Combine(aiDir, "blobs");
        Directory.CreateDirectory(blobDir);

        // Create dummy GGUF (6 MB)
        var ggufPath = Path.Combine(aiDir, "qwen3.5-7b.gguf");
        File.WriteAllBytes(ggufPath, new byte[6 * 1024 * 1024]);

        // Create dummy Safetensors (7 MB)
        var stPath = Path.Combine(aiDir, "flux-lora.safetensors");
        File.WriteAllBytes(stPath, new byte[7 * 1024 * 1024]);

        // Create dummy Ollama Blob (12 MB)
        var blobPath = Path.Combine(blobDir, "sha256-5eda44778353b28d52d1eaea9f6d688c02b3693f687da75ceb715157477482bd");
        File.WriteAllBytes(blobPath, new byte[12 * 1024 * 1024]);

        var detected = AiAssetDetector.DetectAssetsInDirectory(aiDir);

        Assert.Equal(3, detected.Count);
        Assert.Contains(detected, d => d.AssetType == "GGUF" && d.FileSizeBytes == 6 * 1024 * 1024);
        Assert.Contains(detected, d => d.AssetType == "Safetensors" && d.FileSizeBytes == 7 * 1024 * 1024);
        Assert.Contains(detected, d => d.AssetType == "OllamaBlob" && d.FileSizeBytes == 12 * 1024 * 1024);
    }

    [Fact]
    public async Task TestAtomicMigrationWorkflow()
    {
        var sourceDir = Path.Combine(_testRoot, "SourceModels");
        Directory.CreateDirectory(sourceDir);

        var dummyWeightFile = Path.Combine(sourceDir, "model.safetensors");
        File.WriteAllBytes(dummyWeightFile, new byte[2 * 1024 * 1024]); // 2 MB

        var targetParent = Path.Combine(_testRoot, "TargetDriveStore");
        Directory.CreateDirectory(targetParent);

        var task = await JunctionEngine.MigrateDirectoryAsync(
            sourceDir,
            targetParent,
            assetId: "ai_model_test",
            assetName: "TestModelSet"
        );

        Assert.Equal(MigrationStatus.Completed, task.Status);
        Assert.True(Directory.Exists(sourceDir));

        // Source dir should now be an NTFS Junction!
        var info = FastDirectorySizer.GetJunctionInfo(sourceDir);
        Assert.True(info.IsJunction);

        // Target path should hold the actual files
        Assert.True(Directory.Exists(task.TargetPath));
        Assert.True(File.Exists(Path.Combine(task.TargetPath, "model.safetensors")));

        // Reading through the source junction must work seamlessly
        var readThrough = File.ReadAllBytes(Path.Combine(sourceDir, "model.safetensors"));
        Assert.Equal(2 * 1024 * 1024, readThrough.Length);
    }
}
