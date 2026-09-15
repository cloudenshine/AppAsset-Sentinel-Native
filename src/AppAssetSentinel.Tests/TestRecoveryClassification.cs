using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W06 acceptance: after an abrupt termination the true state must be determinable, and
/// the disk must win over the log. RecoveryInspector is exercised against every layout the
/// kernel can leave behind, including the case where the process died between recording a step
/// and performing it.
/// </summary>
public class TestRecoveryClassification : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestRecoveryClassification()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelRecovery_{Guid.NewGuid():N}");
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

    private (OperationLog log, MigrationKernelResult result) SwitchAnAsset(string name)
    {
        string source = Path.Combine(_root, name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.bin"), "REAL-DATA");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(_root, "vault", name),
            AssetName = name,
            Category = "ai_models"
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);
        return (log, result);
    }

    [Fact]
    public void ABuildThatNeverReachedTheSwitchReportsTheOriginalAsIntact()
    {
        string source = Path.Combine(_root, "untouched");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.bin"), "REAL-DATA");

        var log = new OperationLog(_logDir);
        var stopped = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(source, "nested"),
            AssetName = "untouched",
            Category = "test"
        });

        var assessment = RecoveryInspector.Inspect(log, stopped.Record!.TaskId);

        Assert.Equal(ObservedLayout.OriginalIntact, assessment.ObservedLayout);
        Assert.True(assessment.LogMatchesDisk);
        Assert.True(assessment.DataAccountedFor);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("源数据完好"));
    }

    [Fact]
    public void ASwitchedTaskWithABackupIsClassifiedAsRecoverable()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("switched");
        var assessment = RecoveryInspector.Inspect(log, result.Record!.TaskId);

        Assert.Equal(ObservedLayout.SwitchedWithBackup, assessment.ObservedLayout);
        Assert.True(assessment.LogMatchesDisk);
        Assert.True(assessment.DataAccountedFor);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("Recover"));
    }

    [Fact]
    public void ACommittedTaskIsClassifiedAsHavingNoLocalBackup()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("committed");
        var record = result.Record!;

        var committed = MigrationCommit.Commit(log, record.TaskId, authorized: true);
        Assert.Equal(OperationStatus.Succeeded, committed.Status);

        var assessment = RecoveryInspector.Inspect(log, record.TaskId);

        Assert.Equal(ObservedLayout.SwitchedCommitted, assessment.ObservedLayout);
        Assert.True(assessment.LogMatchesDisk);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("不再有本地备份"));
    }

    [Fact]
    public void WhenTheLogSaysSwitchedButTheDiskShowsTheOriginalTheDiskWins()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("disagree");
        var record = result.Record!;

        // Reconstruct the state after a crash that happened after the log recorded Switched but
        // before the switch took effect on disk: undo the filesystem, keep the log.
        JunctionEngine.RemoveJunction(record.SourcePath, out _);
        Directory.Move(record.SourceBackupPath, record.SourcePath);
        Directory.Delete(record.TargetPath, true);

        var assessment = RecoveryInspector.Inspect(log, record.TaskId);

        Assert.Equal(ObservedLayout.OriginalIntact, assessment.ObservedLayout);
        Assert.False(assessment.LogMatchesDisk);
        Assert.Contains("以磁盘为准", assessment.Conclusion);
        Assert.Contains("Switched", assessment.Conclusion);
        Assert.True(assessment.DataAccountedFor);
    }

    [Fact]
    public void BothRealDirectoriesPresentIsReportedAsNeedingAHumanDecision()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("both");
        var record = result.Record!;

        JunctionEngine.RemoveJunction(record.SourcePath, out _);
        Directory.CreateDirectory(record.SourcePath);
        File.WriteAllText(Path.Combine(record.SourcePath, "written-after.bin"), "LOCAL-EDIT");

        var assessment = RecoveryInspector.Inspect(log, record.TaskId);

        Assert.Equal(ObservedLayout.BothPresent, assessment.ObservedLayout);
        Assert.True(assessment.DataAccountedFor);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("人工比对"));
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("不会自动合并或删除"));

        Assert.True(File.Exists(Path.Combine(record.SourcePath, "written-after.bin")));
        Assert.True(Directory.Exists(record.SourceBackupPath));
    }

    [Fact]
    public void MissingDataIsNeverReportedAsSafeToCleanUp()
    {
        var log = new OperationLog(_logDir);

        log.Save(new OperationRecord
        {
            TaskId = "task_missing",
            AssetName = "gone",
            SourcePath = Path.Combine(_root, "no-such-source"),
            TargetPath = Path.Combine(_root, "no-such-target"),
            SourceBackupPath = Path.Combine(_root, "no-such-backup"),
            State = OperationState.Switched
        });

        var assessment = RecoveryInspector.Inspect(log, "task_missing");

        Assert.Equal(ObservedLayout.DataMissing, assessment.ObservedLayout);
        Assert.False(assessment.DataAccountedFor);
        Assert.False(assessment.LogMatchesDisk);
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("卷是否离线"));
        Assert.Contains(assessment.SafeNextActions, a => a.Contains("不要执行任何清理操作"));
    }

    [Fact]
    public void CorruptLogStopsAllInference()
    {
        var log = new OperationLog(_logDir);
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(log.PathFor("task_broken"), "{ not json");

        var assessment = RecoveryInspector.Inspect(log, "task_broken");

        Assert.Equal(ObservedLayout.Unrecognised, assessment.ObservedLayout);
        Assert.False(assessment.DataAccountedFor);
        Assert.Contains("不要删除任何目录", assessment.SafeNextActions[0]);
    }

    [Fact]
    public void UnknownTaskIsNotAssumedToHaveLeftNothing()
    {
        var log = new OperationLog(_logDir);
        var assessment = RecoveryInspector.Inspect(log, "task_never_ran");

        Assert.Equal(ObservedLayout.Unrecognised, assessment.ObservedLayout);
        Assert.False(assessment.DataAccountedFor);
        Assert.Contains("人工确认", assessment.SafeNextActions[0]);
    }

    [Fact]
    public void EveryAssessmentNamesItsObservationsRatherThanAssertingAVerdict()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("facts");
        var assessment = RecoveryInspector.Inspect(log, result.Record!.TaskId);

        Assert.NotEmpty(assessment.ObservedFacts);
        Assert.Contains(assessment.ObservedFacts, f => f.Contains("源路径存在"));
        Assert.Contains(assessment.ObservedFacts, f => f.Contains("源备份存在"));
        Assert.Contains(assessment.ObservedFacts, f => f.Contains("目标路径存在"));
        Assert.False(string.IsNullOrEmpty(assessment.Conclusion));
    }
}