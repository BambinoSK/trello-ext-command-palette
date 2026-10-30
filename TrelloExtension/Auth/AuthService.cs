using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;

namespace TrelloExtension.Auth;

/// <summary>
/// Stateful singleton that owns the Trello OAuth 2.0 (3LO) client and the per-user token set.
///
/// This extension is a PUBLIC client (no client secret - a distributed MSIX binary cannot
/// keep secrets; PKCE compensates, and public clients pass no secret on authorize/refresh).
///
/// Endpoints:
///   Authorization:      https://auth.atlassian.com/authorize
///   Token exchange:     POST https://auth.atlassian.com/oauth/token   (JSON body)
///   Token refresh:      POST https://auth.atlassian.com/oauth/token   (JSON body)
///   API authentication: Authorization: Bearer {access_token}
///
/// Token lifetimes: access token 1 hour, refresh token 90 days, SINGLE USE
/// (rotating) - every refresh must immediately persist the new refresh token.
///
/// Sign-in flow (loopback redirect, RFC 8252 §7.3):
///   1. User clicks "Connect" on BoardListPage → ConnectTrelloCommand
///   2. This service generates PKCE verifier/challenge + state, starts a loopback
///      TcpListener on 127.0.0.1 on one of the registered callback ports
///   3. System browser opens the authorize URL (redirect_uri = loopback callback)
///   4. User approves → browser redirects to http://127.0.0.1:{port}/callback?code=…&state=…
///   5. Listener validates state, exchanges the code (+ code_verifier) for tokens
///   6. Tokens persisted to Windows Credential Locker; BoardListPage reloads
///
/// Sign-out: delete tokens from vault → BoardListPage shows the Connect item again.
/// </summary>

// CA1001: AuthService is a process-lifetime singleton (AuthService.Instance) -
// nothing disposes it and nothing should. The only disposable it owns
// (_pendingCts) is disposed deterministically at the end of every sign-in
// handshake (see AcceptCallbackAsync's finally block); the field is null
// between sign-ins. Making the singleton IDisposable would imply a lifetime
// owner that does not exist.
#pragma warning disable CA1001

internal sealed class AuthService
{
    // ── Singleton ────────────────────────────────────────────────────────────

    public static readonly AuthService Instance = new();

    private AuthService()
    {
        _tokens = CredentialService.LoadTokens();
    }

    // ── Client configuration ──────────────────────────────────────────────────

    /// <summary>
    /// OAuth 2.0 client ID
    ///
    /// Supplied at BUILD time via the TrelloClientId MSBuild property, resolved
    /// in this order (see TrelloExtension.csproj):
    ///   1. -p:TrelloClientId=… (CI builds - inject the GitHub Actions secret)
    ///   2. TrelloClientId environment variable
    ///   3. client-id.local.props in the repo root
    ///   4. Placeholder → SignIn() shows a friendly setup error
    /// </summary>
    public string ClientId { get; } = TrelloClientIdConfig.Value;
    private const string LoopbackHost = "127.0.0.1";
    private static readonly int[] CallbackPorts = [8756, 8757, 8758, 8759];
    private const string ScopeString = "read:member:trello read:board:trello write:board:trello offline_access";
    private const string AuthorizeEndpoint = "https://auth.atlassian.com/authorize";
    private const string TokenEndpoint = "https://auth.atlassian.com/oauth/token";

    // Refresh early: the access token lasts 1 h; 5 min of slack avoids edge-case
    // failures from server-client clock skew.
    private static readonly TimeSpan RefreshSlack = TimeSpan.FromMinutes(5);

    // Single shared HttpClient - safe for AOT, avoids socket exhaustion. Same
    // pattern as TrelloService. Only used for the token endpoint (no per-request
    // default headers - the API bearer header is set by TrelloService).
    // Explicit 30s timeout: the HttpClient default (100s) would stall
    // GetValidAccessTokenAsync callers (and thus every API action) for that
    // long when the token endpoint hangs.
    private static readonly HttpClient TokenHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ── Token state ───────────────────────────────────────────────────────────

    private TrelloOAuthTokens? _tokens;

    /// <summary>True when a stored refresh token exists (access token may need refreshing).</summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(ClientId)
        && ClientId != "YOUR_TRELLO_OAUTH2_CLIENT_ID_HERE"
        && _tokens is { RefreshToken.Length: > 0 };

    // ── PKCE + state generation ───────────────────────────────────────────────

    /// <summary>PKCE code_verifier</summary>
    internal static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>PKCE code_challenge</summary>
    internal static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>Base64url without padding (RFC 7636 appendix). Plain loops - AOT-safe.</summary>
    internal static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Random state value for CSRF protection in the authorize redirect.</summary>
    internal static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    // ── Authorization URL + loopback callback ────────────────────────────────

