// OAuth callback parsing tests: the loopback sign-in listener parses the
// browser redirect request line ("/callback?code=...&state=...") with
// AuthService.TryGetQueryParam, and PKCE uses Base64UrlEncode.

using TrelloExtension.Auth;
using Xunit;

namespace TrelloExtension.Tests;

public class OAuthCallbackParsingTests
{
    [Fact]
    public void TryGetQueryParam_ExtractsCodeAndState()
    {
        var path = "/callback?code=abc123&state=xyz789";

        Assert.True(AuthService.TryGetQueryParam(path, "code", out var code));
        Assert.True(AuthService.TryGetQueryParam(path, "state", out var state));
        Assert.Equal("abc123", code);
        Assert.Equal("xyz789", state);
    }

    [Fact]
    public void TryGetQueryParam_MissingParameter_ReturnsFalse()
    {
        Assert.False(AuthService.TryGetQueryParam("/callback?code=abc", "state", out var state));
        Assert.Null(state);
    }

    [Fact]
    public void TryGetQueryParam_NoQuery_ReturnsFalse()
    {
        Assert.False(AuthService.TryGetQueryParam("/callback", "code", out var code));
        Assert.Null(code);
    }

    [Fact]
    public void TryGetQueryParam_UrlDecodesValue()
    {
        Assert.True(AuthService.TryGetQueryParam("/cb?code=%2Ffoo%2Bbar", "code", out var code));
        Assert.Equal("/foo+bar", code);
    }

    [Fact]
    public void TryGetQueryParam_PairWithoutEquals_IsSkipped()
    {
        var path = "/cb?flag&code=abc";
        Assert.True(AuthService.TryGetQueryParam(path, "code", out var code));
        Assert.Equal("abc", code);
    }

    [Fact]
    public void TryGetQueryParam_FirstOccurrenceWins()
    {
        Assert.True(AuthService.TryGetQueryParam("/cb?code=one&code=two", "code", out var code));
        Assert.Equal("one", code);
    }

    [Fact]
    public void Base64UrlEncode_NoPaddingOrUnsafeCharacters()
    {
        // 0xFB, 0xFF, 0xBF maps to base64 "+/+" (chars 62,63,62,63) - the
        // URL-unsafe form that must be translated to "-_-" + trailing "_"
        // and never padded.
        var encoded = AuthService.Base64UrlEncode([0xFB, 0xFF, 0xBF]);

        Assert.Equal("-_-_", encoded);
        Assert.Equal(4, encoded.Length);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
    }
}
