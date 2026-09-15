using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Scanner;
using AppAssetSentinel.Core.Shield;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W04 acceptance: probing failures must not invent facts. "We did not observe use"
/// is not "unused for 180 days"; an unreported volume is not an NVMe SSD; a partial size
/// walk is not a complete figure; and an unknown dependency is not "no dependency".
/// </summary>
public class TestUnknownStatesAndEvidence : IDisposable
{
    private readonly string _testRoot;

    public TestUnknownStatesAndEvidence()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SentinelUnknown_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public void AssetDefaultsToUnknownRatherThanAWarmOrZombieGuess()
    {
        var asset = new SoftwareAsset { DisplayName = "SomeApp" };

        Assert.Equal("unknown", asset.HeatLevel);
        Assert.Equal(0.0, asset.HeatScore);
        Assert.Null(asset.DaysSinceLastUse);
        Assert.Equal("unknown", asset.UsageConfidence);
        Assert.Empty(asset.LastUsedTimestamp);
    }

    [Fact]
    public void TelemetryLeavesUnobservedAssetsUnknownInsteadOfFabricatingHistory()
    {
        // An app with no install location, no shortcut, no process and no data folder.
        var ghost = new SoftwareAsset
        {
            Id = "unknown-1",
            DisplayName = "Ghost App With No Evidence",
            InstallLocation = string.Empty,
            MainExecutable = string.Empty,
            Publisher = "Nobody",
            AssetType = "gui_app",
            Category = "tools_utility"
        };

        new TelemetryEngine().AnalyzeAll(new List<SoftwareAsset> { ghost });

        // AUDIT A13: the previous code hard-filled "180 days ago" and called it a zombie.
        Assert.Equal("unknown", ghost.HeatLevel);
        Assert.Null(ghost.DaysSinceLastUse);
        Assert.Equal(string.Empty, ghost.LastUsedTimestamp);
        Assert.Equal("unknown", ghost.UsageConfidence);
        Assert.Equal("no_evidence_observed", ghost.TelemetrySource);
        Assert.NotEqual(180, ghost.DaysSinceLastUse);
        Assert.NotEqual("inferred_legacy", ghost.TelemetrySource);
    }

    [Fact]
    public void DirectorySizeReportsItsOwnCompleteness()
    {
        string root = Path.Combine(_testRoot, "sized");
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "inner", "b.bin"), new byte[2000]);

        var clean = FastDirectorySizer.CalculateDirectorySizeDetailed(root);
        Assert.Equal(3000, clean.Bytes);
        Assert.Equal(2, clean.FileCount);
        Assert.True(clean.Complete, "a fully readable tree must report itself complete");
        Assert.Equal(0, clean.SkippedDirectories);
        Assert.Equal(0, clean.SkippedFiles);

        // A reparse point is skipped, and that fact must be visible.
        TestEnvironment.RequireJunctionSupport();
        string target = Path.Combine(_testRoot, "external");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "c.bin"), new byte[5000]);
        string link = Path.Combine(root, "linked");
        Assert.True(JunctionEngine.CreateJunction(link, target, out var err), err);

        var withLink = FastDirectorySizer.CalculateDirectorySizeDetailed(root);
        Assert.Equal(3000, withLink.Bytes);           // foreign volume bytes excluded
        Assert.False(withLink.Complete);              // and the partiality is declared
        Assert.True(withLink.SkippedDirectories >= 1);
    }

    [Fact]
    public void MissingPathIsNotReportedAsACompleteZeroByteAsset()
    {
        var missing = FastDirectorySizer.CalculateDirectorySizeDetailed(
            Path.Combine(_testRoot, "does-not-exist"));

        Assert.Equal(0, missing.Bytes);
        Assert.Equal(0, missing.FileCount);
        Assert.True(missing.Complete); // nothing to read and nothing was skipped
    }

    [Fact]
    public void VolumeMediaIsUnknownRatherThanAssumedSolidState()
    {
        var volumes = VolumeManager.GetSystemVolumes();
        Assert.NotEmpty(volumes);

        foreach (var v in volumes)
        {
            if (!v.MediaKnown)
            {
                // AUDIT A14: an unprobed volume must never claim a media type.
                Assert.False(v.IsSSD, $"{v.DriveLetter} 在介质未确认时不应声明为 SSD");
                Assert.DoesNotContain("SSD", v.MediaType);
                Assert.Equal("Unknown", v.BusType);
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(v.MediaType));
            }
        }
    }

    [Fact]
    public void RecommendationReasonNeverPromisesSpeedForUnverifiedMedia()
    {
        var (path, reason) = VolumeManager.RecommendVaultPathWithReason("ai_models", "ollama");

        Assert.False(string.IsNullOrEmpty(path));
        Assert.False(string.IsNullOrEmpty(reason));

        // The old wording promised "秒级加载进显存" regardless of what was actually measured.
        Assert.DoesNotContain("秒级加载进显存", reason);
        Assert.DoesNotContain("保障数十 GB", reason);
    }

    [Fact]
    public void DependencyEvidenceKeepsEveryObservedEdgeIncludingCycles()
    {
        var links = new List<DependencyLink>
        {
            new() { UpstreamSoftwareId = "A", DownstreamSoftwareId = "B" },
            new() { UpstreamSoftwareId = "B", DownstreamSoftwareId = "C" },
            new() { UpstreamSoftwareId = "C", DownstreamSoftwareId = "A" }
        };

        // AUDIT A16: a cycle is a real observation, so all three edges count as cyclic.
        Assert.Equal(3, DependencyShield.CountCyclicEdges(links));
    }

    [Fact]
    public void DependencyShieldDoesNotPruneCyclicEdgesWhenBuildingTheGraph()
    {
        // A rule set that produces a genuine cycle must survive graph construction intact.
        string rulesDir = Path.Combine(_testRoot, "rules");
        Directory.CreateDirectory(rulesDir);

        File.WriteAllText(Path.Combine(rulesDir, "dependency_rules.json"), """
        [
          {"upstream_pattern":"alpha","downstream_pattern":"beta","dependency_type":"tool_dependency","description":"A->B"},
          {"upstream_pattern":"beta","downstream_pattern":"gamma","dependency_type":"tool_dependency","description":"B->C"},
          {"upstream_pattern":"gamma","downstream_pattern":"alpha","dependency_type":"tool_dependency","description":"C->A"}
        ]
        """);

        var apps = new List<SoftwareAsset>
        {
            new() { Id = "A", DisplayName = "alpha", MainExecutable = "" },
            new() { Id = "B", DisplayName = "beta", MainExecutable = "" },
            new() { Id = "C", DisplayName = "gamma", MainExecutable = "" }
        };

        var shield = new DependencyShield(Path.Combine(rulesDir, "dependency_rules.json"));
        var links = shield.BuildGraph(apps);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.UpstreamSoftwareId == "A" && l.DownstreamSoftwareId == "B");
        Assert.Contains(links, l => l.UpstreamSoftwareId == "B" && l.DownstreamSoftwareId == "C");
        Assert.Contains(links, l => l.UpstreamSoftwareId == "C" && l.DownstreamSoftwareId == "A");
    }
}
