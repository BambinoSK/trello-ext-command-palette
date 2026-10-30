// Integration tests against a fake Trello REST API (StubTrelloHandler):
// no network, no credentials, no Windows Credential Locker. They pin down
// the exact wire behavior of TrelloService: endpoints, query params, JSON
// bodies for write calls, Bearer auth, and the 401 -> refresh -> retry flow.

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Services;
using Xunit;

namespace TrelloExtension.Tests;

public sealed class TrelloServiceIntegrationTests
{
    private const string BoardsJson = """
    [
      { "id": "b1", "name": "Personal", "url": "https://trello.com/b/per", "shortUrl": "https://trello.com/b/per", "closed": false },
      { "id": "b2", "name": "Work Projects", "url": "https://trello.com/b/wrk", "shortUrl": "https://trello.com/b/wrk", "closed": false }
    ]
    """;

    private const string CardsJson = """
    [
      { "id": "c1", "name": "Sample card", "desc": "**Ref:** [ITEM-1](https://example.com/1)", "idList": "l1", "idBoard": "b1", "closed": false, "pos": 1.0 },
      { "id": "c2", "name": "Second", "desc": "", "idList": "l1", "idBoard": "b1", "closed": false, "pos": 2.0 }
    ]
    """;

    /// <summary>A service wired to the stub API with a constant token.</summary>
    private static (TrelloService Service, StubTrelloHandler Handler) MakeService(string token = "test-access-token")
    {
        var handler = new StubTrelloHandler();
        var service = new TrelloService(handler, _ => Task.FromResult(token));
        return (service, handler);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBoardsAsync_ParsesBoards_AndSendsBearerToken()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/members/me/boards", (200, BoardsJson));

        var boards = await service.GetBoardsAsync();

        Assert.Equal(2, boards.Count);
        Assert.Equal("Personal", boards[0].Name);
        Assert.Equal("b2", boards[1].Id);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith("https://api.trello.com/1/members/me/boards", request.Url);
        Assert.Contains("fields=name,url,shortUrl,closed", request.Url);
        Assert.Equal("Bearer test-access-token", request.AuthHeader);
    }

