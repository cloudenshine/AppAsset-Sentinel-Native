using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W02: these tests must never write real user settings. Every persistence path is
/// a fresh temp file, environment access goes through an in-memory store, and nothing here
/// touches the production vault registry or a user environment variable.
/// </summary>
public class TestVaultPolicyAndBoundaries : IDisposable
{
    private readonly string _testRoot;

    public TestVaultPolicyAndBoundaries()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SentinelVaultTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                foreach (var dir in Directory.GetDirectories(_testRoot, "*", SearchOption.AllDirectories))
                {
                    if (FastDirectorySizer.IsReparsePoint(dir))
                    {
                        JunctionEngine.RemoveJunction(dir, out _);
                    }
                }

                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    // -----------------------------------------------------------------
    // W01 — capability gate
    // -----------------------------------------------------------------

    [Fact]
    public void SafeObservationPolicyBlocksEveryDataRewritingCapability()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();

        // Read-only work stays open.
        Assert.True(policy.Check(Capability.ObserveReadOnly).IsAllowed);
        Assert.True(policy.Check(Capability.PlanPreview).IsAllowed);
        Assert.True(policy.Check(Capability.DriftDiagnose).IsAllowed);

        // Everything that rewrites user data is closed.
        Assert.False(policy.AllowsMutation(Capability.UninstallLive));
        Assert.False(policy.AllowsMutation(Capability.ForceClean));
        Assert.False(policy.AllowsMutation(Capability.VaultRelocate));
        Assert.False(policy.AllowsMutation(Capability.JunctionUnlink));
        Assert.False(policy.AllowsMutation(Capability.DriftAutoHeal));
        Assert.False(policy.AllowsMutation(Capability.RestorePointCreate));

