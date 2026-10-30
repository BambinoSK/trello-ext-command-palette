using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// A file or link attached to a Trello card.
/// </summary>
internal sealed class TrelloAttachment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name of the attachment (file name or link label).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Download URL for uploaded files, or the linked URL for link attachments.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Size in bytes; NULL for link attachments (verified:
    /// GET /1/cards/{id}/attachments returns "bytes": null when isUpload=false).
    /// Empirically verified: non-nullable long makes System.Text.Json throw
    /// JsonException on the null → the whole list deserialization fails →
    /// empty cache → no Attachments row. Must stay nullable.
    /// </summary>
    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }

    /// <summary>Date the attachment was added, ISO 8601 date-time; null for links.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    /// <summary>True when the attachment is an uploaded file; false for linked URLs.</summary>
    [JsonPropertyName("isUpload")]
    public bool IsUpload { get; init; }

    /// <summary>MIME type for uploaded files; empty for link attachments.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = string.Empty;

    /// <summary>Human-readable size, e.g. "1.4 MB"; empty for link attachments (null bytes).</summary>
    public string DisplaySize
    {
        get
        {
            if (Bytes is not > 0)
            {
                return string.Empty;
            }

            string[] units = ["B", "KB", "MB", "GB"];
            var size = (double)Bytes.Value;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{Bytes} B" : $"{size:0.#} {units[unit]}";
        }
    }
}
