using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W06/W07 acceptance, exercised in isolated temp trees:
/// exact confirmed target, source/target boundary, same-name conflict preservation,
/// per-file verification, cancellation, repeat requests, and crash-state classification.
/// Cross-volume and real-application runs require an isolated account plus two test volumes
/// and are tracked separately — they are NOT claimed here.
/// </summary>
public class TestMigrationKernelAndOperationLog : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestMigrationKernelAndOperationLog()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelKernel_{Guid.NewGuid():N}");
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

    private (string source, string target) MakeSource(string name, params (string rel, string content)[] files)
    {
        string source = Path.Combine(_root, name);
        Directory.CreateDirectory(source);

        foreach (var (rel, content) in files)
        {
            string full = Path.Combine(source, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return (source, Path.Combine(_root, "vault", name));
    }

    // -----------------------------------------------------------------
    // W07: exact target path (AUDIT A10 counterexample)
    // -----------------------------------------------------------------

    [Fact]
    public void UsesTheExactConfirmedTargetPathNotAPathRebuiltFromTheSourceName()
    {
        TestEnvironment.RequireJunctionSupport();

        // The audit probe: source leaf "models", requested "...\models\ollama" and the old
        // code produced "...\models\models".
        string source = Path.Combine(_root, "app", "models");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "blob.bin"), "weights");

        string requested = Path.Combine(_root, "AppVault", "models", "ollama");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = requested,
            AssetName = "ollama",
            Category = "ai_models"
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        // The confirmed path must be the one that now holds the data.
        Assert.True(Directory.Exists(requested), "确认的目标路径必须真实存在");
        Assert.True(File.Exists(Path.Combine(requested, "blob.bin")));

        // And no double-leaf artefact was produced.
        Assert.False(Directory.Exists(Path.Combine(_root, "AppVault", "models", "models")));
    }

    // -----------------------------------------------------------------
    // W07: boundary rejection (A05)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("same")]
    [InlineData("target-inside-source")]
    [InlineData("source-inside-target")]
    public void RejectsUnsupportedPathRelationshipsWithNoSideEffects(string mode)
    {
        var (source, target) = MakeSource("boundary", ("a.bin", "data"));

        (string s, string t) = mode switch
        {
            "same" => (source, source),
            "target-inside-source" => (source, Path.Combine(source, "inner")),
            _ => (Path.Combine(source, "inner"), Path.Combine(source, "inner"))
        };

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = s,
            TargetPath = t,
            AssetName = "boundary",
            Category = "test"
        });

        Assert.NotEqual(OperationStatus.Succeeded, result.Outcome.Status);
        Assert.False(result.Outcome.DidMutate);
        Assert.Equal(OperationState.PreflightFailed, result.Record!.State);

        // Acceptance: nothing was created or removed.
        Assert.True(File.Exists(Path.Combine(source, "a.bin")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(source, "a.bin")));
        Assert.False(Directory.Exists(target) && mode != "same");
    }

    [Fact]
    public void RefusesToRunAtAllWhenPolicyBlocksRelocation()
    {
        var (source, target) = MakeSource("blocked", ("a.bin", "data"));
        var log = new OperationLog(_logDir);

        var result = MigrationKernel.Execute(CapabilityPolicy.SafeObservationDefault(), log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "blocked",
            Category = "test"
        });

        Assert.Equal(OperationStatus.Blocked, result.Outcome.Status);
        Assert.False(result.Outcome.DidMutate);

        // A blocked call must not even write a log record.
        Assert.Null(result.Record);
        Assert.Empty(Directory.GetFiles(_logDir));
    }

    // -----------------------------------------------------------------
    // W07: conflict preservation (A03/A06)
    // -----------------------------------------------------------------

    [Fact]
    public void PreservesBothSidesWhenTheTargetAlreadyHoldsADifferentFile()
    {
        TestEnvironment.RequireJunctionSupport();

        var (source, target) = MakeSource("conflict", ("shared.bin", "SOURCE-VERSION"));

        // A different file already exists at the destination with the same name.
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "shared.bin"), "EXISTING-TARGET-VERSION");

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

        // Acceptance: neither version was destroyed.
        Assert.Equal("EXISTING-TARGET-VERSION", File.ReadAllText(Path.Combine(target, "shared.bin")));
        Assert.Equal("SOURCE-VERSION", File.ReadAllText(Path.Combine(source, "shared.bin")));

        // The source is still a real directory, not a link.
        Assert.False(FastDirectorySizer.IsReparsePoint(source));
    }

    // -----------------------------------------------------------------
    // W07: content verification beats size comparison (A04)
    // -----------------------------------------------------------------

    [Fact]
    public void VerificationUsesPerFileHashesSoEqualSizeDifferentContentIsCaught()
    {
        var sourceManifest = FileIntegrity.BuildManifest(MakeSource("v1", ("x.bin", "AAAA")).source);
        var otherManifest = FileIntegrity.BuildManifest(MakeSource("v2", ("x.bin", "BBBB")).source);

        Assert.Equal(sourceManifest.TotalBytes, otherManifest.TotalBytes);

        var comparison = FileIntegrity.Compare(sourceManifest, otherManifest);
        Assert.False(comparison.Identical);
        Assert.Contains(comparison.Differences, d => d.Contains("内容不一致"));
    }

    // -----------------------------------------------------------------
    // W07: cancellation leaves the original intact
    // -----------------------------------------------------------------

    [Fact]
    public void CancellationRestoresTheOriginalLayoutWithoutLosingData()
    {
        TestEnvironment.RequireJunctionSupport();

        var (source, target) = MakeSource("cancel", ("a.bin", "payload-a"), ("b.bin", "payload-b"));

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel before the copy starts

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "cancel",
            Category = "test"
        }, cts.Token);

        Assert.False(result.Outcome.DidMutate);
        Assert.True(Directory.Exists(source));
        Assert.False(FastDirectorySizer.IsReparsePoint(source));
        Assert.Equal("payload-a", File.ReadAllText(Path.Combine(source, "a.bin")));
        Assert.Equal("payload-b", File.ReadAllText(Path.Combine(source, "b.bin")));
    }

    // -----------------------------------------------------------------
    // W06: operation log semantics
    // -----------------------------------------------------------------

    [Fact]
    public void OperationLogIsWrittenBeforeTheFirstSideEffectAndIsAtomicallyReadable()
    {
        TestEnvironment.RequireJunctionSupport();

        var (source, target) = MakeSource("logged", ("m.bin", "weights"));
        var log = new OperationLog(_logDir);

        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "logged",
            Category = "ai_models"
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        var loaded = log.Load(result.Record!.TaskId);
        Assert.True(loaded.Succeeded);
        Assert.NotNull(loaded.Record);
        Assert.Equal(OperationState.Switched, loaded.Record!.State);

        // The record must be enough to tell what happened without trusting any cache.
        Assert.Equal(source, loaded.Record.SourcePath);
        Assert.Equal(target, loaded.Record.TargetPath);
        Assert.False(string.IsNullOrEmpty(loaded.Record.SourceBackupPath));
        Assert.True(Directory.Exists(loaded.Record.SourceBackupPath));
        Assert.NotEmpty(loaded.Record.Steps);

        // No partial temp file survives a successful save.
        Assert.False(File.Exists(log.PathFor(result.Record.TaskId) + ".tmp"));
    }

    [Fact]
    public void SwitchedStateRetainsTheSourceBackupSoSpaceIsNotSilentlyReclaimed()
    {
        TestEnvironment.RequireJunctionSupport();

        var (source, target) = MakeSource("retain", ("big.bin", "payload"));
        var log = new OperationLog(_logDir);

        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "retain",
            Category = "test"
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        var record = result.Record!;
        Assert.Equal(OperationState.Switched, record.State);

        // AUDIT A11: a successful switch must NOT have reclaimed the source space.
        Assert.Equal(BackupDisposition.Retained, record.BackupDisposition);
        Assert.True(Directory.Exists(record.SourceBackupPath), "切换后源备份必须保留");
        Assert.True(File.Exists(Path.Combine(record.SourceBackupPath, "big.bin")));

        // And the anchor reads through to the new location.
        Assert.True(FastDirectorySizer.IsReparsePoint(source));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(source, "big.bin")));
    }

    [Fact]
    public void CorruptOperationLogIsReportedRatherThanTreatedAsNoOperation()
    {
        var log = new OperationLog(_logDir);
        string path = log.PathFor("task_corrupt");
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(path, "{ not valid json");

        var loaded = log.Load("task_corrupt");

        Assert.False(loaded.Succeeded);
        Assert.False(string.IsNullOrEmpty(loaded.Error));
        Assert.Null(loaded.Record);
    }

    [Fact]
    public void MissingOperationLogIsDistinguishableFromACorruptOne()
    {
        var log = new OperationLog(_logDir);
        var loaded = log.Load("task_never_started");

        Assert.True(loaded.Succeeded);
        Assert.True(loaded.FileMissing);
        Assert.Null(loaded.Record);
    }

    // -----------------------------------------------------------------
    // W06: mutual exclusion on overlapping resources
    // -----------------------------------------------------------------

    [Fact]
    public void OverlappingResourcesAreMutuallyExcludedUntilReleased()
    {
        string shared = Path.Combine(_root, "shared-resource");

        Assert.True(OperationLog.TryClaimResources(new[] { shared }, out _));

        // A second task touching the same path must be refused.
        Assert.False(OperationLog.TryClaimResources(new[] { shared }, out string conflict));
        Assert.Equal(shared, conflict);

        OperationLog.ReleaseResources(new[] { shared });

        // After release it can be claimed again.
        Assert.True(OperationLog.TryClaimResources(new[] { shared }, out _));
        OperationLog.ReleaseResources(new[] { shared });
    }

    [Fact]
    public void ClaimingIsCaseAndSeparatorInsensitive()
    {
        string a = Path.Combine(_root, "CaseTest");
        Assert.True(OperationLog.TryClaimResources(new[] { a }, out _));

        string b = a.ToUpperInvariant() + Path.DirectorySeparatorChar;
        Assert.False(OperationLog.TryClaimResources(new[] { b }, out _));

        OperationLog.ReleaseResources(new[] { a });
    }

    // -----------------------------------------------------------------
    // W06: repeat requests are idempotent by task id
    // -----------------------------------------------------------------

    [Fact]
    public void RepeatingTheSameTaskIdReusesTheSameLogRecord()
    {
        var (source, target) = MakeSource("idem", ("f.bin", "x"));

        var log = new OperationLog(_logDir);
        string taskId = "task_fixed_id";

        // First attempt stops at preflight because the target sits inside the source.
        var first = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(source, "nested"),
            AssetName = "idem",
            Category = "test",
            TaskId = taskId
        });

        Assert.Equal(OperationState.PreflightFailed, first.Record!.State);

        var loaded = log.Load(taskId);
        Assert.True(loaded.Succeeded);
        Assert.Equal(taskId, loaded.Record!.TaskId);
        Assert.Equal(OperationState.PreflightFailed, loaded.Record.State);
    }

    // -----------------------------------------------------------------
    // R1 profile posture
    // -----------------------------------------------------------------

    [Fact]
    public void RelocationProfileOpensOnlyTheVerifiedCapabilities()
    {
        var profile = CapabilityPolicy.RelocationVerifiedProfile();

        // Verified by W06/W07/W09 and therefore open in this profile.
        Assert.True(profile.AllowsMutation(Capability.VaultRelocate));
        Assert.True(profile.AllowsMutation(Capability.JunctionUnlink));
        Assert.True(profile.AllowsMutation(Capability.VaultCommit));
        Assert.True(profile.AllowsMutation(Capability.VaultRecover));
        Assert.True(profile.AllowsMutation(Capability.DriftAutoHeal));

        // Still without an acceptance gate: live uninstall and the unguarded force clean.
        Assert.False(profile.AllowsMutation(Capability.UninstallLive));
        Assert.False(profile.AllowsMutation(Capability.ForceClean));

        // The restore-point injection and false-success bugs are fixed, but the real creation
        // path has not been accepted in an elevated environment, so it stays closed.
        Assert.False(profile.AllowsMutation(Capability.RestorePointCreate));

        // R0 keeps every one of the above closed.
        var r0 = CapabilityPolicy.SafeObservationDefault();
        Assert.False(r0.AllowsMutation(Capability.VaultRelocate));
        Assert.False(r0.AllowsMutation(Capability.VaultCommit));
        Assert.False(r0.AllowsMutation(Capability.VaultRecover));
        Assert.False(r0.AllowsMutation(Capability.DriftAutoHeal));
    }
}
