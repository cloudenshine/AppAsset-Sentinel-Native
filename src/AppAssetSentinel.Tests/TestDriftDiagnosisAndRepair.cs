using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W09 acceptance: a diagnosis must not invent a cause, and a repair must not destroy
/// either side of a conflict. The previous implementation picked the newer timestamp and then
/// deleted the other directory, which lost one of two divergent versions (A06).
/// </summary>
public class TestDriftDiagnosisAndRepair : IDisposable
{
    private readonly string _root;

    public TestDriftDiagnosisAndRepair()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelDrift_{Guid.NewGuid():N}");
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

    private VaultRegistration MakeRegistration(
        string anchorName = "anchor",
        string vaultName = "vault",
        string envVar = "")
    {
        string anchor = Path.Combine(_root, anchorName);
        string vault = Path.Combine(_root, vaultName);
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(vault);

        return new VaultRegistration
        {
            Id = "reg-" + anchorName,
            AssetName = "Ollama",
            VirtualAnchorPath = anchor,
            PhysicalVaultPath = vault,
            SyncedEnvVar = envVar
        };
    }

    // -----------------------------------------------------------------
    // W09: conflicting content is classified, never resolved by timestamp
    // -----------------------------------------------------------------

    [Fact]
    public void SameNameDifferentContentIsClassifiedAsConflictAndBothSidesSurvive()
    {
        var reg = MakeRegistration();

        // The audit's exact scenario: two divergent versions of the same file.
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "model.bin"), "UNIQUE-ANCHOR");
        File.WriteAllText(Path.Combine(reg.PhysicalVaultPath, "model.bin"), "UNIQUE-VAULT!");

        // Force the equal-timestamp case the old code got wrong.
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(reg.VirtualAnchorPath, "model.bin"), t);
        File.SetLastWriteTimeUtc(Path.Combine(reg.PhysicalVaultPath, "model.bin"), t);

        var plan = DriftRepairPlanner.BuildPlan(reg, Array.Empty<DriftFinding>());

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(DriftFileClass.Conflicting, entry.Classification);
        Assert.False(plan.SafeToApplyAutomatically);
        Assert.NotEmpty(plan.RequiresManualDecision);

        // Applying is refused and nothing is destroyed.
        var outcome = DriftRepairPlanner.Apply(plan, authorized: true);
        Assert.Equal(OperationStatus.NeedsAttention, outcome.Status);
        Assert.False(outcome.DidMutate);

        Assert.Equal("UNIQUE-ANCHOR", File.ReadAllText(Path.Combine(reg.VirtualAnchorPath, "model.bin")));
        Assert.Equal("UNIQUE-VAULT!", File.ReadAllText(Path.Combine(reg.PhysicalVaultPath, "model.bin")));
    }

    [Fact]
    public void NewerAnchorContentDoesNotOverwriteTheVaultVersion()
    {
        var reg = MakeRegistration();

        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "model.bin"), "NEWER-ANCHOR");
        File.WriteAllText(Path.Combine(reg.PhysicalVaultPath, "model.bin"), "OLDER-VAULT");

        // Anchor is strictly newer: the old code overwrote the vault copy.
        File.SetLastWriteTimeUtc(Path.Combine(reg.VirtualAnchorPath, "model.bin"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(reg.PhysicalVaultPath, "model.bin"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var plan = DriftRepairPlanner.BuildPlan(reg, Array.Empty<DriftFinding>());
        Assert.False(plan.SafeToApplyAutomatically);

        var outcome = DriftRepairPlanner.Apply(plan, authorized: true);
        Assert.False(outcome.DidMutate);

        Assert.Equal("NEWER-ANCHOR", File.ReadAllText(Path.Combine(reg.VirtualAnchorPath, "model.bin")));
        Assert.Equal("OLDER-VAULT", File.ReadAllText(Path.Combine(reg.PhysicalVaultPath, "model.bin")));
    }

    [Fact]
    public void AnchorOnlyFilesAreIdentifiedForMergeWithoutTouchingConflictingOnes()
    {
        var reg = MakeRegistration();

        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "new-only.bin"), "FRESH");
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "shared.bin"), "SAME");
        File.WriteAllText(Path.Combine(reg.PhysicalVaultPath, "shared.bin"), "SAME");

        var plan = DriftRepairPlanner.BuildPlan(reg, Array.Empty<DriftFinding>());

        Assert.True(plan.SafeToApplyAutomatically);
        Assert.Empty(plan.RequiresManualDecision);
        Assert.Single(plan.Entries, e => e.Classification == DriftFileClass.AnchorOnly);
        Assert.Single(plan.Entries, e => e.Classification == DriftFileClass.Identical);
    }

    [Fact]
    public void RepairRequiresExplicitAuthorization()
    {
        var reg = MakeRegistration();
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "only.bin"), "x");

        var plan = DriftRepairPlanner.BuildPlan(reg, Array.Empty<DriftFinding>());

        var outcome = DriftRepairPlanner.Apply(plan, authorized: false);

        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.False(outcome.DidMutate);
        Assert.True(File.Exists(Path.Combine(reg.VirtualAnchorPath, "only.bin")));
    }

    // -----------------------------------------------------------------
    // W09: an offline volume is a diagnosis, not a cleanup opportunity
    // -----------------------------------------------------------------

    [Fact]
    public void OfflineVaultIsReportedAsUnavailableRatherThanRemovableResidue()
    {
        var reg = MakeRegistration();
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "a.bin"), "data");

        // Simulate an offline volume by pointing the vault at a path under a missing root.
        var offline = new VaultRegistration
        {
            Id = reg.Id,
            AssetName = reg.AssetName,
            VirtualAnchorPath = reg.VirtualAnchorPath,
            PhysicalVaultPath = Path.Combine(_root, "offline-volume", "models")
        };

        var policy = CapabilityPolicy.SafeObservationDefault();
        var report = DriftDiagnostics.Audit(policy, new List<VaultRegistration> { offline });

        var finding = Assert.Single(report.Findings);
        Assert.Equal(DriftKind.TargetMissing, finding.Kind);
        Assert.Contains("不可访问", finding.Details);

        var plan = DriftRepairPlanner.BuildPlan(offline, report.Findings);
        Assert.False(plan.SafeToApplyAutomatically);
        Assert.Contains("先恢复该卷", plan.Summary);

        // The anchor data was not touched to "clean up" the missing target.
        Assert.True(File.Exists(Path.Combine(reg.VirtualAnchorPath, "a.bin")));
    }

    // -----------------------------------------------------------------
    // W09: configuration deviation is a distinct, named cause
    // -----------------------------------------------------------------

    [Fact]
    public void ConfigurationDeviationIsDetectedAndNamed()
    {
        var reg = MakeRegistration(envVar: "OLLAMA_MODELS");

        string deviated = DriftRepairPlanner.DescribeConfigDeviation(reg, @"C:\somewhere\else");
        Assert.Contains("配置偏离", deviated);
        Assert.Contains("OLLAMA_MODELS", deviated);

        string unset = DriftRepairPlanner.DescribeConfigDeviation(reg, null);
        Assert.Contains("未设置", unset);

        string matching = DriftRepairPlanner.DescribeConfigDeviation(reg, reg.PhysicalVaultPath);
        Assert.Equal(string.Empty, matching);
    }

    // -----------------------------------------------------------------
    // W09: the diagnosis never invents a root cause
    // -----------------------------------------------------------------

    [Fact]
    public void DiagnosisReportsObservedFactsWithoutAssertingAnUnprovenCause()
    {
        var reg = MakeRegistration();

        // Anchor exists but is a plain directory (a replaced link), vault exists.
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "drifted.bin"), "x");

        var policy = CapabilityPolicy.SafeObservationDefault();
        var report = DriftDiagnostics.Audit(policy, new List<VaultRegistration> { reg });

        var finding = Assert.Single(report.Findings);
        Assert.Equal(DriftKind.AnchorReplaced, finding.Kind);

        // The old implementation blamed "software upgrade". Asserting an unproven cause is
        // exactly what the audit rejected, so no cause is claimed anywhere in the report.
        Assert.DoesNotContain("软件升级", finding.Details);
        Assert.DoesNotContain("软件升级", finding.RecommendedAction);
        Assert.NotEmpty(finding.ObservedFacts);

        // Diagnosis is still read-only even though a repair path now exists.
        Assert.True(File.Exists(Path.Combine(reg.VirtualAnchorPath, "drifted.bin")));
        Assert.False(report.RepairAvailable);
    }

    [Fact]
    public void MergeCallbacksAreNotInvokedWhenConflictsExist()
    {
        var reg = MakeRegistration();
        File.WriteAllText(Path.Combine(reg.VirtualAnchorPath, "c.bin"), "ANCHOR");
        File.WriteAllText(Path.Combine(reg.PhysicalVaultPath, "c.bin"), "VAULT");

        var plan = DriftRepairPlanner.BuildPlan(reg, Array.Empty<DriftFinding>());

        bool moverCalled = false;
        var outcome = DriftRepairPlanner.Apply(plan, authorized: true, fileMover: (_, _) =>
        {
            moverCalled = true;
            return true;
        });

        Assert.False(moverCalled);
        Assert.Equal(OperationStatus.NeedsAttention, outcome.Status);
    }
}