    [Fact]
    public async Task GetListsAsync_RequestsOpenListsOnTheBoard()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/boards/b1/lists", (200, """[ { "id": "l1", "name": "To Do", "idBoard": "b1", "closed": false } ]"""));

        var lists = await service.GetListsAsync("b1");

        var list = Assert.Single(lists);
        Assert.Equal("To Do", list.Name);
        Assert.Contains("/1/boards/b1/lists?filter=open", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task GetCardsAsync_ParsesCards_IncludingDescAndPos()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/lists/l1/cards", (200, CardsJson));

        var cards = await service.GetCardsAsync("l1");

        Assert.Equal(2, cards.Count);
        Assert.Equal("Sample card", cards[0].Name);
        Assert.Equal("**Ref:** [ITEM-1](https://example.com/1)", cards[0].Desc);
        Assert.Equal(1.0, cards[0].Pos);
        Assert.Contains("/1/lists/l1/cards?filter=open&fields=", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task SearchCardsAsync_EmptyQuery_MakesNoRequest()
    {
        var (service, handler) = MakeService();

        var cards = await service.SearchCardsAsync("   ");

        Assert.Empty(cards);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchCardsAsync_ParsesCardsFromSearchResult()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/search", (200, """{ "cards": [ { "id": "c1", "name": "Sample card" } ] }"""));

        var cards = await service.SearchCardsAsync("sample");

        var card = Assert.Single(cards);
        Assert.Equal("Sample card", card.Name);
        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("/1/search?query=sample&modelTypes=cards", url);
    }

    [Fact]
    public async Task GetCardCommentsAsync_UsesCommentCardFilterAndLimit()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/cards/c1/actions", (200, "[]"));

        await service.GetCardCommentsAsync("c1");

        Assert.Contains("/1/cards/c1/actions?filter=commentCard&limit=1000", Assert.Single(handler.Requests).Url);
    }

    // ── Writes: user-controlled text travels in JSON bodies, not URLs ────────

    [Fact]
    public async Task CreateCardAsync_SendsIdListNamePosInJsonBody()
    {
        var (service, handler) = MakeService();
        handler.Stub("POST", "/1/cards", (200, """{ "id": "c9", "name": "New card", "idList": "l1", "pos": 2.5 }"""));

        var card = await service.CreateCardAsync("l1", "New card");

        Assert.NotNull(card);
        Assert.Equal("c9", card!.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/1/cards", request.Url);
        Assert.Null(request.Url.Split('?')[1..].FirstOrDefault()); // no query string
        Assert.Contains("\"idList\":\"l1\"", request.Body);
        Assert.Contains("\"name\":\"New card\"", request.Body);
        Assert.Contains("\"pos\":\"top\"", request.Body);
    }

    [Fact]
    public async Task AddCommentAsync_SendsTextInJsonBody_LongCommentsStayOutOfUrl()
    {
        var (service, handler) = MakeService();
        handler.Stub("POST", "/1/cards/c1/actions/comments", (200, """{ "id": "a1" }"""));
        var longText = new string('x', 20_000);

        await service.AddCommentAsync("c1", longText);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/1/cards/c1/actions/comments", new Uri(request.Url).AbsolutePath);
        Assert.Contains(longText, request.Body); // travels in the body...
        Assert.True(request.Url.Length < 500, $"URL is {request.Url.Length} chars"); // ...not the URL
    }

    [Fact]
    public async Task UpdateCardAsync_SendsNameAndDescInJsonBody()
    {
        var (service, handler) = MakeService();
        handler.Stub("PUT", "/1/cards/c1", (200, "{}"));

        await service.UpdateCardAsync("c1", "Renamed", "New description");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.EndsWith("/1/cards/c1", request.Url);
        Assert.Contains("\"name\":\"Renamed\"", request.Body);
        Assert.Contains("\"desc\":\"New description\"", request.Body);
    }

    [Fact]
    public async Task UpdateCommentAsync_PutsTextOnTheAction()
    {
        var (service, handler) = MakeService();
        handler.Stub("PUT", "/1/actions/a1", (200, "{}"));

        await service.UpdateCommentAsync("a1", "edited text");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/1/actions/a1", new Uri(request.Url).AbsolutePath);
        Assert.Contains("\"text\":\"edited text\"", request.Body);
    }

    [Fact]
    public async Task CopyCardAsync_SendsSourceListKeepAllInBody()
    {
        var (service, handler) = MakeService();
        handler.Stub("POST", "/1/cards", (200, """{ "id": "c9", "idList": "l2" }"""));

        await service.CopyCardAsync("c1", "l2");

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"idCardSource\":\"c1\"", request.Body);
        Assert.Contains("\"idList\":\"l2\"", request.Body);
        Assert.Contains("\"keepFromSource\":\"all\"", request.Body);
        Assert.Contains("\"pos\":\"bottom\"", request.Body);
    }

    // ── Writes: structural changes travel as query params ────────────────────

    [Fact]
    public async Task MoveCardAsync_PutsIdListAndPosInQuery()
    {
        var (service, handler) = MakeService();
        handler.Stub("PUT", "/1/cards/c1", (200, "{}"));

        await service.MoveCardAsync("c1", "l2", "top");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.EndsWith("/1/cards/c1?idList=l2&pos=top", request.Url);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task ArchiveCardAsync_PutsClosedTrue()
    {
        var (service, handler) = MakeService();
        handler.Stub("PUT", "/1/cards/c1", (200, "{}"));

        await service.ArchiveCardAsync("c1");

        Assert.EndsWith("/1/cards/c1?closed=true", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task DeleteCommentAsync_UsesDeleteMethod()
    {
        var (service, handler) = MakeService();
        handler.Stub("DELETE", "/1/actions/a1", (200, "{}"));

        await service.DeleteCommentAsync("a1");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.EndsWith("/1/actions/a1", request.Url);
    }

    [Fact]
    public async Task UpdateCardPositionAsync_ReturnsServerAssignedPos()
    {
        var (service, handler) = MakeService();
        handler.Stub("PUT", "/1/cards/c1", (200, """{ "id": "c1", "pos": 65536.5 }"""));

        var pos = await service.UpdateCardPositionAsync("c1", "65536.5");

        Assert.Equal(65536.5, pos);
    }

    // ── Auth: 401 -> refresh -> single retry ─────────────────────────────────

    [Fact]
    public async Task GetBoardsAsync_ExpiredToken_RefreshesOnceAndRetriesWithNewToken()
    {
        var handler = new StubTrelloHandler();
        handler.Stub("GET", "/1/members/me/boards", (401, null), (200, BoardsJson));

        var invalidated = false;
        var service = new TrelloService(
            handler,
            _ => Task.FromResult(invalidated ? "fresh-token" : "stale-token"),
            () => invalidated = true);

        var boards = await service.GetBoardsAsync();

        // The retry recovered and parsed the payload.
        Assert.Equal(2, boards.Count);
        // Exactly two requests: the rejected one and the retry (no loops).
        Assert.Equal(2, handler.Requests.Count);
        // The token was invalidated once and the retry used the refreshed token.
        Assert.True(invalidated);
        Assert.Equal("Bearer stale-token", handler.Requests[0].AuthHeader);
        Assert.Equal("Bearer fresh-token", handler.Requests[1].AuthHeader);
    }

    [Fact]
    public async Task GetBoardsAsync_TokenRejectedTwice_ThrowsAfterSingleRetry()
    {
        var handler = new StubTrelloHandler();
        handler.Stub("GET", "/1/members/me/boards", (401, null), (401, null));

        var invalidated = false;
        var service = new TrelloService(
            handler,
            _ => Task.FromResult(invalidated ? "fresh-token" : "stale-token"),
            () => invalidated = true);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetBoardsAsync());

        // Only one refresh attempt - a second 401 surfaces instead of looping.
        Assert.True(invalidated);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetBoardsAsync_ServerError_ThrowsWithoutRetry()
    {
        var (service, handler) = MakeService();
        handler.Stub("GET", "/1/members/me/boards", (500, null));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetBoardsAsync());

        // Non-401 failures are not retried.
        Assert.Single(handler.Requests);
    }
}
