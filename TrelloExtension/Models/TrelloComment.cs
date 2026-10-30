using System;
using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// A comment (commentCard action) on a Trello card.
/// Sourced from GET /1/cards/{cardId}/actions?filter=commentCard (TrelloService.GetCardCommentsAsync).
/// Verified against the "Get Actions on a Card" response sample:
///   [{ id, idMemberCreator, data: { text, card, board, list }, type: "commentCard",
///      date: "2020-03-09T19:41:51.396Z", memberCreator: { id, username, fullName, … } }]
/// The comment body is nested at data.text. memberCreator reuses TrelloMember -
/// verified: same field set as GET /1/cards/{id}/members (id, username, fullName,
/// initials, avatarHash, avatarUrl).
/// </summary>
internal sealed class TrelloComment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>ID of the member who wrote the comment (used as fallback when memberCreator is absent).</summary>
    [JsonPropertyName("idMemberCreator")]
    public string IdMemberCreator { get; init; } = string.Empty;

    /// <summary>Action type - always "commentCard" when filter=commentCard is used.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>ISO 8601 timestamp, e.g. "2020-03-09T19:41:51.396Z".</summary>
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;

    /// <summary>Action payload; the comment body lives at data.text.</summary>
    [JsonPropertyName("data")]
    public TrelloCommentData? Data { get; init; }

    /// <summary>
    /// The member who wrote the comment. Verified: included by default in the
    /// card actions response, but the model is null-defensive regardless.
    /// </summary>
    [JsonPropertyName("memberCreator")]
    public TrelloMember? MemberCreator { get; init; }

    /// <summary>The comment body (data.text).</summary>
    public string Text => Data?.Text ?? string.Empty;

    /// <summary>Display name of the comment author; falls back gracefully.</summary>
    public string AuthorDisplay =>
        !string.IsNullOrWhiteSpace(MemberCreator?.DisplayText) ? MemberCreator!.DisplayText : "Unknown member";

    /// <summary>Human-readable local timestamp for the comment row subtitle.</summary>
    public string DateDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(Date)) return string.Empty;
            if (!DateTime.TryParse(Date, out var d)) return string.Empty;
            var local = d.ToLocalTime();
            return $"{local:MMM d, yyyy h:mm tt}";
        }
    }

    /// <summary>
    /// Single-line title for list rows: first line of the comment, truncated.
    /// The full text is shown in the details pane (CardCommentsPage sets ShowDetails = true).
    /// </summary>
    public string TitleText
    {
        get
        {
            var span = Text.AsSpan();
            var firstLineEnd = span.IndexOf('\n');
            var hasMoreLines = firstLineEnd >= 0;
            if (firstLineEnd >= 0) span = span[..firstLineEnd];
            span = span.Trim();
            var truncated = span.Length > 120;
            if (truncated) span = span[..120].Trim();
            return hasMoreLines || truncated ? $"{span}…" : span.ToString();
        }
    }
}

/// <summary>
/// The data payload of a commentCard action. Only the text field is modeled -
/// the embedded card/board/list references (and data.old on edited comments)
/// are not needed for display and are ignored by the deserializer.
/// </summary>
internal sealed class TrelloCommentData
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