    /// <summary>
    /// Per-sign-in attempt state: the generated PKCE verifier and state are stored
    /// here so the callback handler can validate and exchange. One attempt at a
    /// time - ConnectTrelloCommand replaces this when starting a new sign-in.
    /// </summary>
    internal sealed class AuthRequest
    {
        public required string CodeVerifier { get; init; }
        public required string State { get; init; }
        public required string RedirectUri { get; init; }
    }

    internal volatile AuthRequest? PendingRequest;

    /// <summary>
    /// Cancellation source for the in-flight sign-in: 5-minute timeout, cancelled
    /// early by CancelSignIn(). Owned by the sign-in; disposed on the next
    /// SignIn() attempt or after the handshake completes.
    /// </summary>
    private CancellationTokenSource? _pendingCts;

    /// <summary>
    /// Aborts an in-flight sign-in immediately (user closed the browser tab,
    /// changed their mind, or the flow is stuck). Safe when no sign-in is running.
    /// </summary>
    internal void CancelSignIn()
    {
        _pendingCts?.Cancel();
    }

    /// <summary>
    /// Starts a loopback TcpListener on the first free registered callback port and
    /// returns the authorize URL to open in the system browser. The listener waits
    /// (up to 5 minutes) for the OAuth redirect, then validates state and exchanges
    /// the code for tokens. Results surface via the returned completion task.
    ///
    /// Raw TcpListener instead of HttpListener: HttpListener prefixes can require
    /// URLACL permissions depending on machine state; raw loopback TCP needs none,
    /// and we only need to answer one GET and close.
    /// </summary>
    internal OAuthSignInResult SignIn()
    {
        if (ClientId == "YOUR_TRELLO_OAUTH2_CLIENT_ID_HERE")
        {
            return OAuthSignInResult.Fail("The extension is not configured - OAuth 2.0 client ID is missing");
        }

        // One sign-in at a time - a second attempt would orphan the first
        // listener and its state (the old listener's cleanup would null the new
        // PendingRequest and the state check would fail).
        if (PendingRequest is not null)
        {
            return OAuthSignInResult.Fail("A sign-in is already in progress - finish it in your browser, or cancel it");
        }

        // Try each registered callback port until one is free to bind.
        System.Net.Sockets.TcpListener? listener = null;
        var port = 0;
        foreach (var candidate in CallbackPorts)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
            try
            {
                probe.Start();
                listener = probe;
                port = candidate;
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Port busy - try the next registered callback port.
            }
        }

        if (listener is null)
        {
            return OAuthSignInResult.Fail("Could not open a local callback port (8756-8759 are all busy) - close the apps using them and try again");
        }

        var redirectUri = $"http://{LoopbackHost}:{port}/callback";
        var request = new AuthRequest
        {
            CodeVerifier = CreateCodeVerifier(),
            State = CreateState(),
            RedirectUri = redirectUri,
        };
        PendingRequest = request;

        // Cancellable timeout for the browser handshake. CancelSignIn() cancels
        // this source so the user can abort a stuck sign-in instantly instead
        // of waiting out the 5-minute timeout.
        _pendingCts?.Dispose();
        _pendingCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Verified authorize parameters (Confidential Client Usage, Step 1):
        // client_id, scope (space-separated, incl. offline_access), redirect_uri,
        // response_type=code, prompt=consent, code_challenge, code_challenge_method=S256.
        // state is not in Trello's parameter table but is supported (3LO
        // standard; TrelloDotNet generates and validates it) - always send it.
        var authorizeUrl =
            $"{AuthorizeEndpoint}" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            $"&scope={Uri.EscapeDataString(ScopeString)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code" +
            $"&prompt=consent" +
            $"&code_challenge={CreateCodeChallenge(request.CodeVerifier)}" +
            $"&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(request.State)}";

