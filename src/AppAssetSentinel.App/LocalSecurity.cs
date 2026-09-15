using AppAssetSentinel.Core.Security;
using Microsoft.AspNetCore.Http;

namespace AppAssetSentinel.App;

/// <summary>
/// ASP.NET-facing adapter over <see cref="LocalRequestGuard"/>. The pure rules live in Core
/// so they can be unit-tested without a web host (AUDIT W03).
/// </summary>
public static class LocalSecurity
{
    public const string SessionHeader = LocalRequestGuard.SessionHeader;

    private static readonly string SessionToken = LocalRequestGuard.NewSessionToken();

    public static string Token => SessionToken;

    public static bool IsAcceptableOrigin(string? origin, int port) =>
        LocalRequestGuard.IsAcceptableOrigin(origin, port);

    public static bool IsAcceptableHost(string? host, int port) =>
        LocalRequestGuard.IsAcceptableHost(host, port);

    public static bool IsTokenValid(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(SessionHeader, out var provided) || provided.Count == 0)
        {
            return false;
        }

        return LocalRequestGuard.IsTokenValid(provided[0], SessionToken);
    }
}
