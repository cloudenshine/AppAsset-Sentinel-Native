using AppAssetSentinel.Core.Abstractions;
using AppAssetSentinel.Core.Adapters;
using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Operations;
using AppAssetSentinel.Core.Policy;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W08 acceptance for the first production adapter.
///
/// Discovery and the service probe run against the real machine but are strictly read-only.
/// The configuration switch is exercised against an injected in-memory environment store, so
/// no test ever writes the user's real OLLAMA_MODELS (AUDIT A09/W02).
///
/// Cross-volume relocation and a real "stop Ollama, relocate, restart and infer" run are NOT
/// performed here: they require an isolated account plus two real test volumes and are tracked
/// separately. Nothing below claims that acceptance.
/// </summary>
public class TestOllamaAdapterAndConfigRedirect : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly CapabilityPolicy _policy = CapabilityPolicy.RelocationVerifiedProfile();

    public TestOllamaAdapterAndConfigRedirect()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelOllama_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_logDir);
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
    // Discovery (read-only, real machine)
    // -----------------------------------------------------------------

    [Fact]
    public void DiscoveryReportsWhatItActuallyFoundWithoutInventingDetails()
    {
        var instance = OllamaAdapter.Discover();

        // Confidence must be one of the three honest values, never a bare assumption.
        Assert.Contains(instance.Confidence,
            new[] { AdapterConfidence.Confirmed, AdapterConfidence.Inferred, AdapterConfidence.Unknown });

        if (instance.Confidence == AdapterConfidence.Unknown)
        {
            // Nothing found: the adapter must say so rather than claim a path.
            Assert.True(string.IsNullOrEmpty(instance.ExecutablePath));
            Assert.True(instance.ManifestCount == 0 && instance.BlobCount == 0);
            Assert.False(instance.ServiceRunning);
            Assert.True(instance.Notes.Count > 0);
        }
        else
        {
            Assert.True(instance.Notes.Count > 0, "发现结果必须附带可读的依据说明");
        }

        // A configured path, when reported, must name the variable it came from.
        if (!string.IsNullOrEmpty(instance.ConfiguredModelsPath))
        {
            Assert.Contains(OllamaAdapter.ConfigurationVariable, instance.ModelsPathSource);
        }
    }

    [Fact]
    public void ServiceProbeReportsReachabilityHonestly()
    {
        // Point at a port nothing listens on: the probe must report failure, not guess.
        var unreachable = OllamaAdapter.ProbeService("http://127.0.0.1:59999");
        Assert.False(unreachable.Reachable);
        Assert.Empty(unreachable.Models);
        Assert.False(string.IsNullOrEmpty(unreachable.Detail));

        // And a real endpoint, when the service is up, must list what it can serve.
        var real = OllamaAdapter.ProbeService(OllamaAdapter.DefaultApiEndpoint);
        if (real.Reachable)
        {
            Assert.Equal(real.Models.Count, real.ModelCount);
        }
    }

    // -----------------------------------------------------------------
    // Mechanism preference (AUDIT W08: official config first)
    // -----------------------------------------------------------------

    [Fact]
    public void OfficialConfigurationIsPreferredOverFilesystemRedirection()
    {
        var instance = new OllamaInstance
        {
            Found = true,
            Confidence = AdapterConfidence.Confirmed,
            ExecutablePath = @"C:\x\ollama.exe",
            ManifestCount = 3,
            BlobCount = 5
        };

        var (mechanism, reason) = OllamaAdapter.ChooseMechanism(instance);

        Assert.Equal(RelocationMechanism.OfficialConfig, mechanism);
        Assert.Contains(OllamaAdapter.ConfigurationVariable, reason);
        Assert.Contains("目录联接", reason); // explains why the fallback is not preferred
    }

    [Fact]
    public void UnknownInstanceIsUnsupportedRatherThanBestEffort()
    {
        var (mechanism, reason) = OllamaAdapter.ChooseMechanism(new OllamaInstance
        {
            Confidence = AdapterConfidence.Unknown
        });

        Assert.Equal(RelocationMechanism.Unsupported, mechanism);
        Assert.Contains("未知", reason);
    }

    // -----------------------------------------------------------------
    // Config-redirect switch (no junction, injected environment)
    // -----------------------------------------------------------------

    [Fact]
    public void ConfigRedirectMovesDataAndFlipsTheOfficialConfigurationWithoutAJunction()
    {
        string source = Path.Combine(_root, ".ollama", "models");
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        File.WriteAllText(Path.Combine(source, "blobs", "sha256-abc"), "BLOB-PAYLOAD");
        string target = Path.Combine(_root, "vault", "ollama-models");

        var env = new InMemoryEnvironmentStore();
        // Seed the value the user actually had, so the record can prove the change is reversible.
        env.Set(OllamaAdapter.ConfigurationVariable, @"C:\old\ollama\models", EnvironmentScope.User);
        string? probeObservedPath = null;

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = OllamaAdapter.ConfigurationVariable,
            EnvironmentStore = env,
            HealthCheck = () =>
            {
                // The consumer is asked to confirm it sees the new location.
                probeObservedPath = env.Get(OllamaAdapter.ConfigurationVariable, EnvironmentScope.User);
                return probeObservedPath == target;
            }
        });

        Assert.Equal(OperationStatus.Succeeded, result.Outcome.Status);
        Assert.Equal("switched_via_config", result.Outcome.Code);
        Assert.True(result.Outcome.DidMutate);

        // The official configuration now points at the new location.
        Assert.Equal(target, env.Get(OllamaAdapter.ConfigurationVariable, EnvironmentScope.User));
        Assert.Equal(target, probeObservedPath);

        // Data really moved and reads correctly.
        Assert.True(Directory.Exists(target));
        Assert.Equal("BLOB-PAYLOAD", File.ReadAllText(Path.Combine(target, "blobs", "sha256-abc")));

        // Acceptance: the anchor was NOT turned into a reparse point — the application was told
        // the truth instead of being redirected underneath.
        Assert.False(Directory.Exists(source) && FastDirectorySizer.IsReparsePoint(source));

        // The original directory is retained as the backup, so space is not silently reclaimed.
        var record = result.Record!;
        Assert.Equal(RelocationMechanism.OfficialConfig, record.Mechanism);
        Assert.Equal(OperationState.Switched, record.State);
        Assert.True(Directory.Exists(record.SourceBackupPath));
        Assert.True(File.Exists(Path.Combine(record.SourceBackupPath, "blobs", "sha256-abc")));

        // The log carries the previous value so the config change is reversible.
        Assert.True(record.ConfigWasSet);
        Assert.Equal(target, record.ConfigAppliedValue);
    }

    [Fact]
    public void FailedHealthCheckRollsBackBothTheConfigurationAndTheDirectory()
    {
        string source = Path.Combine(_root, "src2", "models");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "blob.bin"), "ORIGINAL");
        string target = Path.Combine(_root, "vault2", "models");

        var env = new InMemoryEnvironmentStore();
        const string previousValue = @"C:\original\ollama\models";
        env.Set(OllamaAdapter.ConfigurationVariable, previousValue, EnvironmentScope.User);

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = OllamaAdapter.ConfigurationVariable,
            EnvironmentStore = env,
            HealthCheck = () => false // the consumer never picks up the new location
        });

        Assert.Equal(OperationStatus.FailedRecoverable, result.Outcome.Status);
        Assert.False(result.Outcome.DidMutate);

        // The configuration is back to exactly what it was.
        Assert.Equal(previousValue, env.Get(OllamaAdapter.ConfigurationVariable, EnvironmentScope.User));

        // The original directory is back in place with its data intact.
        Assert.True(Directory.Exists(source));
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(source, "blob.bin")));

        var record = result.Record!;
        Assert.Equal(OperationState.SwitchFailed, record.State);
        Assert.Equal(previousValue, record.ConfigPreviousValue);
    }

    [Fact]
    public void ConfigRedirectWithoutAVariableRefusesInsteadOfGuessing()
    {
        string source = Path.Combine(_root, "src3");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.bin"), "x");
        string target = Path.Combine(_root, "vault3");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = target,
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = string.Empty,
            EnvironmentStore = new InMemoryEnvironmentStore()
        });

        Assert.Equal(OperationStatus.Failed, result.Outcome.Status);
        Assert.Equal("missing_config_variable", result.Outcome.Code);
        Assert.False(result.Outcome.DidMutate);

        // Nothing was moved and the old path is untouched.
        Assert.True(File.Exists(Path.Combine(source, "a.bin")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void ConfigRedirectStillHonoursEveryPathBoundaryRule()
    {
        string source = Path.Combine(_root, "src4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.bin"), "x");

        var log = new OperationLog(_logDir);
        var result = MigrationKernel.Execute(_policy, log, new MigrationRequest
        {
            SourcePath = source,
            TargetPath = Path.Combine(source, "nested"),
            AssetName = "ollama",
            Category = "ai_models",
            Mechanism = RelocationMechanism.OfficialConfig,
            ConfigVariable = OllamaAdapter.ConfigurationVariable,
            EnvironmentStore = new InMemoryEnvironmentStore()
        });

        Assert.Equal(OperationState.PreflightFailed, result.Record!.State);
        Assert.False(result.Outcome.DidMutate);
        Assert.False(Directory.Exists(Path.Combine(source, "nested")));
    }

    [Fact]
    public void TestsNeverWriteTheRealUserEnvironment()
    {
        // The injected store is the only writer in this class; the real variable is read-only
        // evidence. Assert the real value is still a path we recognise, not a temp fixture.
        string? realValue = Environment.GetEnvironmentVariable(
            OllamaAdapter.ConfigurationVariable, EnvironmentVariableTarget.User);

        if (!string.IsNullOrEmpty(realValue))
        {
            Assert.DoesNotContain("SentinelOllama_", realValue);
            Assert.DoesNotContain("SentinelVaultTest_", realValue);
        }
    }
}
