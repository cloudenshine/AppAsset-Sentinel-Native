using AppAssetSentinel.Core.Migration;
using AppAssetSentinel.Core.Scanner;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// Environment capability probes for tests that need real NTFS reparse-point support.
/// AUDIT A27: a test that could not actually run must never look like it passed. xUnit v2
/// has no dynamic skip, so an unavailable capability is reported as an explicit failure
/// with the reason, which is the honest outcome the audit asks for.
/// </summary>
internal static class TestEnvironment
{
    private static readonly Lazy<bool> JunctionSupport = new(ProbeJunctionSupport, isThreadSafe: true);

    public static bool SupportsJunctions => JunctionSupport.Value;

    public static void RequireJunctionSupport()
    {
        if (SupportsJunctions)
        {
            return;
        }

        Assert.Fail(
            "环境能力不足：当前卷或账户无法创建 NTFS 目录联接（重解析点）。" +
            "本用例需要该能力才能验证，未执行不等同于通过。");
    }

    private static bool ProbeJunctionSupport()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sentinel_cap_{Guid.NewGuid():N}");
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");

        try
        {
            Directory.CreateDirectory(target);
            if (!JunctionEngine.CreateJunction(link, target, out _))
            {
                return false;
            }

            bool recognised = FastDirectorySizer.IsReparsePoint(link);
            JunctionEngine.RemoveJunction(link, out _);
            return recognised;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(link) && FastDirectorySizer.IsReparsePoint(link))
                {
                    JunctionEngine.RemoveJunction(link, out _);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch { }
        }
    }
}
