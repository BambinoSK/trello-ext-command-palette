using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Auth;
using TrelloExtension.Models;

namespace TrelloExtension.Services;

/// <summary>
/// Wraps the Trello REST API v1 (https://api.trello.com/1/).
/// Authentication: OAuth 2.0 access token as a Bearer header (verified from the
/// Trello OAuth 2.0 docs: "pass the token in the Authorization http header as a
/// bearer token"). AuthService refreshes the short-lived access token (1 h)
/// automatically via the stored rotating refresh token.
///
/// Reads go through GetStringAuthAsync - a 401 (expired/revoked access token)
/// forces one token refresh and a single retry before surfacing the error.
///
/// Verified endpoints:
///   GET  /1/members/me/boards           → List of TrelloBoard
///   GET  /1/boards/{id}/lists           → List of TrelloList
///   GET  /1/lists/{id}/cards            → List of TrelloCard
///   GET  /1/search?query=…&modelTypes=cards → TrelloSearchResult
///   POST /1/cards                       → TrelloCard (create)
///   PUT  /1/cards/{id}                  → move / archive / mark complete
/// </summary>
// CA1001 (reported on the type): the test-constructor HttpClient is
// intentionally not disposed by TrelloService - it lives for the duration of
// the test process, and the production constructor only references the
// shared static client (never disposed, process-lifetime by design).
#pragma warning disable CA1001
internal sealed class TrelloService
{
    private const string BaseUrl = "https://api.trello.com/1";

    // Single shared HttpClient for production use - safe for AOT, avoids
    // socket exhaustion across all TrelloService instances.
    private static readonly HttpClient SharedHttp = new();

    // Instance transport + token seams. Production uses the shared client and
    // AuthService; the internal test constructor injects a stub handler and
    // token source (TrelloExtension.Tests.TrelloServiceIntegrationTests) so
    // integration tests run against a fake Trello API with no network,
    // credentials, or Windows Credential Locker.
#pragma warning restore CA1001
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _getValidAccessToken;
    private readonly Action _invalidateAccessToken;

    /// <summary>Production constructor: shared HttpClient, tokens from AuthService.</summary>
    public TrelloService()
    {
        _http = SharedHttp;
        _getValidAccessToken = ct => AuthService.Instance.GetValidAccessTokenAsync(ct);
        _invalidateAccessToken = () => AuthService.Instance.InvalidateAccessToken();
    }

    /// <summary>
    /// Test constructor: injectable HTTP transport and token source so the
    /// service can be driven by a fake Trello API in unit tests.
    /// </summary>
    internal TrelloService(HttpMessageHandler handler, Func<CancellationToken, Task<string>>? tokenProvider = null, Action? invalidateAccessToken = null)
    {
        _http = new HttpClient(handler);
        _getValidAccessToken = tokenProvider ?? (ct => AuthService.Instance.GetValidAccessTokenAsync(ct));
        _invalidateAccessToken = invalidateAccessToken ?? (() => AuthService.Instance.InvalidateAccessToken());
    }

    // CA1822: IsConfigured reads the AuthService singleton (no instance data)
    // but stays an instance member - callers receive TrelloService as a
    // dependency object and should not depend on statics.
#pragma warning disable CA1822
    /// <summary>True when AuthService has a stored OAuth 2.0 refresh token.</summary>
    public bool IsConfigured => AuthService.Instance.IsAuthenticated;
#pragma warning restore CA1822

    // ── Authenticated request helpers ───────────────────────────────────────

