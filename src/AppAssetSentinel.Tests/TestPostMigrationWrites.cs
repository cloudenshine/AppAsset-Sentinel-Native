using AppAssetSentinel.Core.Abstractions;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W08 acceptance, two clauses that were not previously covered:
///
///   "受控写入落在目标"                    - after the switch, new writes must land at the target
///   "卸载/重装应用壳的测试不丢资产"        - the data must not live under the application shell
///
/// Both are verified structurally rather than by asserting a claim, and the configuration is
/// exercised through an injected environment store so the real user setting is never touched.
/// </summary>
public class TestPostMigrationWrites : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestPostMigrationWrites()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelPostMig_{Guid.NewGuid():N}");
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

                for (int i = 0; i < 3 && Directory.Exists(_root); i++)
                {
                    try { Directory.Delete(_root, true); }
                    catch { System.Threading.Thread.Sleep(30); }
                }
            }
        }
        catch { }
    }

    private (string appShell, string dataPath, string vaultTarget) BuildOllamaLikeLayout()
    {
        // Mimics the real shape: the model store lives INSIDE the application shell directory.
        string appShell = Path.Combine(_root, "app", "Programs", "Ollama");
        string dataPath = Path.Combine(appShell, "models");
        Directory.CreateDirectory(Path.Combine(dataPath, "blobs"));
        Directory.CreateDirectory(Path.Combine(dataPath, "manifests"));

        File.WriteAllText(Path.Combine(dataPath, "blobs", "sha256-abc"), "WEIGHTS");
        File.WriteAllText(Path.Combine(dataPath, "manifests", "latest"), "{\"schemaVersion\":2}");
        File.WriteAllText(Path.Combine(appShell, "ollama.exe"), "binary");

        return (appShell, dataPath, Path.Combine(_root, "vault", "AIStack", "models", "ollama"));
    }

    [Fact]
    public void AfterAConfigSwitchNewWritesLandAtTheTargetNotTheOldLocation()
    {
        var (appShell, dataPath, target) = BuildOllamaLikeLayout();

        var env = new InMemoryEnvironmentStore();
        env.Set(OllamaAdapter.ConfigurationVariable, dataPath, EnvironmentScope.User);

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = dataPath,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = OllamaAdapter.ConfigurationVariable,
            EnvironmentStore = env,
            HealthCheck = () => env.Get(OllamaAdapter.ConfigurationVariable, EnvironmentScope.User) == target
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        // "受控写入落在目标": whatever the application writes to the configured path must land in
        // the migrated store. Resolve the configured location and write through it.
        string configured = env.Get(OllamaAdapter.ConfigurationVariable, EnvironmentScope.User)!;
        Assert.Equal(target, configured);

        Directory.CreateDirectory(Path.Combine(configured, "blobs"));
        File.WriteAllText(Path.Combine(configured, "blobs", "sha256-new"), "NEW-PULL");

        Assert.True(File.Exists(Path.Combine(target, "blobs", "sha256-new")),
            "迁移后新写入必须落在目标位置");

        // And the pre-existing content is still there beside it.
        Assert.Equal("WEIGHTS", File.ReadAllText(Path.Combine(target, "blobs", "sha256-abc")));
    }

    [Fact]
    public void TheMigratedStoreSitsOutsideTheApplicationShellSoReinstallingIsSafe()
    {
        var (appShell, dataPath, target) = BuildOllamaLikeLayout();

        var env = new InMemoryEnvironmentStore();
        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = dataPath,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = OllamaAdapter.ConfigurationVariable,
            EnvironmentStore = env
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        // "卸载/重装应用壳不丢资产": the store must not be under the shell directory any more.
        string shellFull = Path.GetFullPath(appShell).TrimEnd('\\');
        string targetFull = Path.GetFullPath(target).TrimEnd('\\');

        Assert.False(targetFull.StartsWith(shellFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "迁移后的数据不应位于应用外壳目录之下，否则重装应用会丢资产");

        // The retained backup sits beside the old location, which is inside the shell. Record
        // that plainly: uninstalling the application forfeits rollback ability even though the
        // assets themselves are safe. Verified rather than assumed.
        string backup = result.Record!.SourceBackupPath;
        Assert.True(Directory.Exists(backup));
        Assert.StartsWith(shellFull, Path.GetFullPath(backup).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        // Simulate uninstalling the shell: remove it.
        Directory.Delete(appShell, true);
        Assert.False(Directory.Exists(appShell));

        // The requirement is that the ASSETS survive, and they do: they are outside the shell.
        Assert.True(File.Exists(Path.Combine(target, "blobs", "sha256-abc")));
        Assert.True(File.Exists(Path.Combine(target, "manifests", "latest")));

        // But the rollback copy did not survive, and the test states that rather than hiding it.
        Assert.False(Directory.Exists(backup),
            "源备份位于应用外壳内，卸载外壳会一并失去回退能力；此处记录该限制而非掩盖");
    }

    [Fact]
    public void TheJunctionVariantAlsoKeepsNewWritesInsideTheVault()
    {
        TestEnvironment.RequireJunctionSupport();

        var (_, dataPath, target) = BuildOllamaLikeLayout();

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = dataPath,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models"
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);

        // Writing through the anchor must reach the vault, because the anchor is a link.
        File.WriteAllText(Path.Combine(dataPath, "blobs", "sha256-new"), "WRITTEN-VIA-ANCHOR");

        Assert.True(File.Exists(Path.Combine(target, "blobs", "sha256-new")),
            "经锚点写入应落到目标位置");
    }
}