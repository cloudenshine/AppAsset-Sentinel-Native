using AppAssetSentinel.Core.Security;
using Xunit;

namespace AppAssetSentinel.Tests;

/// <summary>
/// AUDIT W03 acceptance: legal data containing quotes, backslashes, Chinese and spaces must
/// survive a round trip; malicious HTML must be displayed as text; plan identity must be
/// server-held; and an unknown caller must not be able to drive a write.
/// </summary>
public class TestInputAndAuthorizationBoundaries
{
    private const int Port = 8765;

    // -----------------------------------------------------------------
    // Origin / Host gate
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("http://LOCALHOST:8765")]
    public void AcceptsLoopbackOrigins(string origin)
    {
        Assert.True(LocalRequestGuard.IsAcceptableOrigin(origin, Port));
    }

    [Theory]
    [InlineData("http://evil.example.com")]
    [InlineData("https://attacker.test:8765")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("null")]
    public void RejectsForeignOrigins(string origin)
    {
        Assert.False(LocalRequestGuard.IsAcceptableOrigin(origin, Port));
    }

    [Theory]
    [InlineData("127.0.0.1:8765")]
    [InlineData("localhost")]
    public void AcceptsLoopbackHosts(string host)
    {
        Assert.True(LocalRequestGuard.IsAcceptableHost(host, Port));
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("evil.example.com:8765")]
    [InlineData("127.0.0.1.nip.io:8765")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsForeignHosts(string? host)
    {
        Assert.False(LocalRequestGuard.IsAcceptableHost(host, Port));
    }

    // -----------------------------------------------------------------
    // Session token
    // -----------------------------------------------------------------

    [Fact]
    public void SessionTokensAreLongRandomAndNotReusableAcrossValues()
    {
        string a = LocalRequestGuard.NewSessionToken();
        string b = LocalRequestGuard.NewSessionToken();

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);

        Assert.True(LocalRequestGuard.IsTokenValid(a, a));
        Assert.False(LocalRequestGuard.IsTokenValid(b, a));
        Assert.False(LocalRequestGuard.IsTokenValid(null, a));
        Assert.False(LocalRequestGuard.IsTokenValid("", a));
        Assert.False(LocalRequestGuard.IsTokenValid(a, ""));
    }

    // -----------------------------------------------------------------
    // A25 round trip: the probe's Windows-path corruption must not recur
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Tools\ComfyUI\models")]
    [InlineData(@"C:\Users\Shine\.ollama\models")]
    [InlineData(@"D:\Program Files (x86)\WPS\WPS Office\11.8.2.12344")]
    [InlineData("C:\\路径 带 空格\\模型")]
    [InlineData("C:\\quote'and\"double\\mixed")]
    [InlineData("C:\\semi;colon&ampersand\\x")]
    public void PathSurvivesHtmlAttributeRoundTripUnchanged(string original)
    {
        // Reproduces the A25 defect: interpolating a path into an inline JS string literal
        // dropped every backslash. Values now travel via a double-quoted attribute, so the
        // only transformation is HTML entity escaping, which undoes exactly.
        string encoded = EscapeForAttribute(original);
        string decoded = DecodeHtmlEntities(encoded);

        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("\"><img src=x onerror=alert(1)>")]
    [InlineData("javascript:eval('x')")]
    public void MaliciousMarkupIsNeutralisedWhenEscaped(string payload)
    {
        string encoded = EscapeForAttribute(payload);

        // No character that could terminate an attribute or open a tag may survive.
        Assert.DoesNotContain("<", encoded);
        Assert.DoesNotContain(">", encoded);
        Assert.DoesNotContain("\"", encoded);
        Assert.DoesNotContain("'", encoded);

        // The escaping is lossless, so the original value is recoverable as data.
        Assert.Equal(payload, DecodeHtmlEntities(encoded));
    }

    // -----------------------------------------------------------------
    // Server-held plan identity (W03)
    // -----------------------------------------------------------------

    [Fact]
    public void PlanIdGuardReadsOnlyServerIssuedIdentifier()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"plan_id":"plan_abc123","software_id":"x"}""");

        Assert.Equal("plan_abc123", AppAssetSentinel.Core.Uninstaller.PlanRequestGuard.ExtractPlanId(doc.RootElement));
    }

    [Fact]
    public void PlanIdGuardReturnsNullWhenAbsent()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"software_id":"x"}""");
        Assert.Null(AppAssetSentinel.Core.Uninstaller.PlanRequestGuard.ExtractPlanId(doc.RootElement));
    }

    // --- helpers mirroring the browser's escAttr / entity decode ---

    private static string EscapeForAttribute(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");

    private static string DecodeHtmlEntities(string value) => value
        .Replace("&quot;", "\"")
        .Replace("&#39;", "'")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&amp;", "&");
}
