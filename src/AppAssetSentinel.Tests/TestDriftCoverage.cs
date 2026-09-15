using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W09 acceptance: "注册状态可重建，不因无记录返回已健康".
///
/// The failure mode being guarded: a report over an empty or partial registration set returns
/// drifted_count = 0, which reads as "everything is healthy" when in fact nothing was examined.
/// The report must distinguish "audited and healthy" from "not audited".
/// </summary>
public class TestDriftCoverage : IDisposable
{
    private readonly string _root;

    public TestDriftCoverage()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelCoverage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
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
            }

            for (int i = 0; i < 3 && Directory.Exists(_root); i++)
            {
                try { Directory.Delete(_root, true); }
                catch { System.Threading.Thread.Sleep(30); }
            }
        }
        catch { }
    }

    /// <summary>
    /// A genuinely healthy registration: the vault holds the data and the anchor is a real
    /// junction pointing at it. A plain directory at the anchor is AnchorReplaced, not Healthy.
    /// </summary>
    private VaultRegistration HealthyRegistration(string name)
    {
        TestEnvironment.RequireJunctionSupport();

        string vault = Path.Combine(_root, name, "vault");
        string anchor = Path.Combine(_root, name, "anchor");
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(vault, "a.bin"), "same");

        Assert.True(JunctionEngine.CreateJunction(anchor, vault, out var err), err);

        return new VaultRegistration
        {
            Id = "reg-" + name,
            AssetName = name,
            VirtualAnchorPath = anchor,
            PhysicalVaultPath = vault
        };
    }

    [Fact]
    public void AnEmptyRegistrationSetIsNotReportedAsHealthy()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();

        var report = DriftDiagnostics.Audit(
            policy,
            new List<VaultRegistration>(),
            new[] { Path.Combine(_root, "orphan-asset") });

        // Nothing was audited, so the report must not read as a clean bill of health.
        Assert.Equal(0, report.TotalRegistrations);
        Assert.Equal(0, report.DriftedCount);
        Assert.False(report.CoverageComplete);
        Assert.False(report.AllVerifiedHealthy);
        Assert.Single(report.UnverifiedPaths);
        Assert.False(report.RepairAvailable);
        Assert.Contains("没有登记记录", report.RepairUnavailableReason);
    }

    [Fact]
    public void ARegistrationWithoutACandidateIsStillCompleteCoverage()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();
        var registration = HealthyRegistration("plain");

        var report = DriftDiagnostics.Audit(
            policy,
            new List<VaultRegistration> { registration },
            new[] { registration.VirtualAnchorPath });

        Assert.True(report.CoverageComplete);
        Assert.Empty(report.UnverifiedPaths);

        // Verified healthy here really does mean it was examined.
        Assert.Equal(0, report.DriftedCount);
    }

    [Fact]
    public void AHealthyAuditWithAPartialRecordSetIsNotCalledHealthy()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();
        var registration = HealthyRegistration("one");

        var report = DriftDiagnostics.Audit(
            policy,
            new List<VaultRegistration> { registration },
            new[] { registration.VirtualAnchorPath, Path.Combine(_root, "never-registered") });

        // The one registration is genuinely healthy, but coverage is not complete.
        Assert.Equal(0, report.DriftedCount);
        Assert.False(report.CoverageComplete);
        Assert.False(report.AllVerifiedHealthy);
        Assert.Single(report.UnverifiedPaths);
        Assert.Contains("never-registered", report.UnverifiedPaths[0]);
    }

    [Fact]
    public void ThePlainOverloadKeepsItsOriginalMeaning()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();
        var registration = HealthyRegistration("solo");

        var report = DriftDiagnostics.Audit(policy, new List<VaultRegistration> { registration });

        Assert.True(report.CoverageComplete);
        Assert.True(report.AllVerifiedHealthy);
        Assert.Empty(report.UnverifiedPaths);
    }

    [Fact]
    public void CoverageComparisonIsCaseAndSeparatorInsensitive()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();
        var registration = HealthyRegistration("case");

        var report = DriftDiagnostics.Audit(
            policy,
            new List<VaultRegistration> { registration },
            new[] { registration.VirtualAnchorPath.ToUpperInvariant() + Path.DirectorySeparatorChar });

        Assert.True(report.CoverageComplete,
            "同一路径的大小写与尾分隔符差异不应被当作缺记录");
    }
}