using System.Security.Cryptography;
using System.Text;

namespace AppAssetSentinel.Core.Security;

/// <summary>
/// AUDIT A08: the local HTTP API had no origin check and no session identity, so any page
/// open in the user's browser could attempt to drive write endpoints. These are the pure,
/// testable rules behind that gate. They are defence in depth for a loopback-only service
/// and are not a substitute for authentication on a networked deployment.
/// </summary>
public static class LocalRequestGuard
{
    public const string SessionHeader = "X-Sentinel-Session";

    /// <summary>Issues a fresh, unpredictable per-process session token.</summary>
    public static string NewSessionToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Origins accepted for state-changing requests.</summary>
    public static bool IsAcceptableOrigin(string? origin, int port)
    {
        // A same-origin POST may omit Origin; a forged cross-site one cannot hide it.
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hosts accepted, which also blocks DNS-rebinding style names.</summary>
    public static bool IsAcceptableHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fixed-time token comparison keeps the secret out of timing side channels.</summary>
    public static bool IsTokenValid(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(provided);
        byte[] b = Encoding.UTF8.GetBytes(expected);

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
