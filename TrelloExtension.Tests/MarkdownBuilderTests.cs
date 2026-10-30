// Markdown builder tests: the card details Body is rendered as markdown by
// Command Palette. Content Trello itself renders as markdown (descriptions,
// comments, checklist names/items) must pass through RAW; only plain-text
// fields without legitimate markdown (attachment names) get escaped.

using TrelloExtension.Models;
using TrelloExtension.Pages;
using Xunit;

namespace TrelloExtension.Tests;

public class MarkdownBuilderTests
{
    private static TrelloChecklist MakeChecklist(params (string Name, string State)[] items) => new()
    {
        Id = "cl1",
        Name = "Deploy",
        CheckItems = [.. items.Select(i => new TrelloCheckItem { Id = i.Name, Name = i.Name, State = i.State })],
    };

    [Fact]
    public void BuildChecklistMarkdown_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CardsPage.BuildChecklistMarkdown(null!));
        Assert.Equal(string.Empty, CardsPage.BuildChecklistMarkdown([]));
    }

    [Fact]
    public void BuildChecklistMarkdown_RendersHeadingProgressAndMarkers()
    {
        var markdown = CardsPage.BuildChecklistMarkdown(
        [
            MakeChecklist(("Done", "complete"), ("Todo", "incomplete")),
        ]);

        Assert.Contains("### Deploy (1/2)", markdown);
        Assert.Contains("- [x] Done", markdown);
        Assert.Contains("- [ ] Todo", markdown);
    }

    [Fact]
    public void BuildChecklistMarkdown_ItemWithMarkdown_PassesThroughRaw()
    {
        // Regression test for the "broken links" bug: checklist item names are
        // markdown BY DESIGN in Trello and must NOT be escaped.
        var markdown = CardsPage.BuildChecklistMarkdown(
        [
            MakeChecklist(("See [docs](https://example.com)", "incomplete")),
        ]);

        Assert.Contains("See [docs](https://example.com)", markdown);
        Assert.DoesNotContain("\\[", markdown);
    }

    [Fact]
    public void BuildAttachmentMarkdown_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CardsPage.BuildAttachmentMarkdown(null!));
        Assert.Equal(string.Empty, CardsPage.BuildAttachmentMarkdown([]));
    }

    [Fact]
    public void BuildAttachmentMarkdown_Upload_ShowsNameAndSize()
    {
        var markdown = CardsPage.BuildAttachmentMarkdown(
        [
            new TrelloAttachment { Name = "report.pdf", Bytes = 2048 },
        ]);

        Assert.Contains("### Attachments (1)", markdown);
        Assert.Contains("- report.pdf (2 KB)", markdown);
    }

    [Fact]
    public void BuildAttachmentMarkdown_LinkAttachment_NoSizeShown()
    {
        var markdown = CardsPage.BuildAttachmentMarkdown(
        [
            new TrelloAttachment { Name = "https://example.com/spec", Bytes = null },
        ]);

        Assert.Contains("- https://example.com/spec", markdown);
        Assert.DoesNotContain("(0 B)", markdown);
    }

    [Fact]
    public void BuildAttachmentMarkdown_NameWithBrackets_IsEscaped()
    {
        // Attachment names are plain-text fields - brackets must not be able
        // to inject link syntax into the rendered body.
        var markdown = CardsPage.BuildAttachmentMarkdown(
        [
            new TrelloAttachment { Name = "report_(final).md", Bytes = 1024 },
        ]);

        // "report_(final).md" -> the underscore/parens are each replaced with
        // their escaped form: "report\_(final\).md".
        Assert.Contains("report\\_\\(final\\).md", markdown);
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a[b](c)d", "a\\[b\\]\\(c\\)d")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("a_b*c#d`e", "a\\_b\\*c\\#d\\`e")]
    public void EscapeMarkdown_EscapesControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, CardsPage.EscapeMarkdown(input));
    }
}