        // The browser handshake runs in the background; the command opens the
        // browser and returns immediately (the palette must not block).
        var completion = AcceptCallbackAsync(listener, request);
        return OAuthSignInResult.OpenBrowser(authorizeUrl, completion);
    }

    /// <summary>
    /// Accepts one HTTP GET on the loopback listener (the browser redirect after the
    /// user clicks Allow), answers with a minimal HTML page, validates state, and
    /// exchanges the authorization code for tokens. Verified from the docs: the code
    /// arrives in the "code" query parameter; exchange is a JSON POST to the token
    /// endpoint with client_id, grant_type, redirect_uri, code, code_verifier.
    /// </summary>
    private async Task<OAuthSignInResult> AcceptCallbackAsync(System.Net.Sockets.TcpListener listener, AuthRequest request)
    {
        // Capture the sign-in-scoped CTS BY VALUE before the handshake starts.
        // The finally block below disposes the captured local, never the
        // _pendingCts FIELD: if the user cancels and immediately re-invokes
        // SignIn(), the field is replaced with the NEW sign-in's CTS -
        // disposing the field here would kill the new sign-in's timeout and
        // leave its AcceptCallbackAsync holding a disposed source.
        var timeoutCts = _pendingCts!;
        var ctsCaptured = timeoutCts;

        try
        {
            string? code = null;
            string? returnedState = null;
            string? callbackError = null;

            // Accept connections until we get the actual redirect request. A browser
            // may open multiple connections (pre-connect probes, favicon, etc.) -
            // anything without a code is answered with the fallback page and skipped.
            while (code is null && callbackError is null)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return OAuthSignInResult.Fail("Sign-in cancelled - the browser never reached the local callback. Try connecting again.");
                }

                using (client)
                {
                    var stream = client.GetStream();

                    // Read the request head (request line + headers) - bounded so a
                    // misbehaving client cannot hang us or blow memory.
                    var buffer = new byte[8192];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && head.Length < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var requestHead = head.ToString();
                    var firstLineEnd = requestHead.IndexOf("\r\n", StringComparison.Ordinal);
                    if (firstLineEnd < 0) continue; // malformed request - drop connection, keep waiting

                    // Request line: "GET /callback?code=…&state=… HTTP/1.1"
                    var requestLine = requestHead[..firstLineEnd];
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2 || parts.Length > 0 && parts[0] != "GET") continue;
                    var path = parts[1];

                    var hasCode = TryGetQueryParam(path, "code", out code);
                    TryGetQueryParam(path, "state", out returnedState);
                    var hasError = TryGetQueryParam(path, "error", out callbackError);

                    // Answer the browser once with a tiny static page (success or
                    // error) so the tab doesn't hang on a pending connection.
                    var ok = hasCode && callbackError is null;
                    var html = BuildCallbackPage(ok);
                    var responseBytes = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: " +
                        Encoding.UTF8.GetByteCount(html) + "\r\n\r\n" + html);
                    await stream.WriteAsync(responseBytes, timeoutCts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (!hasCode && !hasError)
                    {
                        // Probe/favicon request without a code - keep waiting for
                        // the real redirect on the next accepted connection.
                        code = null;
                    }
                }
            }

            if (callbackError is not null)
            {
                return OAuthSignInResult.Fail($"Trello returned an error: {callbackError}");
            }

            if (code is null)
            {
                return OAuthSignInResult.Fail("The sign-in response did not contain an authorization code. Try connecting again.");
            }

            // Validate state BEFORE exchanging (CSRF protection).
            if (!string.Equals(returnedState, request.State, StringComparison.Ordinal))
            {
                return OAuthSignInResult.Fail("Sign-in state did not match - the response may have been tampered with. Try connecting again.");
            }

            // Exchange the code for tokens.
            var tokens = await ExchangeCodeAsync(code, request).ConfigureAwait(false);
            SaveTokens(tokens);
            return OAuthSignInResult.Success();
        }
        catch (Exception ex)
        {
            return OAuthSignInResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PendingRequest = null;
            listener.Stop();
            // The handshake is over (success, failure, timeout, or cancel) -
            // dispose THIS sign-in's captured CTS (by value, not the field: a
            // fast re-invoke may have already installed a new sign-in's CTS in
            // _pendingCts) and clear the field only if it still points at ours.
            ctsCaptured.Dispose();
            if (ReferenceEquals(_pendingCts, ctsCaptured))
            {
                _pendingCts = null;
            }
        }
    }

    /// <summary>Reads one query-string parameter (URL-decoded) from a request path like "/callback?code=…". Internal for unit tests.</summary>
    internal static bool TryGetQueryParam(string path, string name, out string? value)
    {
        value = null;
        var queryIndex = path.IndexOf('?');
        if (queryIndex < 0) return false;
        var query = path[(queryIndex + 1)..];

        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair[..eq] != name) continue;
            value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            return true;
        }

        return false;
    }

    /// <summary>Minimal static HTML served to the browser after the redirect. No resources, no JS.</summary>
    private static string BuildCallbackPage(bool success)
    {
        var title = success ? "Trello connected" : "Sign-in problem";
        var message = success
            ? "<p>You can close this tab and return to Command Palette.</p>"
            : "<p>Something went wrong - please try connecting again from Command Palette.</p>";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + title + "</title></head>" +
               "<body style=\"font-family:Segoe UI,sans-serif;padding:40px;text-align:center;\">" +
               "<h2>" + title + "</h2>" + message + "</body></html>";
    }

    // ── Token exchange / refresh ─────────────────────────────────────────────

    /// <summary>
    /// Exchanges an authorization code for access + refresh tokens.
    /// Verified request shape (Confidential Client Usage, Step 2): client_id,
    /// grant_type=authorization_code, redirect_uri, code, code_verifier. Public
    /// client → no client_secret field.
    /// Request body built via JsonObject - AOT-safe (no reflection), same pattern
    /// as TokenReviewPage's DataJson.
    /// </summary>
    private async Task<TrelloOAuthTokens> ExchangeCodeAsync(string code, AuthRequest request)
    {
        var body = new JsonObject
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = request.RedirectUri,
            ["code"] = code,
            ["code_verifier"] = request.CodeVerifier,
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await TokenHttp.PostAsync(TokenEndpoint, content).ConfigureAwait(false);
        return await ReadTokensAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token. Single-flight:
    /// concurrent callers await the same in-flight refresh. Rotating tokens:
    /// the new refresh token is persisted BEFORE the new access token is returned
    /// for use, so a crash cannot lose it (replaying a used refresh token can
    /// revoke the whole family).
    /// </summary>
    internal async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var current = _tokens;
        if (current is null || string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException("Not signed in - no refresh token stored");
        }

        // Clamp before subtracting the refresh slack: a legacy/corrupt vault blob
        // can deserialize ExpiresAtUtc = DateTime.MinValue, and MinValue - 5min
        // overflows (ArgumentOutOfRangeException - the same crash class
        // documented at InvalidateAccessToken). Clamping to a safe past instant
        // simply treats the token as expired → refresh.
        var expiresAt = current.ExpiresAtUtc;
        var expirySkew = expiresAt <= DateTime.MinValue + RefreshSlack
            ? DateTime.MinValue + RefreshSlack
            : expiresAt - RefreshSlack;
        if (DateTime.UtcNow < expirySkew && current.AccessToken.Length > 0)
        {
            return current.AccessToken;
        }

        // Single-flight refresh under a lock. The previous check-then-assign on
        // _refreshTask was racy: two concurrent callers could both pass the
        // IsCompleted check and start RefreshAsync with the SAME single-use
        // refresh token; the loser replays a consumed token → invalid_grant →
        // auto-sign-out. Double-checked locking over the volatile field
        // guarantees exactly one in-flight refresh.
        var refreshTask = _refreshTask;
        if (refreshTask is null || refreshTask.IsCompleted)
        {
            lock (_refreshLock)
            {
                refreshTask = _refreshTask;
                if (refreshTask is null || refreshTask.IsCompleted)
                {
                    // Read the token generation we are about to refresh. SignOut() bumps
                    // _generation - if it fires mid-refresh, the result must be discarded.
                    var generation = _generation;
                    current = _tokens;
                    if (current is null || string.IsNullOrEmpty(current.RefreshToken))
                    {
                        throw new InvalidOperationException("Signed out during token refresh");
                    }

                    refreshTask = RefreshAsync(current.RefreshToken, generation, ct);
                    _refreshTask = refreshTask;
                }
            }
        }

        return await refreshTask.ConfigureAwait(false);
    }

    private volatile Task<string>? _refreshTask;
    private readonly object _refreshLock = new();
    private int _generation;

    private async Task<string> RefreshAsync(string refreshToken, int generation, CancellationToken ct)
    {
        // Verified refresh request (Confidential Client Usage, Step 4): client_id,
        // grant_type=refresh_token, refresh_token. Public client → no client_secret.
        // JsonObject body - AOT-safe (see ExchangeCodeAsync).
        var body = new JsonObject
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await TokenHttp.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
        var tokens = await ReadTokensAsync(response).ConfigureAwait(false);

        // Guard: if SignOut() ran during the refresh, do not resurrect credentials.
        if (generation != _generation)
        {
            throw new InvalidOperationException("Signed out during token refresh");
        }

        // Persist the new token set (rotating refresh token replaced) BEFORE use.
        SaveTokens(tokens);
        return tokens.AccessToken;
    }

    /// <summary>Reads + validates a token response; throws on non-success with the server's error description.</summary>
    private async Task<TrelloOAuthTokens> ReadTokensAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Verified error shape: { "error": "...", "error_description": "..." }
            string errorCode = string.Empty;
            string description;
            try
            {
                using var doc = JsonDocument.Parse(json);
                errorCode = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? string.Empty
                    : errorCode.Length > 0 ? errorCode
                    : $"{(int)response.StatusCode} {response.StatusCode}";
            }
            catch (JsonException)
            {
                description = $"{(int)response.StatusCode} {response.StatusCode}";
            }

            // Refresh-token failures: the token is revoked (e.g. the user removed
            // the app from trello.com connected apps) or expired (90 days idle).
            // RFC 6749 §5.2 calls this "invalid_grant", but
            // authorization server also emits variants like "invalid_refresh_token"
            // / "refresh_token is invalid". A public client can never recover from
            // any of these - the only remedy is re-consent, so auto-sign-out instead of looping errors.
            var isRefreshTokenFailure = errorCode == "invalid_grant"
                || errorCode == "invalid_refresh_token"
                || (description.Contains("refresh", StringComparison.OrdinalIgnoreCase)
                    && (description.Contains("invalid", StringComparison.OrdinalIgnoreCase) || description.Contains("expired", StringComparison.OrdinalIgnoreCase)));
            if (isRefreshTokenFailure)
            {
                SignOut();
                throw new InvalidOperationException("Trello access was revoked or expired - sign in again");
            }

            throw new InvalidOperationException($"Trello sign-in failed: {description}");
        }

        var parsed = JsonSerializer.Deserialize(json, TrelloJsonContext.Default.TrelloOAuthTokenResponse)
            ?? throw new InvalidOperationException("Trello returned an empty token response");

        if (string.IsNullOrEmpty(parsed.AccessToken) || string.IsNullOrEmpty(parsed.RefreshToken))
        {
            throw new InvalidOperationException("Trello returned an incomplete token response (missing access or refresh token)");
        }

        return new TrelloOAuthTokens
        {
            AccessToken = parsed.AccessToken,
            RefreshToken = parsed.RefreshToken,
            // Default expires_in is 3600 s (verified); tolerate 0 defensively.
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 3600),
        };
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drops the cached access token so the next GetValidAccessTokenAsync call
    /// performs a refresh. Called by TrelloService when an API request got a 401
    /// (access token expired early or revoked server-side); the retry then uses
    /// a freshly refreshed token.
    /// </summary>
    internal void InvalidateAccessToken()
    {
        var current = _tokens;
        if (current is not null)
        {
            // Force the expiry check to fail without touching the refresh token.
            // NOTE: must be a RE presentable past instant, NOT DateTime.MinValue -
            // GetValidAccessTokenAsync subtracts the refresh slack from this value,
            // and MinValue - 5min overflows (ArgumentOutOfRangeException, the
            // "Failed to load lists" crash seen when access was revoked).
            _tokens = new TrelloOAuthTokens { AccessToken = string.Empty, RefreshToken = current.RefreshToken, ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) };
        }
    }

    /// <summary>Persists a fresh token set.</summary>
    internal void SaveTokens(TrelloOAuthTokens tokens)
    {
        _tokens = tokens;
        CredentialService.SaveTokens(tokens);
    }

    /// <summary>Clears in-memory + stored tokens. Bumps the generation so an in-flight refresh cannot resurrect them.</summary>
    public void SignOut()
    {
        _generation++;
        _refreshTask = null;
        _tokens = null;
        CredentialService.DeleteTokens();
        RaiseSignedOut();
    }

    /// <summary>
    /// Raised whenever stored credentials are cleared (explicit Disconnect or
    /// automatic sign-out after a revoked refresh token), so visible pages can
    /// re-render to the Connect state instead of looping API errors.
    /// Subscribers: BoardListPage (top-level state).
    /// </summary>
    internal static event Action? SignedOut;

    private static void RaiseSignedOut() => SignedOut?.Invoke();
}

/// <summary>
/// Outcome of a sign-in attempt, returned by AuthService.SignIn() to the
/// ConnectTrelloCommand. OpenBrowser carries the URL to launch plus the background
/// completion task; Fail carries a user-readable message for a toast.
/// </summary>
internal sealed class OAuthSignInResult
{
    public bool ShouldOpenBrowser { get; private init; }
    public string? AuthorizeUrl { get; private init; }
    public Task<OAuthSignInResult>? Completion { get; private init; }
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static OAuthSignInResult OpenBrowser(string authorizeUrl, Task<OAuthSignInResult> completion) => new() { ShouldOpenBrowser = true, AuthorizeUrl = authorizeUrl, Completion = completion };
    public static OAuthSignInResult Success() => new() { IsSuccess = true };
    public static OAuthSignInResult Fail(string message) => new() { ErrorMessage = message };
}