    /// <summary>
    /// Sends a request with the Authorization: Bearer header (verified OAuth 2.0
    /// API auth pattern - replaces the legacy key/token query params). The caller
    /// disposes the response.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthAsync(string url, HttpMethod method, CancellationToken ct = default)
    {
        var accessToken = await _getValidAccessToken(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON-body request with the Authorization: Bearer header
    /// Used for write endpoints whose parameters are user-controlled text
    /// (card names, descriptions, comment text): the REST docs
    /// state "query parameters may also be replaced with a JSON request
    /// body", and a body avoids URL-length limits with 16 KB descriptions/
    /// comments. Body built via JsonObject - AOT-safe (no reflection).
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthJsonAsync(string url, HttpMethod method, JsonObject body, CancellationToken ct = default)
    {
        var accessToken = await _getValidAccessToken(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET with the Bearer access token. A 401 forces one token refresh and a
    /// single retry (covers access-token expiry and server-side revocation).
    /// </summary>
    private async Task<string> GetStringAuthAsync(string url, CancellationToken ct = default)
    {
        using var first = await SendAuthAsync(url, HttpMethod.Get, ct).ConfigureAwait(false);
        if (first.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Access token rejected - invalidate the cached one and retry once.
            _invalidateAccessToken();
            using var retry = await SendAuthAsync(url, HttpMethod.Get, ct).ConfigureAwait(false);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        first.EnsureSuccessStatusCode();
        return await first.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // ── GET /1/members/me ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the member the current token authenticates as.
    /// Verified endpoint: GET /1/members/me?fields=id,username,fullName,initials,avatarHash,avatarUrl
    /// Used to check comment ownership: Trello only allows the comment CREATOR to
    /// edit/delete a comment, so the UI must not offer those actions for other
    /// members' comments (401).
    /// </summary>
    public async Task<TrelloMember?> GetCurrentMemberAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/members/me?fields=id,username,fullName,initials,avatarHash,avatarUrl";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloMember);
    }

    // ── GET /1/members/me/boards ────────────────────────────────────────────

    /// <summary>
    /// Returns all boards visible to the authenticated member.
    /// Verified endpoint: GET /1/members/me/boards?fields=name,url,shortUrl,closed
    /// </summary>
    public async Task<List<TrelloBoard>> GetBoardsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/members/me/boards?fields=name,url,shortUrl,closed";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloBoard) ?? [];
    }

    // ── GET /1/boards/{id}/lists ────────────────────────────────────────────

    /// <summary>
    /// Returns all open lists on a board.
    /// Verified endpoint: GET /1/boards/{boardId}/lists?filter=open
    /// </summary>
    public async Task<List<TrelloList>> GetListsAsync(string boardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/boards/{boardId}/lists?filter=open";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloList) ?? [];
    }

    // ── GET /1/lists/{id}/cards ─────────────────────────────────────────────

    /// <summary>
    /// Returns all open (non-archived) cards on a list.
    /// Verified endpoint: GET /1/lists/{listId}/cards?filter=open
    /// </summary>
    public async Task<List<TrelloCard>> GetCardsAsync(string listId, CancellationToken ct = default)
    {
        // Added "labels" to fields - verified: Trello returns labels array with {id,name,color}
        // Added "pos" - verified: pos is a numeric float used for card ordering; needed for move-up/down.
        var fields = "name,desc,idList,idBoard,due,dueComplete,closed,shortUrl,url,labels,pos";
        var url = $"{BaseUrl}/lists/{listId}/cards?filter=open&fields={fields}";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloCard) ?? [];
    }

    // ── GET /1/search?query=…&modelTypes=cards - Global card search ─────────

    /// <summary>
    /// Full-text card search across all accessible boards.
    /// Verified endpoint: GET /1/search?query=…&modelTypes=cards&cards_limit=20
    /// Response JSON: { "cards": [...] }
    /// </summary>
    public async Task<List<TrelloCard>> SearchCardsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&modelTypes=cards&cards_limit=20&card_fields=name,idList,idBoard,due,dueComplete,closed,shortUrl,url";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloSearchResult);
        return result?.Cards ?? [];
    }

    // ── GET /1/search?query=…&idBoards={boardId} - Board-scoped card search ──

    /// <summary>
    /// Searches for cards within a specific board.
    /// Verified endpoint: GET /1/search?query=…&modelTypes=cards&idBoards={boardId}&cards_limit=20
    /// The idBoards parameter restricts search results to the specified board.
    /// Verified from Trello REST API OpenAPI spec: idBoards is a comma-separated
    /// list of board IDs to limit the search to.
    /// </summary>
    public async Task<List<TrelloCard>> SearchCardsOnBoardAsync(string query, string boardId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&modelTypes=cards&idBoards={Uri.EscapeDataString(boardId)}&cards_limit=20&card_fields=name,idList,idBoard,due,dueComplete,closed,shortUrl,url,labels,pos";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloSearchResult);
        return result?.Cards ?? [];
    }

    // ── POST /1/cards ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new card in the specified list.
    /// Verified endpoint: POST /1/cards with a JSON body { idList, name, pos }
    /// </summary>
    public async Task<TrelloCard?> CreateCardAsync(string listId, string name, string pos = "top", CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["idList"] = listId,
            ["name"] = name,
            ["pos"] = pos,
        };
        using var response = await SendAuthJsonAsync($"{BaseUrl}/cards", HttpMethod.Post, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloCard);
    }

    // ── POST /1/cards?idCardSource=… - Copy card ───────────────────────────

    /// <summary>
    /// Copies an existing card to a target list.
    /// Verified endpoint: POST /1/cards with JSON body
    /// { idCardSource, idList, keepFromSource: "all", pos }.
    /// keepFromSource=all copies description, labels, checklists, and attachments (verified default).
    /// pos defaults to "bottom" in the target list.
    /// Response: the newly created Card JSON.
    /// </summary>
    public async Task<TrelloCard?> CopyCardAsync(string sourceCardId, string targetListId, string pos = "bottom", CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["idCardSource"] = sourceCardId,
            ["idList"] = targetListId,
            ["keepFromSource"] = "all",
            ["pos"] = pos,
        };
        using var response = await SendAuthJsonAsync($"{BaseUrl}/cards", HttpMethod.Post, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloCard);
    }

