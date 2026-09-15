using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W05 acceptance: "内嵌 Python 与外部指定 Python 的测试分开".
///
/// The safety-relevant consequence is asserted, not just the classification: a runtime the
/// application does not own must block destructive automation, because removing a shared Python
/// affects software other than the one being examined.
/// </summary>
public class TestPythonRuntimeOwnership : IDisposable
{
    private readonly string _root;

    public TestPythonRuntimeOwnership()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SentinelPy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            for (int i = 0; i < 3 && Directory.Exists(_root); i++)
            {
                try { Directory.Delete(_root, true); }
                catch { System.Threading.Thread.Sleep(30); }
            }
        }
        catch { }
    }

    private string MakeApp(string name, Action<string> populate)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        populate(dir);
        return dir;
    }

    [Fact]
    public void AnApplicationShippingItsOwnRuntimeIsClassifiedAsEmbeddedAndOwned()
    {
        string dir = MakeApp("embedded", d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "python"));
            File.WriteAllText(Path.Combine(d, "python", "python.exe"), "runtime");
            File.WriteAllText(Path.Combine(d, "app.exe"), "binary");
        });

        var finding = PythonRuntimeDetector.Detect(dir, Path.Combine(dir, "app.exe"), "MyApp");

        Assert.Equal(PythonRuntimeRelationship.Embedded, finding.Relationship);
        Assert.True(finding.RuntimeOwnedByApplication);
        Assert.False(finding.BlocksDestructiveAutomation);
        Assert.NotEmpty(finding.Evidence);
    }

    [Fact]
    public void AScriptWithoutAShippedRuntimeIsClassifiedAsExternalAndNotOwned()
    {
        string dir = MakeApp("external", d =>
        {
            File.WriteAllText(Path.Combine(d, "tool.py"), "print('hi')");
        });

        var finding = PythonRuntimeDetector.Detect(dir, Path.Combine(dir, "tool.py"), "SomeTool");

        Assert.Equal(PythonRuntimeRelationship.ExternalShared, finding.Relationship);

        // The decisive part: the application does not own this runtime.
        Assert.False(finding.RuntimeOwnedByApplication);

        // And that must block destructive automation.
        Assert.True(finding.BlocksDestructiveAutomation,
            "依赖外部共享 Python 时，不得授权破坏性自动化");
    }

    [Fact]
    public void AnUnreadableInstallDirectoryYieldsUnknownRatherThanNone()
    {
        string missing = Path.Combine(_root, "gone");

        var finding = PythonRuntimeDetector.Detect(missing, Path.Combine(missing, "tool.py"), "GhostTool");

        // Absence of a readable directory is not evidence of absence of a runtime.
        Assert.Equal(PythonRuntimeRelationship.Unknown, finding.Relationship);
        Assert.NotEqual(PythonRuntimeRelationship.None, finding.Relationship);
        Assert.False(finding.RuntimeOwnedByApplication);
        Assert.True(finding.BlocksDestructiveAutomation);
    }

    [Fact]
    public void AnApplicationWithoutAnyPythonSignalIsNoneAndBlocksNothing()
    {
        string dir = MakeApp("plain", d => File.WriteAllText(Path.Combine(d, "app.exe"), "binary"));

        var finding = PythonRuntimeDetector.Detect(dir, Path.Combine(dir, "app.exe"), "PlainApp");

        Assert.Equal(PythonRuntimeRelationship.None, finding.Relationship);
        Assert.False(finding.BlocksDestructiveAutomation);
    }

    [Fact]
    public void AnEmbeddedPackageTreeAlsoCountsAsOwned()
    {
        string dir = MakeApp("packaged", d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "Lib", "site-packages"));
            File.WriteAllText(Path.Combine(d, "launcher.exe"), "binary");
        });

        var finding = PythonRuntimeDetector.Detect(dir, Path.Combine(dir, "launcher.exe"), "Packaged");

        Assert.Equal(PythonRuntimeRelationship.Embedded, finding.Relationship);
        Assert.True(finding.RuntimeOwnedByApplication);
    }

    [Fact]
    public void PythonInTheDisplayNameAloneIsNotEnoughToCallTheRuntimeOwned()
    {
        // A name mentioning Python with no readable directory tells us something is there, but
        // nothing about who owns it.
        var finding = PythonRuntimeDetector.Detect(string.Empty, string.Empty, "Python Toolbox");

        Assert.NotEqual(PythonRuntimeRelationship.Embedded, finding.Relationship);
        Assert.False(finding.RuntimeOwnedByApplication);
    }

    [Fact]
    public void TheTwoCasesAreNeverCollapsedIntoOneVerdict()
    {
        string embedded = MakeApp("cmp-embedded", d =>
        {
            File.WriteAllText(Path.Combine(d, "python.exe"), "runtime");
        });
        string external = MakeApp("cmp-external", d =>
        {
            File.WriteAllText(Path.Combine(d, "tool.py"), "print()");
        });

        var a = PythonRuntimeDetector.Detect(embedded, string.Empty, "A");
        var b = PythonRuntimeDetector.Detect(external, string.Empty, "B");

        Assert.NotEqual(a.Relationship, b.Relationship);
        Assert.NotEqual(a.RuntimeOwnedByApplication, b.RuntimeOwnedByApplication);
        Assert.NotEqual(a.BlocksDestructiveAutomation, b.BlocksDestructiveAutomation);
    }
}