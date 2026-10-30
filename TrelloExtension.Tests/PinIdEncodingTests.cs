// Pin Id encoding tests: board/list names are embedded in stable pin ids
// ("trello.board.{id}|{name}", "trello.list.{id}|{name}|{board}") and are
// URL-encoded so a name containing '|' cannot corrupt the Split('|', n)
// parsing in TrelloExtensionCommandsProvider.GetCommandItem.

using Xunit;

namespace TrelloExtension.Tests;

public class PinIdEncodingTests
{
    [Theory]
    [InlineData("First board")]
    [InlineData("Board | with | pipes")]
    [InlineData("Number (v2) access")]
    [InlineData("  leading and trailing  ")]
    [InlineData("")]
    public void RoundTrip_PreservesName(string name)
    {
        var encoded = TrelloExtensionCommandsProvider.EncodeIdName(name);
        Assert.DoesNotContain('|', encoded);
        Assert.Equal(name, TrelloExtensionCommandsProvider.DecodeIdName(encoded));
    }

    [Fact]
    public void Encode_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TrelloExtensionCommandsProvider.EncodeIdName(null!));
        Assert.Equal(string.Empty, TrelloExtensionCommandsProvider.DecodeIdName(null!));
    }

    [Fact]
    public void Decode_LegacyUnencodedValue_PassesThrough()
    {
        // Pin ids created before encoding was introduced stored the raw name.
        Assert.Equal("First board", TrelloExtensionCommandsProvider.DecodeIdName("First board"));
    }

    [Fact]
    public void BoardPinId_WithPipeInName_SplitAndDecodeRecoversName()
    {
        var name = "Sprint | 42";
        var id = $"trello.board.abc123|{TrelloExtensionCommandsProvider.EncodeIdName(name)}";

        var parts = id.Split('|', 2);

        Assert.Equal("trello.board.abc123", parts[0]);
        Assert.Equal(name, TrelloExtensionCommandsProvider.DecodeIdName(parts[1]));
    }

    [Fact]
    public void ListPinId_WithPipesInListAndBoardNames_SplitAndDecodeRecoversBoth()
    {
        var listName = "To | Do";
        var boardName = "Project | Status";
        var id = $"trello.list.list1|{TrelloExtensionCommandsProvider.EncodeIdName(listName)}|{TrelloExtensionCommandsProvider.EncodeIdName(boardName)}";

        var parts = id.Split('|', 3);

        Assert.Equal("trello.list.list1", parts[0]);
        Assert.Equal(listName, TrelloExtensionCommandsProvider.DecodeIdName(parts[1]));
        Assert.Equal(boardName, TrelloExtensionCommandsProvider.DecodeIdName(parts[2]));
    }
}
