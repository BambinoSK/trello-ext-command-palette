using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// A label attached to a Trello card.
/// Verified from Trello API docs:
///   labels: [{ id, idBoard, name, color }]
/// color is a named string: "red", "green", "blue", "yellow", "orange",
///   "purple", "pink", "sky", "lime", or null (label has no color).
/// name may be empty string (label has a color but no text name).
/// </summary>
internal sealed class TrelloLabel
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name of the label. May be empty if label has only a color.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Color name string, e.g. "red", "green", "blue", "yellow", "orange",
    /// "purple", "pink", "sky", "lime". Null or empty = no color assigned.
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>
    /// Display text: use name if set, otherwise fall back to the color name.
    /// </summary>
    public string DisplayText => !string.IsNullOrWhiteSpace(Name) ? Name : (Color ?? "label");
}
