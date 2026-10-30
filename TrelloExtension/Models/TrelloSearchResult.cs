using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// Wraps the response from GET /1/search?modelTypes=cards
/// Verified: the top-level response is an object with a "cards" array.
/// </summary>
internal sealed class TrelloSearchResult
{
    [JsonPropertyName("cards")]
    public List<TrelloCard> Cards { get; init; } = [];
}
