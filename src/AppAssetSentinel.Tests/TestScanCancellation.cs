using AppAssetSentinel.Core.Operations;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W11 acceptance: a long-running scan must be cancellable, and cancellation must be
/// reported honestly as a cooperative stop rather than as a failure or, worse, as a complete
/// inventory. A cancelled scan must never make partial results look authoritative.
/// </summary>
public class TestScanCancellation
{
    [Fact]
    public void ANewHandleIsNotCancelled()
    {
        using var cancellation = new ScanCancellation();

        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(cancellation.Token.IsCancellationRequested);

        // Checkpoints are no-ops before cancellation is requested.
        cancellation.ThrowIfCancelled();
    }

    [Fact]
    public void RequestMarksTheTokenAndIsIdempotent()
    {
        using var cancellation = new ScanCancellation();

        Assert.True(cancellation.Request());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(cancellation.Token.IsCancellationRequested);

        // A second request is a no-op, not an error.
        Assert.False(cancellation.Request());
    }

    [Fact]
    public void CheckpointsThrowOnceCancellationIsRequested()
    {
        using var cancellation = new ScanCancellation();
        cancellation.Request();

        var ex = Assert.Throws<OperationCanceledException>(() => cancellation.ThrowIfCancelled());
        Assert.True(ScanCancellation.IsCancelledResult(ex));

        // A genuine failure must not be mistaken for a cancellation.
        Assert.False(ScanCancellation.IsCancelledResult(new InvalidOperationException()));
    }

    [Fact]
    public void TheStopMessageIsExplicitlyCooperativeAndSaysPartialResultsAreNotComplete()
    {
        string text = ScanCancellation.DescribeCooperativeStop("复制");

        Assert.Contains("协作式", text);
        Assert.Contains("检查点", text);
        Assert.Contains("复制", text);

        // The important promise: un-scanned content is empty, NOT "nothing there".
        Assert.Contains("不会被当作", text);
    }

    [Theory]
    [InlineData(ScanPhase.NeverScanned, false)]
    [InlineData(ScanPhase.Scanning, true)]
    [InlineData(ScanPhase.Completed, false)]
    [InlineData(ScanPhase.Failed, false)]
    [InlineData(ScanPhase.Cancelled, false)]
    public void CancelledIsItsOwnPhaseAndIsNotReportedAsInProgress(ScanPhase phase, bool expected)
    {
        var status = new ScanStatus { Phase = phase };
        Assert.Equal(expected, status.IsInProgress);
    }

    [Fact]
    public void ACancelledScanIsDistinctFromAFailedOne()
    {
        var cancelled = new ScanStatus { Phase = ScanPhase.Cancelled };
        var failed = new ScanStatus { Phase = ScanPhase.Failed };

        Assert.NotEqual(cancelled.Phase, failed.Phase);

        // Cancellation is a user action, not an error, so the two states must stay distinct.
        Assert.False(cancelled.IsInProgress);
        Assert.False(failed.IsInProgress);
    }
}