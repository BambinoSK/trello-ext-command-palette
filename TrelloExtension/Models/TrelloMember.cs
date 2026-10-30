using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// A Trello member assigned to a card.
/// Verified from Trello API docs:
///   GET /1/cards/{id}/members?fields=avatarHash,fullName,initials,username
///   → [{ id, username, fullName, initials, avatarHash, avatarUrl, url }]
/// </summary>
internal sealed class TrelloMember
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("initials")]
    public string Initials { get; init; } = string.Empty;

    /// <summary>Hash for constructing avatar CDN URL (may be null if no avatar).</summary>
    [JsonPropertyName("avatarHash")]
    public string? AvatarHash { get; init; }

    /// <summary>Full avatar URL if returned by the API.</summary>
    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }

    /// <summary>Member's Trello profile URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Display text: full name if set, otherwise username.</summary>
    public string DisplayText => !string.IsNullOrWhiteSpace(FullName) ? FullName : Username;
}
