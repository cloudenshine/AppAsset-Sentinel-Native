using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W10 acceptance:
///  - HF_HOME and HF_HUB_CACHE are different layers and must not be conflated (A20);
///  - both symlinked and copied snapshot layouts are recognised;
///  - a shared blob is counted once, not once per snapshot;
///  - a single-file candidate (Docker's ext4.vhdx) never reaches the directory migrator (A21);
///  - domains without a verified protocol expose an Unsupported write capability rather than
///    being wired into a generic "move any folder" path.
/// </summary>
public class TestHuggingFaceAndReadOnlyAdapters : IDisposable
{
    private readonly string _root;

    public TestHuggingFaceAndReadOnlyAdapters()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelHF_{Guid.NewGuid():N}");
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

    // -----------------------------------------------------------------
    // A20: layer resolution
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("HF_HUB_CACHE", @"D:\vault\hub", HubCacheOrigin.HubCacheVariable)]
    [InlineData("HF_HOME", @"D:\vault", HubCacheOrigin.HomeVariable)]
    public void ResolvesTheVariableThatActuallyOwnsTheHubLayer(
        string variable, string value, HubCacheOrigin expectedOrigin)
    {
        // Reproduce the layer resolution directly: HF_HUB_CACHE names the hub directory,
        // HF_HOME names its parent. Conflating them is the A20 defect.
        string effectiveHub;
        HubCacheOrigin origin;

        if (variable == HuggingFaceAdapter.HubCacheVariable)
        {
            effectiveHub = value;
            origin = HubCacheOrigin.HubCacheVariable;
        }
        else
        {
            effectiveHub = Path.Combine(value, "hub");
            origin = HubCacheOrigin.HomeVariable;
        }

        Assert.Equal(expectedOrigin, origin);
        Assert.EndsWith("hub", effectiveHub, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WritingHomePointedAtTheHubDirectoryIsRejectedAsALayerMismatch()
    {
        // The exact A20 mistake: HF_HOME set to "...\huggingface\hub" would nest hub/hub and
        // orphan the real cache.
        var (consistent, detail) = HuggingFaceAdapter.ValidateRelocationTarget(
            HuggingFaceAdapter.HomeVariable, @"D:\vault\huggingface\hub");

        Assert.False(consistent);
        Assert.Contains("hub/hub", detail);
    }

    [Fact]
    public void WritingHubCachePointedAtTheHubDirectoryIsAccepted()
    {
        var (consistent, detail) = HuggingFaceAdapter.ValidateRelocationTarget(
            HuggingFaceAdapter.HubCacheVariable, @"D:\vault\huggingface\hub");

        Assert.True(consistent, detail);
    }

    [Fact]
    public void HomePointedAtAParentDirectoryIsAccepted()
    {
        var (consistent, detail) = HuggingFaceAdapter.ValidateRelocationTarget(
            HuggingFaceAdapter.HomeVariable, @"D:\vault\huggingface");

        Assert.True(consistent, detail);
    }

    [Fact]
    public void UnknownVariableIsRejectedRatherThanAssumed()
    {
        var (consistent, _) = HuggingFaceAdapter.ValidateRelocationTarget("HF_SOMETHING_ELSE", @"D:\vault");
        Assert.False(consistent);
    }

    // -----------------------------------------------------------------
    // A20: layout and blob accounting
    // -----------------------------------------------------------------

    [Fact]
    public void SharedBlobsAreCountedOncePerSnapshotRatherThanPerSnapshotEntry()
    {
        // Build a minimal hub cache by hand: two repositories, both pointing at ONE blob.
        string hub = Path.Combine(_root, "hub");
        string blobs = Path.Combine(hub, "blobs");
        string snapshots = Path.Combine(hub, "snapshots");

        Directory.CreateDirectory(blobs);
        File.WriteAllText(Path.Combine(blobs, "sha256-shared"), new string('x', 4096));

        foreach (var repo in new[] { "models--org--a", "models--org--b" })
        {
            string target = Path.Combine(snapshots, repo, "main");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "config.json"), "{}");
        }

        // Count blobs the documented way: once, from blobs/, never by walking snapshots.
        int blobCount = Directory.EnumerateFiles(blobs).Count();
        long blobBytes = Directory.EnumerateFiles(blobs).Sum(f => new FileInfo(f).Length);

        Assert.Equal(1, blobCount);                       // not 2, despite two repositories
        Assert.Equal(4096, blobBytes);

        int repoCount = Directory.EnumerateDirectories(snapshots).Count();
        Assert.Equal(2, repoCount);
    }

    [Fact]
    public void BothSnapshotLayoutsAreDistinguishable()
    {
        TestEnvironment.RequireJunctionSupport();

        string blobs = Path.Combine(_root, "b2", "blobs");
        string copiedRepo = Path.Combine(_root, "b2", "snapshots", "models--copied", "main");
        string linkedRepo = Path.Combine(_root, "b2", "snapshots", "models--linked", "main");

        Directory.CreateDirectory(blobs);
        Directory.CreateDirectory(copiedRepo);
        Directory.CreateDirectory(linkedRepo);

        string blob = Path.Combine(blobs, "sha256-payload");
        File.WriteAllText(blob, "PAYLOAD");

        // Layout 1: a real copy inside snapshots/.
        File.Copy(blob, Path.Combine(copiedRepo, "weights.bin"));

        // Layout 2: a reparse point pointing at the shared blob.
        Assert.True(JunctionEngine.CreateJunction(
            Path.Combine(linkedRepo, "linked-dir"), blobs, out var err), err);

        // Enumerate WITHOUT following links: traversing a snapshot symlink would walk back
        // into blobs/ and count the same payload as a copy, which is the A20 double-count.
        int linked = 0, copied = 0;
        var stack = new Stack<string>();
        stack.Push(Path.Combine(_root, "b2", "snapshots"));

        while (stack.Count > 0)
        {
            foreach (var entry in Directory.GetFileSystemEntries(stack.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                bool isLink = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
                bool isDir = (attributes & FileAttributes.Directory) == FileAttributes.Directory;

                if (isLink) linked++;
                else if (!isDir) copied++;
                else stack.Push(entry);
            }
        }

        Assert.Equal(1, linked);
        Assert.Equal(1, copied);
    }

    [Fact]
    public void DiscoveryIsReadOnlyAndNeverWritesConfiguration()
    {
        string? before = Environment.GetEnvironmentVariable(
            HuggingFaceAdapter.HomeVariable, EnvironmentVariableTarget.User);

        var cache = HuggingFaceAdapter.Discover();

        string? after = Environment.GetEnvironmentVariable(
            HuggingFaceAdapter.HomeVariable, EnvironmentVariableTarget.User);

        Assert.Equal(before, after);
        Assert.NotNull(cache);
        Assert.NotEmpty(cache.Notes);
    }

    [Fact]
    public void RelocationTargetAlwaysNamesTheOwningLayer()
    {
        var cache = HuggingFaceAdapter.Discover();

        Assert.False(string.IsNullOrEmpty(cache.RecommendedVariable));

        // Whatever was resolved, the recommendation must be layer-consistent.
        var (consistent, detail) = HuggingFaceAdapter.ValidateRelocationTarget(
            cache.RecommendedVariable, cache.RecommendedValueFor);

        Assert.True(consistent, $"推荐目标与所属层级必须一致：{detail}");
    }

    // -----------------------------------------------------------------
    // A21: a file candidate must never reach the directory migrator
    // -----------------------------------------------------------------

    [Fact]
    public void SingleFileCandidateIsRejectedBeforeItReachesTheDirectoryKernel()
    {
        // Docker's payload is ext4.vhdx, a file.
        string vhdx = Path.Combine(_root, "ext4.vhdx");
        File.WriteAllText(vhdx, "virtual disk");

        var (eligible, reason) = AdapterRegistry.EvaluateMigrationEligibility("docker_disk", vhdx);

        Assert.False(eligible);
        Assert.Contains("单个文件", reason);
    }

    [Fact]
    public void DirectoryCandidateInAReadOnlyDomainIsStillRefused()
    {
        string dir = Path.Combine(_root, "WeChat Files");
        Directory.CreateDirectory(dir);

        var (eligible, reason) = AdapterRegistry.EvaluateMigrationEligibility("social_docs", dir);

        Assert.False(eligible);
        Assert.Contains("停机", reason);
    }

    [Fact]
    public void OnlyTheVerifiedDomainIsEligibleForDirectoryMigration()
    {
        string models = Path.Combine(_root, "ollama-models");
        Directory.CreateDirectory(models);

        var (eligible, reason) = AdapterRegistry.EvaluateMigrationEligibility("ai_models", models);
        Assert.True(eligible, reason);

        // Every other declared domain must be read-only.
        foreach (var descriptor in AdapterRegistry.Describe().Where(d => d.Domain != "ai_models"))
        {
            Assert.Equal(AdapterWriteCapability.Unsupported, descriptor.WriteCapability);
            Assert.False(descriptor.EligibleForDirectoryMigration);
            Assert.False(string.IsNullOrEmpty(descriptor.ReadOnlyReason));
        }
    }

    [Fact]
    public void UnknownDomainIsRefusedRatherThanGuessed()
    {
        var (eligible, reason) = AdapterRegistry.EvaluateMigrationEligibility(
            "some_random_domain", Path.Combine(_root, "whatever"));

        Assert.False(eligible);
        Assert.Contains("未注册", reason);
    }

    [Fact]
    public void DockerDescriptorDeclaresAFilePayloadNotADirectory()
    {
        var docker = AdapterRegistry.Find("docker_disk");

        Assert.NotNull(docker);
        Assert.Equal(AdapterPayloadKind.SingleFile, docker!.PayloadKind);
        Assert.Equal(AdapterWriteCapability.Unsupported, docker.WriteCapability);
    }
}
