using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W06/W07 acceptance for the Commit step:
///  - reclaiming the source space is a separate authorized action, never a side effect of a
///    successful switch (A11);
///  - the reported figure comes from observed volume free space, with the error sources named,
///    not from summing file sizes (W07);
///  - removing a link is not a recovery, so Recover rebuilds the layout and refuses to delete
///    work that appeared after the switch (A12).
/// </summary>
public class TestCommitAndRecovery : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestCommitAndRecovery()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelCommit_{Guid.NewGuid():N}");
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
                        if (FastDirectorySizer.IsReparsePoint(dir))
                        {
                            JunctionEngine.RemoveJunction(dir, out _);
                        }
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

        // Enough content that a discards-are-visible test has something real to move.
        for (int i = 0; i < 8; i++)
        {
            File.WriteAllText(Path.Combine(source, $"blob-{i}.bin"), new string('z', 4096));
        }

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

    // -----------------------------------------------------------------
    // A11: a successful switch does not reclaim space
    // -----------------------------------------------------------------

    [Fact]
    public void ASuccessfulSwitchRetainsTheBackupAndReclaimsNothing()
    {
        var (log, result) = SwitchAnAsset("retain");
        var record = result.Record!;

        Assert.Equal(OperationState.Switched, record.State);
        Assert.Equal(BackupDisposition.Retained, record.BackupDisposition);
        Assert.True(Directory.Exists(record.SourceBackupPath));

        // Committing without authorization must not delete anything.
        var blocked = MigrationCommit.Commit(log, record.TaskId, authorized: false);

        Assert.Equal(OperationStatus.Blocked, blocked.Status);
        Assert.True(Directory.Exists(record.SourceBackupPath));
        Assert.Equal(0, blocked.ReclaimedBytesObserved);
    }

    // -----------------------------------------------------------------
    // W06/W07: authorized commit reclaims, with volume evidence
    // -----------------------------------------------------------------

    [Fact]
    public void AuthorizedCommitReclaimsTheBackupAndReportsVolumeEvidence()
    {
        var (log, result) = SwitchAnAsset("recycle");
        var record = result.Record!;

        long expected = FastDirectorySizer.CalculateDirectorySize(record.SourceBackupPath);
        Assert.True(expected > 0, "备份目录应当有内容");

        var committed = MigrationCommit.Commit(log, record.TaskId, authorized: true);

        Assert.Equal(OperationStatus.Succeeded, committed.Status);
        Assert.Equal(CommitAction.Recycle, committed.Action);

        // The backup is gone and the anchor still resolves to the new location.
        Assert.False(Directory.Exists(record.SourceBackupPath));
        Assert.True(FastDirectorySizer.IsReparsePoint(record.SourcePath));
        Assert.True(File.Exists(Path.Combine(record.SourcePath, "blob-0.bin")));

        // The figure is reported and its provenance is stated.
        Assert.True(committed.ReclaimedBytesExpected > 0);
        Assert.NotEmpty(committed.MeasurementNotes);
        Assert.Contains(committed.MeasurementNotes, n => n.Contains("卷"));
        Assert.Contains(committed.MeasurementNotes, n => n.Contains("可用空间"));

        // The recorded disposition changed, and the log reflects the new state.
        var reloaded = log.Load(record.TaskId);
        Assert.Equal(OperationState.Committed, reloaded.Record!.State);
        Assert.Equal(BackupDisposition.RecycledAfterRetention, reloaded.Record.BackupDisposition);
    }

    [Fact]
    public void CommitRefusesAnyStateOtherThanSwitched()
    {
        // A task that stopped at preflight must not be committable.
        string source = Path.Combine(_root, "preflight-only");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.bin"), "x");

        var log = new OperationLog(_logDir);
        var failed = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(source, "nested"),
            AssetName = "preflight-only",
            Category = "test"
        });

        Assert.Equal(OperationState.PreflightFailed, failed.Record!.State);

        var committed = MigrationCommit.Commit(log, failed.Record.TaskId, authorized: true);

        Assert.Equal(OperationStatus.Blocked, committed.Status);
        Assert.Contains("Switched", committed.Message);
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public void CommitOnAnUnknownTaskReportsFailureRatherThanGuessing()
    {
        var log = new OperationLog(_logDir);
        var committed = MigrationCommit.Commit(log, "task_never_existed", authorized: true);

        Assert.Equal(OperationStatus.Failed, committed.Status);
        Assert.Contains("不存在", committed.Message);
        Assert.Equal(0, committed.ReclaimedBytesObserved);
    }

    [Fact]
    public void CommitOnACorruptLogRefusesInsteadOfDeletingAnything()
    {
        var log = new OperationLog(_logDir);
        string path = log.PathFor("task_broken");
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(path, "{ corrupt");

        var committed = MigrationCommit.Commit(log, "task_broken", authorized: true);

        Assert.Equal(OperationStatus.Failed, committed.Status);
        Assert.Contains("损坏", committed.Message);
        Assert.Contains("不要凭猜测删除", committed.Recovery);
    }

    // -----------------------------------------------------------------
    // A12: unlink is not recovery
    // -----------------------------------------------------------------

    [Fact]
    public void RecoverRestoresTheOriginalLayoutAndKeepsTheTargetData()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("recover");
        var record = result.Record!;

        var recovered = MigrationCommit.Recover(log, record.TaskId, authorized: true);

        Assert.Equal(OperationStatus.Recovered, recovered.Status);

        // The anchor is a real directory again with the original content.
        Assert.True(Directory.Exists(record.SourcePath));
        Assert.False(FastDirectorySizer.IsReparsePoint(record.SourcePath));
        Assert.True(File.Exists(Path.Combine(record.SourcePath, "blob-0.bin")));

        // And the data at the new location was not destroyed by the recovery.
        Assert.True(Directory.Exists(record.TargetPath));
        Assert.True(File.Exists(Path.Combine(record.TargetPath, "blob-0.bin")));
    }

    [Fact]
    public void RecoverRequiresAuthorization()
    {
        var (log, result) = SwitchAnAsset("recover-auth");
        var record = result.Record!;

        var blocked = MigrationCommit.Recover(log, record.TaskId, authorized: false);

        Assert.Equal(OperationStatus.Blocked, blocked.Status);
        Assert.True(Directory.Exists(record.SourceBackupPath));
        Assert.True(FastDirectorySizer.IsReparsePoint(record.SourcePath));
    }

    [Fact]
    public void RecoverRefusesToDeleteWorkThatAppearedAtTheAnchorAfterTheSwitch()
    {
        TestEnvironment.RequireJunctionSupport();

        var (log, result) = SwitchAnAsset("post-switch-writes");
        var record = result.Record!;

        // The anchor is a link. Replace it with a real directory holding new data, simulating a
        // tool that broke the link and wrote locally.
        Assert.True(JunctionEngine.RemoveJunction(record.SourcePath, out _));
        Directory.CreateDirectory(record.SourcePath);
        File.WriteAllText(Path.Combine(record.SourcePath, "written-after-switch.bin"), "PRECIOUS");

        var recovered = MigrationCommit.Recover(log, record.TaskId, authorized: true);

        // Recovery must not silently destroy that work.
        Assert.Equal(OperationStatus.NeedsAttention, recovered.Status);
        Assert.True(File.Exists(Path.Combine(record.SourcePath, "written-after-switch.bin")));
        Assert.Equal("PRECIOUS", File.ReadAllText(Path.Combine(record.SourcePath, "written-after-switch.bin")));

        // Both sides survive and the user is told where they are.
        Assert.True(Directory.Exists(record.SourceBackupPath));
        Assert.Contains(record.SourceBackupPath, recovered.Recovery);
    }

    [Fact]
    public void RecoverAfterCommitExplainsThatTheBackupIsGone()
    {
        var (log, result) = SwitchAnAsset("recover-after-commit");
        var record = result.Record!;

        var committed = MigrationCommit.Commit(log, record.TaskId, authorized: true);
        Assert.Equal(OperationStatus.Succeeded, committed.Status);

        var recovered = MigrationCommit.Recover(log, record.TaskId, authorized: true);

        Assert.Equal(OperationStatus.Blocked, recovered.Status);
        Assert.Contains("已被回收", recovered.Message);
        Assert.Contains(record.TargetPath, recovered.Recovery);
    }
}
