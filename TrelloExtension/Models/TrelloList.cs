using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// Lightweight snapshot of a Trello list (column on a board).
/// Fields sourced from GET /1/boards/{boardId}/lists
/// Verified: id, name, idBoard, closed are always returned.
/// </summary>
internal sealed class TrelloList
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("idBoard")]
    public string IdBoard { get; init; } = string.Empty;

    /// <summary>True when this list is archived.</summary>
    [JsonPropertyName("closed")]
    public bool Closed { get; init; }
}
