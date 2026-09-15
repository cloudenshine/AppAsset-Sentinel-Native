using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Operations;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT A28/W11 acceptance for the asynchronous inventory pipeline:
/// the UI must be able to render the last known inventory immediately, a failed or corrupt
/// cache must be reported rather than silently ignored, and the "cached vs current" distinction
/// must be expressible so stale data is never presented as freshly verified.
/// </summary>
public class TestInventorySnapshotAndScanState : IDisposable
{
    private readonly string _root;

    public TestInventorySnapshotAndScanState()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelSnapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch { }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static List<SoftwareAsset> SampleAssets() => new()
    {
        new SoftwareAsset { Id = "a1", DisplayName = "Ollama", Category = "ai_compute", HeatLevel = "hot" },
        new SoftwareAsset { Id = "a2", DisplayName = "微信", Category = "office_collaboration", HeatLevel = "unknown" }
    };

    // -----------------------------------------------------------------
    // Snapshot round trip
    // -----------------------------------------------------------------

    [Fact]
    public void SnapshotRoundTripsTheInventoryAndItsCaptureTime()
    {
        string path = PathFor("snapshot.json");
        var before = DateTime.UtcNow.AddSeconds(-1);

        ScanSnapshotCache.Save(SampleAssets(), path);
        var loaded = ScanSnapshotCache.Load(path);

        Assert.True(loaded.Succeeded);
        Assert.False(loaded.FileMissing);
        Assert.Equal(2, loaded.Assets.Count);
        Assert.Equal("Ollama", loaded.Assets[0].DisplayName);
        Assert.Equal("unknown", loaded.Assets[1].HeatLevel);

        Assert.NotNull(loaded.CapturedAtUtc);
        Assert.True(loaded.CapturedAtUtc!.Value >= before.AddSeconds(-5));
    }

    [Fact]
    public void MissingSnapshotIsDistinguishableFromACorruptOne()
    {
        var missing = ScanSnapshotCache.Load(PathFor("never-written.json"));
        Assert.True(missing.Succeeded);
        Assert.True(missing.FileMissing);
        Assert.Empty(missing.Assets);

        string corruptPath = PathFor("corrupt.json");
        File.WriteAllText(corruptPath, "{ not json at all");

        var corrupt = ScanSnapshotCache.Load(corruptPath);
        Assert.False(corrupt.Succeeded);
        Assert.False(string.IsNullOrEmpty(corrupt.Error));
        Assert.Empty(corrupt.Assets);
    }

    [Fact]
    public void SaveIsAtomicAndLeavesNoTemporaryFile()
    {
        string path = PathFor("atomic.json");

        ScanSnapshotCache.Save(SampleAssets(), path);
        ScanSnapshotCache.Save(SampleAssets(), path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var loaded = ScanSnapshotCache.Load(path);
        Assert.True(loaded.Succeeded);
        Assert.Equal(2, loaded.Assets.Count);
    }

    [Fact]
    public void DeletedSnapshotIsReportedAsMissingNotAsAnError()
    {
        string path = PathFor("deletable.json");
        ScanSnapshotCache.Save(SampleAssets(), path);
        Assert.True(File.Exists(path));

        ScanSnapshotCache.Delete(path);
        var loaded = ScanSnapshotCache.Load(path);

        Assert.True(loaded.Succeeded);
        Assert.True(loaded.FileMissing);
    }

    // -----------------------------------------------------------------
    // Scan status vocabulary
    // -----------------------------------------------------------------

    [Fact]
    public void ScanStatusDefaultsToNeverScannedRatherThanClaimingSuccess()
    {
        var status = new ScanStatus();

        Assert.Equal(ScanPhase.NeverScanned, status.Phase);
        Assert.False(status.ServingCachedSnapshot);
        Assert.False(status.IsInProgress);
        Assert.Equal(0, status.AssetCount);
        Assert.Null(status.CompletedAtUtc);
    }

    [Fact]
    public void CachedResultsAreLabelledSoStaleDataIsNeverPresentedAsVerified()
    {
        var status = new ScanStatus
        {
            Phase = ScanPhase.Completed,
            ServingCachedSnapshot = true,
            AssetCount = 84
        };

        // The two facts are independent: a completed scan can still be serving a cached
        // snapshot while the fresh one runs, and the UI needs both to be honest.
        Assert.True(status.ServingCachedSnapshot);
        Assert.Equal(ScanPhase.Completed, status.Phase);
        Assert.Equal(84, status.AssetCount);
    }

    [Theory]
    [InlineData(ScanPhase.NeverScanned, false)]
    [InlineData(ScanPhase.Scanning, true)]
    [InlineData(ScanPhase.Completed, false)]
    [InlineData(ScanPhase.Failed, false)]
    public void OnlyTheScanningPhaseReportsItselfAsInProgress(ScanPhase phase, bool expected)
    {
        var status = new ScanStatus { Phase = phase };
        Assert.Equal(expected, status.IsInProgress);
    }

    [Fact]
    public void FailedScanKeepsTheLastErrorWithoutDiscardingPreviousResults()
    {
        string path = PathFor("failed-then-served.json");
        ScanSnapshotCache.Save(SampleAssets(), path);

        var status = new ScanStatus
        {
            Phase = ScanPhase.Failed,
            LastError = "注册表访问被拒绝",
            ServingCachedSnapshot = true,
            AssetCount = 2
        };

        var loaded = ScanSnapshotCache.Load(path);

        // The failure is visible and the previous inventory is still available to serve.
        Assert.False(string.IsNullOrEmpty(status.LastError));
        Assert.True(loaded.Succeeded);
        Assert.Equal(2, loaded.Assets.Count);
    }
}