        // A capability nobody declared must never be implicitly allowed.
        var undeclared = policy.Check((Capability)9999);
        Assert.Equal(CapabilityState.Unsupported, undeclared.State);
    }

    [Fact]
    public void RelocationUnderBlockedPolicyReturnsBlockedAndTouchesNothing()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();
        string source = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "model.bin"), "ORIGINAL");

        var outcome = AssetVaultEngine.RelocateAndDualLock(
            policy, source, Path.Combine(_testRoot, "vault"), "ollama", "ai_models");

        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.False(outcome.DidMutate);

        // Acceptance: the source is byte-for-byte unchanged and no junction appeared.
        Assert.True(File.Exists(Path.Combine(source, "model.bin")));
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(source, "model.bin")));
        Assert.False(FastDirectorySizer.IsReparsePoint(source));
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "vault")));
    }

    // -----------------------------------------------------------------
    // A18 — registry persistence
    // -----------------------------------------------------------------

    [Fact]
    public void VaultRegistryPersistsAtomicallyAndReportsCorruption()
    {
        string registryPath = Path.Combine(_testRoot, "vault_registry.json");

        Assert.True(VaultRegistry.Load(registryPath).FileMissing);

        VaultRegistry.Save(new List<VaultRegistration>
        {
            new()
            {
                AssetName = "ollama",
                VirtualAnchorPath = @"C:\x\models",
                PhysicalVaultPath = @"D:\vault\models"
            }
        }, registryPath);

        var loaded = VaultRegistry.Load(registryPath);
        Assert.True(loaded.Succeeded);
        Assert.Single(loaded.Registrations);

        // Corrupt the file: the loader must report failure, not silently return "empty".
        File.WriteAllText(registryPath, "{ this is not json");
        var corrupted = VaultRegistry.Load(registryPath);
        Assert.False(corrupted.Succeeded);
        Assert.False(string.IsNullOrEmpty(corrupted.Error));
        Assert.Empty(corrupted.Registrations);

        // No stray temp file should survive a successful save.
        Assert.False(File.Exists(registryPath + ".tmp"));
    }

    // -----------------------------------------------------------------
    // A05 — path boundary validation
    // -----------------------------------------------------------------

    [Fact]
    public void PathBoundaryRejectsSameInsideAndAliasedPaths()
    {
        string source = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(source);

        // Same path
        Assert.Equal(PathBoundaryVerdict.SamePath, PathIdentity.ValidateRelocation(source, source).Verdict);

        // Target inside source (self-recursive copy)
        Assert.Equal(PathBoundaryVerdict.TargetInsideSource,
            PathIdentity.ValidateRelocation(source, Path.Combine(source, "inner")).Verdict);

        // Source inside target
        string outer = Path.Combine(_testRoot, "outer");
        Directory.CreateDirectory(outer);
        Assert.Equal(PathBoundaryVerdict.SourceInsideTarget,
            PathIdentity.ValidateRelocation(Path.Combine(outer, "inner"), outer).Verdict);

        // Trailing separator / case / relative spelling must not create an alias escape.
        Assert.Equal(PathBoundaryVerdict.SamePath,
            PathIdentity.ValidateRelocation(source + Path.DirectorySeparatorChar, source.ToUpperInvariant()).Verdict);

        // Empty input
        Assert.Equal(PathBoundaryVerdict.EmptyInput, PathIdentity.ValidateRelocation("", source).Verdict);

        // A legitimate sibling target is accepted.
        string target = Path.Combine(_testRoot, "vault", "models");
        Assert.True(PathIdentity.ValidateRelocation(source, target).IsOk);
    }

    // -----------------------------------------------------------------
    // A04 — integrity is content, not size
    // -----------------------------------------------------------------

    [Fact]
    public void ManifestComparisonRejectsEqualSizeDifferentContent()
    {
        string a = Path.Combine(_testRoot, "a");
        string b = Path.Combine(_testRoot, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        // The audit's probe: both are four bytes, content differs.
        File.WriteAllText(Path.Combine(a, "payload.bin"), "AAAA");
        File.WriteAllText(Path.Combine(b, "payload.bin"), "BBBB");

        var ma = FileIntegrity.BuildManifest(a);
        var mb = FileIntegrity.BuildManifest(b);

        Assert.Equal(ma.TotalBytes, mb.TotalBytes);

        var comparison = FileIntegrity.Compare(ma, mb);
        Assert.False(comparison.Identical);
        Assert.Contains(comparison.Differences, d => d.Contains("内容不一致"));
    }

    [Fact]
    public void ManifestComparisonDetectsMissingAndExtraFiles()
    {
        string expected = Path.Combine(_testRoot, "exp");
        string actual = Path.Combine(_testRoot, "act");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(actual);

        File.WriteAllText(Path.Combine(expected, "keep.bin"), "same");
        File.WriteAllText(Path.Combine(actual, "keep.bin"), "same");
        File.WriteAllText(Path.Combine(expected, "only-in-source.bin"), "x");

        var comparison = FileIntegrity.Compare(
            FileIntegrity.BuildManifest(expected),
            FileIntegrity.BuildManifest(actual));

        Assert.False(comparison.Identical);
        Assert.Contains(comparison.Differences, d => d.Contains("缺失"));
    }

    // -----------------------------------------------------------------
    // A06 / W09 — diagnosis without mutation
    // -----------------------------------------------------------------

    [Fact]
    public void DriftDiagnosisDetectsReplacedAnchorAndRefusesToRepair()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();

        // Simulate a drifted anchor: the path is now a plain directory, not a junction.
        string anchor = Path.Combine(_testRoot, "anchor");
        string vault = Path.Combine(_testRoot, "vault");
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(anchor, "drifted.bin"), "NEW-DRIFTED-DATA");
        File.WriteAllText(Path.Combine(vault, "original.bin"), "ORIGINAL-VAULT-DATA");

        var registrations = new List<VaultRegistration>
        {
            new()
            {
                Id = "reg-test-1",
                AssetName = "Ollama",
                VirtualAnchorPath = anchor,
                PhysicalVaultPath = vault
            }
        };
        VaultRegistry.Save(registrations, Path.Combine(_testRoot, "vault_registry.json"));

        // Point the watchdog at this temp registry so production state is untouched.
        var report = DriftDiagnostics.Audit(policy, registrations);

        Assert.Equal(1, report.DriftedCount);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(DriftKind.AnchorReplaced, finding.Kind);
        Assert.False(report.RepairAvailable);

        // Acceptance: diagnosis did not merge, overwrite or delete either side.
        Assert.True(File.Exists(Path.Combine(anchor, "drifted.bin")));
        Assert.Equal("NEW-DRIFTED-DATA", File.ReadAllText(Path.Combine(anchor, "drifted.bin")));
        Assert.True(File.Exists(Path.Combine(vault, "original.bin")));
        Assert.Equal("ORIGINAL-VAULT-DATA", File.ReadAllText(Path.Combine(vault, "original.bin")));

        var repair = DriftWatchdog.RepairDrift(policy, "reg-test-1");
        Assert.Equal(OperationStatus.Blocked, repair.Status);
        Assert.False(repair.DidMutate);

        // Still intact after the refused repair.
        Assert.True(File.Exists(Path.Combine(anchor, "drifted.bin")));
        Assert.True(File.Exists(Path.Combine(vault, "original.bin")));
    }

    [Fact]
    public void DriftDiagnosisReportsMissingTargetInsteadOfTreatingItAsResidue()
    {
        var policy = CapabilityPolicy.SafeObservationDefault();

        string anchor = Path.Combine(_testRoot, "anchor2");
        Directory.CreateDirectory(anchor);

        var registrations = new List<VaultRegistration>
        {
            new()
            {
                Id = "reg-test-2",
                AssetName = "Docker",
                VirtualAnchorPath = anchor,
                PhysicalVaultPath = Path.Combine(_testRoot, "offline-vault")
            }
        };

        var report = DriftDiagnostics.Audit(policy, registrations);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(DriftKind.TargetMissing, finding.Kind);
        Assert.Contains("不可访问", finding.Details);
    }
}
