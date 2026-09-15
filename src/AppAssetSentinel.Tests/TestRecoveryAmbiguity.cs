using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W06: a logged state is a status, not a layout. NeedsAttention in particular is emitted
/// by several different paths whose disk layouts differ, so the recovery assessment must not
/// claim the log determines the state in those cases - it has to defer to the disk observation.
/// </summary>
public class TestRecoveryAmbiguity : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestRecoveryAmbiguity()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelAmbiguous_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void AConflictLeavesBothSidesRealAndMustNotBeClassifiedAsSwitched()
    {
        string source = Path.Combine(_root, "conflict-src");
        string target = Path.Combine(_root, "conflict-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(source, "model.bin"), "SOURCE-VERSION");
        File.WriteAllText(Path.Combine(target, "model.bin"), "EXISTING-TARGET-VERSION");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "conflict",
            Category = "ai_models"
        });

        Assert.Equal(OperationStatus.NeedsAttention, result.Outcome.Status);

        var assessment = RecoveryInspector.Inspect(log, result.Record!.TaskId);

        // The source was never turned into a link, so the layout is NOT SwitchedWithBackup.
        Assert.NotEqual(ObservedLayout.SwitchedWithBackup, assessment.ObservedLayout);

        // Both real directories are present and neither may be discarded.
        Assert.Equal(ObservedLayout.BothPresent, assessment.ObservedLayout);
        Assert.True(assessment.DataAccountedFor);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("人工比对"));

        // Because NeedsAttention is ambiguous, the log alone must not be presented as decisive.
        Assert.False(assessment.LogDecides,
            "NeedsAttention 由多条路径产生，日志不能单独决定磁盘布局");
    }

    [Fact]
    public void ADeterministicStateStillLetsTheLogDecide()
    {
        string source = Path.Combine(_root, "plain");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.bin"), "x");

        var log = new OperationLog(_logDir);
        var stopped = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(source, "nested"),
            AssetName = "plain",
            Category = "test"
        });

        var assessment = RecoveryInspector.Inspect(log, stopped.Record!.TaskId);

        Assert.Equal(OperationState.PreflightFailed, assessment.LoggedState);
        Assert.True(assessment.LogDecides);
        Assert.True(assessment.LogMatchesDisk);
    }
}