using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W06/W11: cancelling a relocation is only meaningful if it can happen *during* the copy,
/// not just before it starts. The kernel checks the token inside the per-file copy loop, so this
/// locks down what a mid-copy cancel actually leaves behind: the source untouched, no staging
/// residue, and an outcome that does not overstate what happened.
/// </summary>
public class TestMidCopyCancellation : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestMidCopyCancellation()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelMidCancel_{Guid.NewGuid():N}");
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
                foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (FastDirectorySizer.IsReparsePoint(dir)) JunctionEngine.RemoveJunction(dir, out _);
                    }
                    catch { }
                }
                Directory.Delete(_root, true);
            }
        }
        catch { }
    }

    [Fact]
    public void CancellingDuringTheCopyLeavesTheSourceIntactAndNoStagingBehind()
    {
        string source = Path.Combine(_root, "big-src");
        Directory.CreateDirectory(source);

        // Large enough that the copy loop cannot finish inside the cancellation window.
        const int fileCount = 1500;
        for (int i = 0; i < fileCount; i++)
        {
            File.WriteAllBytes(Path.Combine(source, $"blob-{i:d4}.bin"), new byte[64 * 1024]);
        }

        string target = Path.Combine(_root, "vault", "big-src");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "big-src",
            Category = "ai_models"
        }, cts.Token);

        // Cancellation must actually have won the race, otherwise this test proves nothing.
        Assert.Equal("cancelled", result.Outcome.Code);
        Assert.Equal(OperationStatus.FailedRecoverable, result.Outcome.Status);
        Assert.False(result.Outcome.DidMutate);

        // The source is a real directory with its data, and nothing was published.
        Assert.True(Directory.Exists(source));
        Assert.False(FastDirectorySizer.IsReparsePoint(source));
        Assert.False(Directory.Exists(target));

        // A cancelled run must not leave its scratch copy behind.
        Assert.Empty(Directory.GetDirectories(_root, "*.sentinel-staging-*", SearchOption.AllDirectories));

        // Every source file survived, and the operation record says it was cancelled.
        Assert.Equal(fileCount, Directory.GetFiles(source, "*.bin").Length);

        var reloaded = log.Load(result.Record!.TaskId);
        Assert.True(reloaded.Succeeded);
        Assert.Equal(OperationState.FailedRecoverable, reloaded.Record!.State);
    }

    [Fact]
    public void PreCancelledTokenStopsBeforeAnySideEffect()
    {
        string source = Path.Combine(_root, "src2");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.bin"), "x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(_root, "vault2"),
            AssetName = "src2",
            Category = "test"
        }, cts.Token);

        Assert.Equal("cancelled", result.Outcome.Code);
        Assert.False(result.Outcome.DidMutate);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_root, "vault2")));
        Assert.Empty(Directory.GetDirectories(_root, "*.sentinel-staging-*", SearchOption.AllDirectories));
    }
}