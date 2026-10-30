using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// Response body from POST https://auth.atlassian.com/oauth/token
/// (both grant_type=authorization_code and grant_type=refresh_token).
///
/// Verified shape from the Trello OAuth 2.0 Confidential Client Usage docs,
/// Step 2 "Exchange authorization code for tokens" response table:
///   access_token, refresh_token, expires_in (seconds, default 3600), scope
/// </summary>
internal sealed class TrelloOAuthTokenResponse
{
    /// <summary>The OAuth 2.0 access token for Trello API requests (Bearer).</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>The new rotating refresh token. Present when offline_access was requested (we always do).</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>Seconds until AccessToken expires. Default 3600 (1 hour).</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>The scopes that were granted.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}
