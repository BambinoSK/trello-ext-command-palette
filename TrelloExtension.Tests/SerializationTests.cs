// Serialization tests: all Trello API responses and the persisted OAuth
// token set go through the AOT-safe source-generated TrelloJsonContext.
// These tests pin down the wire format (property names, state values) and
// the computed properties (IsComplete, ProgressText) the UI depends on.

using System.Text.Json;
using TrelloExtension;
using TrelloExtension.Models;
using TrelloExtension.Pages;
using Xunit;

namespace TrelloExtension.Tests;

public class SerializationTests
{
    [Fact]
    public void OAuthTokens_RoundTrip_PreservesValues()
    {
        var tokens = new TrelloOAuthTokens
        {
            AccessToken = "at-token",
            RefreshToken = "rt-token",
            ExpiresAtUtc = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(tokens, TrelloJsonContext.Default.TrelloOAuthTokens);
        var back = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloOAuthTokens);

        Assert.NotNull(back);
        Assert.Equal(tokens.AccessToken, back.AccessToken);
        Assert.Equal(tokens.RefreshToken, back.RefreshToken);
        Assert.Equal(tokens.ExpiresAtUtc, back.ExpiresAtUtc);
    }

    [Fact]
    public void Checklists_DeserializeFromTrelloApiShape()
    {
        // Shape verified from the Trello REST API: checkItems[].state is
        // "complete" or "incomplete".
        const string json = """
        [
          {
            "id": "cl1",
            "name": "Deploy",
            "idBoard": "b1",
            "idCard": "c1",
            "pos": 16384,
            "checkItems": [
              { "id": "i1", "name": "Done", "state": "complete", "pos": 1, "idChecklist": "cl1" },
              { "id": "i2", "name": "Todo", "state": "incomplete", "pos": 2, "idChecklist": "cl1" }
            ]
          }
        ]
        """;

        var checklists = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloChecklist);

        Assert.NotNull(checklists);
        var checklist = Assert.Single(checklists);
        Assert.Equal("Deploy", checklist.Name);
        Assert.Equal(2, checklist.CheckItems.Count);
        Assert.True(checklist.CheckItems[0].IsComplete);
        Assert.False(checklist.CheckItems[1].IsComplete);
        Assert.Equal("1/2", checklist.ProgressText);
    }

    [Fact]
    public void Checklists_ApiJson_ToMarkdownPipeline()
    {
        // End-to-end: API JSON -> models -> details body markdown.
        const string json = """
        [
          {
            "id": "cl1",
            "name": "Deploy",
            "checkItems": [
              { "id": "i1", "name": "Done", "state": "complete", "idChecklist": "cl1" },
              { "id": "i2", "name": "Todo", "state": "incomplete", "idChecklist": "cl1" }
            ]
          }
        ]
        """;

        var checklists = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloChecklist);
        var markdown = CardsPage.BuildChecklistMarkdown(checklists!);

        Assert.Contains("### Deploy (1/2)", markdown);
        Assert.Contains("- [x] Done", markdown);
        Assert.Contains("- [ ] Todo", markdown);
    }
}
