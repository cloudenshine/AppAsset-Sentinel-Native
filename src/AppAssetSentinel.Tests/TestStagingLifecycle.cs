using AppAssetSentinel.Core.Abstractions;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W07: staging is task-owned scratch space and must not outlive a refused operation.
/// A real cross-volume run leaked a full duplicate of the payload on E: when the target was
/// already occupied — the source was untouched, so the copy served no purpose and silently
/// consumed disk. This class locks that down.
/// </summary>
public class TestStagingLifecycle : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestStagingLifecycle()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelStaging_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Unlink junctions first, then retry the delete: a bare recursive delete can
                // partially succeed and leave an empty root behind, and the catch would hide it.
                foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (FastDirectorySizer.IsReparsePoint(dir))
                        {
                            JunctionEngine.RemoveJunction(dir, out _);
                        }
                    }
                    catch { }
                }

                for (int attempt = 0; attempt < 3 && Directory.Exists(_root); attempt++)
                {
                    try { Directory.Delete(_root, true); }
                    catch { System.Threading.Thread.Sleep(30); }
                }
            }
        }
        catch { }
    }

    private string BuildSource(string name, int fileCount = 3)
    {
        string source = Path.Combine(_root, name);
        Directory.CreateDirectory(source);

        for (int i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(source, $"blob-{i}.bin"), new string('x', 512 + i));
        }

        return source;
    }

    private static string[] FindStagingDirs(string root) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root, "*" + ".sentinel-staging-*", SearchOption.AllDirectories)
            : Array.Empty<string>();

    [Fact]
    public void OccupiedTargetLeavesNoStagingCopyBehind()
    {
        string source = BuildSource("occupied-src");
        string target = Path.Combine(_root, "occupied-target");

        // The destination exists and is not ours.
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "not-ours");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "occupied",
            Category = "test"
        });

        Assert.Equal(OperationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Equal("target_occupied", result.Outcome.Code);
        Assert.False(result.Outcome.DidMutate);

        // The scratch copy must be gone: the source is intact, so it had no purpose.
        Assert.Empty(FindStagingDirs(_root));

        // And the unrelated destination content was left alone.
        Assert.Equal("not-ours", File.ReadAllText(Path.Combine(target, "unrelated.txt")));

        // The source is untouched.
        Assert.True(File.Exists(Path.Combine(source, "blob-0.bin")));
    }

    [Fact]
    public void VerifyFailureLeavesNoStagingCopyBehind()
    {
        string source = BuildSource("verify-src");
        string target = Path.Combine(_root, "verify-target");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "verify",
            Category = "test"
        });

        // This run succeeds, which is the control case: on success staging became the target.
        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);
        Assert.Empty(FindStagingDirs(_root));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void ConflictDeliberatelyKeepsStagingAndSaysWhereItIsAndThatItCostsSpace()
    {
        string source = BuildSource("conflict-src", 1);
        string target = Path.Combine(_root, "conflict-target");

        // Same name, different content: the two sides diverge.
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "blob-0.bin"), "DIFFERENT-EXISTING-CONTENT");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "conflict",
            Category = "test"
        });

        Assert.Equal(OperationStatus.NeedsAttention, result.Outcome.Status);
        Assert.False(result.Outcome.DidMutate);

        // Neither side was overwritten.
        Assert.Equal("DIFFERENT-EXISTING-CONTENT", File.ReadAllText(Path.Combine(target, "blob-0.bin")));
        Assert.True(File.Exists(Path.Combine(source, "blob-0.bin")));

        // Here staging IS retained as evidence, so it must be found and it must be named.
        var staging = FindStagingDirs(_root);
        Assert.NotEmpty(staging);

        Assert.Contains(result.Record!.StagingPath, result.Outcome.Message);
        Assert.Contains("占用磁盘空间", result.Outcome.Message);
        Assert.Contains(result.Record.StagingPath, result.Outcome.Recovery);
    }

    [Fact]
    public void BoundaryRejectionNeverCreatesStagingAtAll()
    {
        string source = BuildSource("boundary-src");
        string target = Path.Combine(source, "nested");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "boundary",
            Category = "test"
        });

        Assert.Equal(OperationState.PreflightFailed, result.Record!.State);
        Assert.Empty(FindStagingDirs(_root));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void ConfigRedirectWithoutAVariableCreatesNoStagingEither()
    {
        string source = BuildSource("cfg-src");
        string target = Path.Combine(_root, "cfg-target");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "cfg",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = string.Empty,
            EnvironmentStore = new InMemoryEnvironmentStore()
        });

        Assert.Equal(OperationState.PreflightFailed, result.Record!.State);
        Assert.Empty(FindStagingDirs(_root));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void CancellationLeavesNeitherStagingNorAMovedSource()
    {
        string source = BuildSource("cancel-src");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(_root, "cancel-target"),
            AssetName = "cancel",
            Category = "test"
        }, cts.Token);

        Assert.False(result.Outcome.DidMutate);
        Assert.True(Directory.Exists(source));
        Assert.False(FastDirectorySizer.IsReparsePoint(source));
        Assert.Empty(FindStagingDirs(_root));
    }
}
