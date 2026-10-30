using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// Lightweight snapshot of a Trello board.
/// Fields sourced from GET /1/members/me/boards?fields=name,url,closed
/// Verified against Trello REST API docs: id is always returned implicitly.
/// </summary>
internal sealed class TrelloBoard
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Full board URL</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Short URL, e.g.</summary>
    [JsonPropertyName("shortUrl")]
    public string ShortUrl { get; init; } = string.Empty;

    /// <summary>True when the board is archived/closed.</summary>
    [JsonPropertyName("closed")]
    public bool Closed { get; init; }
}
