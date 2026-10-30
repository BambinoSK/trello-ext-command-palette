using System;
using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// The persisted OAuth 2.0 token set for the signed-in Trello user.
///
/// Stored as a JSON blob in Windows Credential Locker.
/// Serialized via TrelloJsonContext (AOT-safe source generation).
///
/// Verified from Trello OAuth 2.0 docs (Confidential Client Usage):
///   - access_token expires after 1 hour by default (expires_in: 3600)
///   - refresh_token expires after 90 days and is SINGLE USE (rotating) -
///     every refresh returns a new refresh token which must replace the old one
/// </summary>
internal sealed class TrelloOAuthTokens
{
    /// <summary>Short-lived API access token, sent as Authorization: Bearer. Expires ~1 h after issue.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Single-use rotating refresh token. Expires after 90 days of inactivity.</summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>UTC instant when AccessToken expires (token issue time + expires_in seconds).</summary>
    [JsonPropertyName("expires_at_utc")]
    public DateTime ExpiresAtUtc { get; set; }
}
