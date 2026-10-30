using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrelloExtension.Models;

/// <summary>
/// A checklist on a Trello card.
/// Verified from Trello API docs (nested-resources guide):
///   GET /1/cards/{id}/checklists → [{ id, name, idBoard, idCard, pos, checkItems: [...] }]
/// checkItems array contains the individual checklist items with state (complete/incomplete).
/// </summary>
internal sealed class TrelloChecklist
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("idBoard")]
    public string IdBoard { get; init; } = string.Empty;

    [JsonPropertyName("idCard")]
    public string IdCard { get; init; } = string.Empty;

    [JsonPropertyName("pos")]
    public double Pos { get; init; }

    /// <summary>
    /// The check items on this checklist.
    /// Verified: each item has { id, name, state, pos, due, idMember }.
    /// state is "complete" or "incomplete".
    /// </summary>
    [JsonPropertyName("checkItems")]
    public List<TrelloCheckItem> CheckItems { get; init; } = [];

    /// <summary>
    /// Display text: "2/5" (completed/total) for compact display.
    /// </summary>
    public string ProgressText
    {
        get
        {
            if (CheckItems == null || CheckItems.Count == 0) return string.Empty;
            var completed = 0;
            foreach (var item in CheckItems)
            {
                if (item.State == "complete") completed++;
            }
            return $"{completed}/{CheckItems.Count}";
        }
    }
}

/// <summary>
/// A single check item within a checklist.
/// </summary>
internal sealed class TrelloCheckItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>"complete" or "incomplete".</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("pos")]
    public double Pos { get; init; }

    [JsonPropertyName("idChecklist")]
    public string IdChecklist { get; init; } = string.Empty;

    /// <summary>True when this check item is checked off.</summary>
    public bool IsComplete => State == "complete";
}
