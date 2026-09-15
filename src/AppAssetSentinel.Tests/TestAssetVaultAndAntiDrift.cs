using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

public class TestAssetVaultAndAntiDrift : IDisposable
{
    private readonly string _testRoot;

    public TestAssetVaultAndAntiDrift()
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

    [Fact]
    public void TestVolumeManagerDetectionAndRouting()
    {
        var volumes = VolumeManager.GetSystemVolumes();
        Assert.NotEmpty(volumes);

        // System drive should be identified
        Assert.Contains(volumes, v => v.IsSystem);

        // Routing recommendations for 4 domains
        string aiPath = VolumeManager.RecommendVaultPath("ai_model", "ollama");
        string docPath = VolumeManager.RecommendVaultPath("social_chat", "WeChat Files");
        string devPath = VolumeManager.RecommendVaultPath("dev_container", "docker_vhdx");
        string mediaPath = VolumeManager.RecommendVaultPath("media_project", "Jianying_Projects");

        Assert.False(string.IsNullOrEmpty(aiPath));
        Assert.False(string.IsNullOrEmpty(docPath));
        Assert.False(string.IsNullOrEmpty(devPath));
        Assert.False(string.IsNullOrEmpty(mediaPath));

        Assert.Contains("AIStack_Vault", aiPath);
        Assert.Contains("Documents_Vault", docPath);
        Assert.True(devPath.Contains("AIStack_Vault") || devPath.Contains("Works_Vault"));
        Assert.Contains("Videos_Vault", mediaPath);
    }

    [Fact]
    public async Task TestDualLockRelocationWorkflow()
    {
        // Source simulated on C: (e.g. C:\Temp\OllamaModels)
        var sourceDir = Path.Combine(_testRoot, "Simulated_C_Models");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "weights.safetensors"), "Large LLM Weights 70B");

        // Target simulated on Vault (e.g. D:\Vault\models\ollama)
        var vaultParent = Path.Combine(_testRoot, "VaultParent");
        Directory.CreateDirectory(vaultParent);
        var targetVault = Path.Combine(vaultParent, "ollama_vault");

        var task = await AssetVaultEngine.RelocateAndDualLockAsync(
            sourceDir,
            targetVault,
            assetName: "ollama",
            category: "ai_models"
        );

        Assert.Equal(AppAssetSentinel.Core.Models.MigrationStatus.Completed, task.Status);

        // 1. Check Lock 1: Source path is now an NTFS Junction
        var juncInfo = FastDirectorySizer.GetJunctionInfo(sourceDir);
        Assert.True(juncInfo.IsJunction);

        // 2. Check read transparency through Junction
        string readBack = File.ReadAllText(Path.Combine(sourceDir, "weights.safetensors"));
        Assert.Equal("Large LLM Weights 70B", readBack);

        // 3. Check Lock 2: Watchdog registration
        var activeRegs = AssetVaultEngine.GetActiveRegistrations();
        var reg = activeRegs.FirstOrDefault(r => r.VirtualAnchorPath.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(reg);
        Assert.True(reg.IsJunctionIntact);
        Assert.False(reg.DriftDetected);
    }

    [Fact]
    public void TestDriftWatchdogDetectionAndAutoHeal()
    {
        var anchorDir = Path.Combine(_testRoot, "AnchorDir");
        var vaultDir = Path.Combine(_testRoot, "VaultDir");
        Directory.CreateDirectory(vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "base_model.bin"), "Original Vault Data");

        // Setup pristine junction
        JunctionEngine.CreateJunction(anchorDir, vaultDir, out _);

        string regId = Guid.NewGuid().ToString("N")[..8];
        var regs = AssetVaultEngine.GetActiveRegistrations();
        regs.Add(new VaultRegistration
        {
            Id = regId,
            AssetName = "TestApp",
            VirtualAnchorPath = anchorDir,
            PhysicalVaultPath = vaultDir,
            IsJunctionIntact = true,
            DriftDetected = false
        });
        AssetVaultEngine.SaveRegistrations(regs);

        // SIMULATE DRIFT: A software update deletes the junction and creates a plain folder with new file!
        JunctionEngine.RemoveJunction(anchorDir, out _);
        Directory.CreateDirectory(anchorDir);
        File.WriteAllText(Path.Combine(anchorDir, "drifted_new_file.txt"), "Newly Drifted Content");

        // Watchdog audit must detect drift!
        var audited = DriftWatchdog.InspectAndAuditDrifts();
        var targetAudit = audited.First(r => r.Id == regId);
        Assert.True(targetAudit.DriftDetected);
        Assert.False(targetAudit.IsJunctionIntact);

        // Execute Auto-Heal!
        var (healed, msg) = DriftWatchdog.AutoHealDrift(regId);
        Assert.True(healed, $"AutoHeal failed: {msg}");

        // Verify:
        // 1. Anchor is once again an NTFS Junction
        Assert.True(FastDirectorySizer.IsReparsePoint(anchorDir));

        // 2. Drifted file was merged into vault
        Assert.True(File.Exists(Path.Combine(vaultDir, "drifted_new_file.txt")));
        Assert.Equal("Newly Drifted Content", File.ReadAllText(Path.Combine(vaultDir, "drifted_new_file.txt")));

        // 3. Watchdog marks it intact
        var postAudits = DriftWatchdog.InspectAndAuditDrifts();
        var postTarget = postAudits.First(r => r.Id == regId);
        Assert.False(postTarget.DriftDetected);
        Assert.True(postTarget.IsJunctionIntact);
    }
}