    // ── PUT /1/cards/{id}?pos=… - Update card position ─────────────────────

    /// <summary>
    /// Updates a card's position within its list.
    /// Verified endpoint: PUT /1/cards/{cardId}?pos={pos}
    /// pos accepts "top", "bottom", or a positive numeric value (verified).
    /// Trello uses fractional indexing and auto-renumbers when cards get too close.
    /// Returns the SERVER-ASSIGNED pos from the response body (ground truth for
    /// debugging move-by-2 - the server may normalize/clamp the requested pos).
    /// </summary>
    public async Task<double> UpdateCardPositionAsync(string cardId, string pos, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}?pos={Uri.EscapeDataString(pos)}";
        using var response = await SendAuthAsync(url, HttpMethod.Put, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The PUT response is the updated card JSON - read back the server pos.
        // Verified shape: top-level "pos" numeric field (same as GET /1/cards).
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        double? serverPos = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pos", out var posEl) && posEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                serverPos = posEl.GetDouble();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON body - leave serverPos null; callers fall back to requested value.
        }

        return serverPos ?? double.NaN;
    }

    // ── PUT /1/cards/{id} - Move card ───────────────────────────────────────

    /// <summary>
    /// Moves a card to a different list, placing it at the specified position.
    /// Verified endpoint: PUT /1/cards/{cardId}?idList={newListId}&pos={pos}
    /// pos accepts "top", "bottom", or a positive numeric value (verified).
    /// </summary>
    public async Task MoveCardAsync(string cardId, string newListId, string pos = "top", CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}?idList={Uri.EscapeDataString(newListId)}&pos={Uri.EscapeDataString(pos)}";
        using var response = await SendAuthAsync(url, HttpMethod.Put, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── PUT /1/cards/{id}?closed=true - Archive ─────────────────────────────

    /// <summary>
    /// Archives a card.
    /// Verified endpoint: PUT /1/cards/{cardId}?closed=true
    /// </summary>
    public async Task ArchiveCardAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}?closed=true";
        using var response = await SendAuthAsync(url, HttpMethod.Put, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── PUT /1/cards/{id}?dueComplete=true - Mark complete ──────────────────

    /// <summary>
    /// Marks the due date on a card as complete.
    /// Verified endpoint: PUT /1/cards/{cardId}?dueComplete=true
    /// </summary>
    public async Task MarkCardCompleteAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}?dueComplete=true";
        using var response = await SendAuthAsync(url, HttpMethod.Put, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── PUT /1/cards/{id}?dueComplete=false - Unmark complete ───────────────

    /// <summary>
    /// Removes the "complete" mark from a card's due date.
    /// Symmetric to MarkCardCompleteAsync - same endpoint, dueComplete=false.
    /// </summary>
    public async Task UnmarkCardCompleteAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}?dueComplete=false";
        using var response = await SendAuthAsync(url, HttpMethod.Put, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── PUT /1/cards/{id}?name=…&desc=… - Update card title + description ──

    /// <summary>
    /// Updates a card's name (title) and/or description in a single request.
    /// Verified endpoint: PUT /1/cards/{cardId} with JSON body { name, desc }
    /// (Trello REST API - both are updatable fields; sending only the
    /// fields you want to change is the documented pattern, and the docs allow
    /// query parameters to be replaced by a JSON body. desc may be an
    /// intentional empty string to clear the description.)
    /// </summary>
    public async Task UpdateCardAsync(string cardId, string name, string desc, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["desc"] = desc,
        };
        using var response = await SendAuthJsonAsync($"{BaseUrl}/cards/{cardId}", HttpMethod.Put, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── POST /1/cards/{id}/actions/comments - Add comment ───────────────────

    /// <summary>
    /// Adds a comment to a card.
    /// Verified endpoint: POST /1/cards/{cardId}/actions/comments with JSON
    /// body { text } (maxLength 16384; the REST API allows query
    /// parameters to be replaced by a JSON request body, which avoids
    /// URL-length limits with long comments).
    /// </summary>
    public async Task AddCommentAsync(string cardId, string text, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["text"] = text,
        };
        using var response = await SendAuthJsonAsync($"{BaseUrl}/cards/{cardId}/actions/comments", HttpMethod.Post, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── GET /1/cards/{id}/actions?filter=commentCard - List comments ────────

    /// <summary>
    /// Returns all comments (commentCard actions) on a card, newest-first.
    /// Verified endpoint: GET /1/cards/{cardId}/actions?filter=commentCard&limit=1000
    /// - filter=commentCard is REQUIRED: without it the endpoint defaults to
    ///   "commentCard,updateCard:idList" and card-move actions would be returned.
    /// - limit: 0–1000, default 50. 1000 fetches all comments without pagination.
    /// - memberCreator is included by default in the response; comment text lives at data.text.
    /// </summary>
    public async Task<List<TrelloComment>> GetCardCommentsAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}/actions?filter=commentCard&limit=1000";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloComment) ?? [];
    }

    // ── PUT /1/actions/{id}?text=… - Edit comment ────────────────────────────

    /// <summary>
    /// Edits an existing comment (commentCard action).
    /// Verified endpoint: PUT /1/actions/{actionId} with JSON body { text }
    /// (only comment actions can be updated, and only by the member who
    /// created the comment; the REST API allows query parameters
    /// to be replaced by a JSON request body).
    /// </summary>
    public async Task UpdateCommentAsync(string actionId, string text, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["text"] = text,
        };
        using var response = await SendAuthJsonAsync($"{BaseUrl}/actions/{actionId}", HttpMethod.Put, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── DELETE /1/actions/{id} - Delete comment ──────────────────────────────

    /// <summary>
    /// Deletes a comment (commentCard action).
    /// Verified endpoint: DELETE /1/actions/{actionId}
    /// (Trello REST API docs - only comment actions can be deleted, and
    /// only by the member who created the comment; no request body)
    /// </summary>
    public async Task DeleteCommentAsync(string actionId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/actions/{actionId}";
        using var response = await SendAuthAsync(url, HttpMethod.Delete, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── GET /1/cards/{id}/checklists ────────────────────────────────────────

    /// <summary>
    /// Returns all checklists (with check items) on a card.
    /// Verified endpoint: GET /1/cards/{cardId}/checklists?checkItem_fields=name,state,pos
    /// Response: array of { id, name, idBoard, idCard, pos, checkItems: [{ id, name, state, pos }] }
    /// </summary>
    public async Task<List<TrelloChecklist>> GetCardChecklistsAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}/checklists?checkItem_fields=name,state,pos";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloChecklist) ?? [];
    }

    // ── GET /1/cards/{id}/members ───────────────────────────────────────────

    /// <summary>
    /// Returns all members assigned to a card.
    /// Verified endpoint: GET /1/cards/{cardId}/members?fields=avatarHash,fullName,initials,username
    /// Response: array of { id, username, fullName, initials, avatarHash }
    /// </summary>
    public async Task<List<TrelloMember>> GetCardMembersAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}/members?fields=avatarHash,fullName,initials,username";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloMember) ?? [];
    }

    // ── GET /1/cards/{id}/attachments ───────────────────────────────────────

    /// <summary>
    /// Returns all attachments (uploads + links) on a card.
    /// Verified endpoint: GET /1/cards/{cardId}/attachments?fields=name,url,bytes,date,isUpload,mimeType
    /// Response: array of { id, name, url, bytes, date, isUpload, mimeType }
    /// (verified against the Trello Cards OpenAPI spec - getCardAttachments).
    /// </summary>
    public async Task<List<TrelloAttachment>> GetCardAttachmentsAsync(string cardId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/cards/{cardId}/attachments?fields=name,url,bytes,date,isUpload,mimeType";
        var json = await GetStringAuthAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, TrelloJsonContext.Default.ListTrelloAttachment) ?? [];
    }
}


