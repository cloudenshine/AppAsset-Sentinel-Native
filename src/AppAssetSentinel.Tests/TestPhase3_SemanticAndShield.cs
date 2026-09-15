using AppAssetSentinel.Core.Models;
using AppAssetSentinel.Core.Semantic;
using AppAssetSentinel.Core.Shield;
using Xunit;

namespace AppAssetSentinel.Tests;

public class TestPhase3_SemanticAndShield
{
    private readonly SemanticRuleEngine _semanticEngine;
    private readonly DependencyShield _shield;

    public TestPhase3_SemanticAndShield()
    {
        _semanticEngine = new SemanticRuleEngine();
        _shield = new DependencyShield();
    }

    [Fact]
    public void TestAssetSemanticEnrichment()
    {
        var apps = new List<SoftwareAsset>
        {
            new() { Id = "app_1", DisplayName = "Python 3.12.10 (64-bit)" },
            new() { Id = "app_2", DisplayName = "Ollama" },
            new() { Id = "app_3", DisplayName = "ComfyUI" },
            new() { Id = "app_4", DisplayName = "Cherry Studio" },
            new() { Id = "app_5", DisplayName = "百度网盘" }
        };

        _semanticEngine.EnrichAll(apps);

        var python = apps.First(a => a.Id == "app_1");
        Assert.Equal("runtime_environment", python.AssetType);
        Assert.True(python.IsProtected);
        Assert.Equal("infrastructure", python.HeatLevel);

        var ollama = apps.First(a => a.Id == "app_2");
        Assert.Contains(ollama.Category, new[] { "ai_compute", "ai_workstation" });
        Assert.True(ollama.IsProtected);
        Assert.Contains(ollama.RiskLevel, new[] { "important_runtime", "ai_engine" });

        var comfy = apps.First(a => a.Id == "app_3");
        Assert.Contains(comfy.Category, new[] { "ai_compute", "ai_workstation" });
        Assert.Contains(comfy.RiskLevel, new[] { "ai_engine", "important_runtime", "regular_app" });

        var baidu = apps.First(a => a.Id == "app_5");
        Assert.False(baidu.IsProtected);
    }

    [Fact]
    public void TestDependencyDetectionAndBlocking()
    {
        var apps = new List<SoftwareAsset>
        {
            new() { Id = "py_id", DisplayName = "Python 3.12.10 (64-bit)", IsProtected = true, AssetType = "runtime_environment" },
            new() { Id = "comfy_id", DisplayName = "ComfyUI Desktop", AssetType = "gui_app" },
            new() { Id = "ollama_id", DisplayName = "Ollama", IsProtected = true, AssetType = "runtime_environment" },
            new() { Id = "cherry_id", DisplayName = "Cherry Studio", AssetType = "gui_app" },
            new() { Id = "standalone_id", DisplayName = "百度网盘", AssetType = "gui_app", IsProtected = false }
        };

        var graph = _shield.BuildGraph(apps);
        Assert.NotEmpty(graph);

        // Verify ComfyUI -> Python link
        Assert.Contains(graph, l => l.UpstreamSoftwareId == "py_id" && l.DownstreamSoftwareId == "comfy_id");

        // Verify Cherry Studio -> Ollama link
        Assert.Contains(graph, l => l.UpstreamSoftwareId == "ollama_id" && l.DownstreamSoftwareId == "cherry_id");

        // 1. Attempting to uninstall Python MUST be blocked (protected + downstream dependent)
        var pythonSafety = _shield.EvaluateUninstallSafety("py_id", apps);
        Assert.False(pythonSafety.CanUninstall);
        Assert.True(pythonSafety.IsProtectedInfra);

        // 2. Attempting to uninstall Ollama MUST be blocked
        var ollamaSafety = _shield.EvaluateUninstallSafety("ollama_id", apps);
        Assert.False(ollamaSafety.CanUninstall);
        Assert.True(ollamaSafety.IsProtectedInfra);

        // 3. Standalone app with no dependents can be safely uninstalled
        var standaloneSafety = _shield.EvaluateUninstallSafety("standalone_id", apps);
        Assert.True(standaloneSafety.CanUninstall);
    }

    [Fact]
    public void TestCircularDependencyResilience()
    {
        // Construct circular graph: A -> B, B -> C, C -> A
        var circularLinks = new List<DependencyLink>
        {
            new() { UpstreamSoftwareId = "A", DownstreamSoftwareId = "B" },
            new() { UpstreamSoftwareId = "B", DownstreamSoftwareId = "C" },
            new() { UpstreamSoftwareId = "C", DownstreamSoftwareId = "A" } // Cycle
        };

        var resolved = DependencyShield.BreakCycles(circularLinks);

        // The cyclic link (C -> A) should be pruned, resulting in 2 links
        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, l => l.UpstreamSoftwareId == "A" && l.DownstreamSoftwareId == "B");
        Assert.Contains(resolved, l => l.UpstreamSoftwareId == "B" && l.DownstreamSoftwareId == "C");
        Assert.DoesNotContain(resolved, l => l.UpstreamSoftwareId == "C" && l.DownstreamSoftwareId == "A");
    }
}
