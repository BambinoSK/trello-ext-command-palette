using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Auth;
using TrelloExtension.Commands;
using TrelloExtension.Models;
using TrelloExtension.Services;
using TrelloExtension.Settings;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// Top-level page: checks auth state first.
/// - Not authenticated → shows a single-click "Connect to Trello" item directly
///   (OAuth 2.0 browser sign-in starts immediately - no nested auth page), or a
///   "waiting" status row while a sign-in is completing in the browser.
/// - Authenticated → lists all open Trello boards; each drills to ListsPage.
/// - "Disconnect Trello" item always available at the bottom when authenticated.
/// </summary>
internal sealed partial class BoardListPage : DynamicListPage, IDisposable
{
    private readonly TrelloService _trello;
    private List<TrelloBoard> _boards = [];
    private string _errorMessage = string.Empty;

    // Trello logo PNG asset
    private static readonly IconInfo TrelloIcon =
        IconHelpers.FromRelativePaths(@"Assets\StoreLogo.png", @"Assets\StoreLogo.png");

    public BoardListPage(TrelloService trello)
    {
        _trello = trello;

        Icon = TrelloIcon;
        Title = "Trello";
        Name = "Open";
        PlaceholderText = "Search boards…";
        ShowDetails = false;

        // Start loading boards only if already authenticated.
        if (AuthService.Instance.IsAuthenticated)
        {
            LoadBoardsInBackground();
        }

        // Auto-sign-out (revoked refresh token → invalid_grant, or explicit
        // Disconnect from another page instance) must flip this page back to
        // the Connect row even while it is visible.
        AuthService.SignedOut += OnSignedOut;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        RaiseItemsChanged();
    }

    /// <summary>
    /// Disposes the page: unsubscribes from AuthService.SignedOut. The
    /// subscription held this page alive for process lifetime (BoardListPage
    /// is constructed on every provider creation and pinned-board restore),
    /// so removing it lets the GC reclaim the page when the host drops it.
    /// </summary>
    public void Dispose()
    {
        AuthService.SignedOut -= OnSignedOut;
        GC.SuppressFinalize(this);
    }

    public override IListItem[] GetItems()
    {
        // ── Not authenticated ────────────────────────────────────────────────
        if (!AuthService.Instance.IsAuthenticated)
        {
            // Sign-in already running (browser open) → status row + cancel.
            // The palette stays on THIS page; when the background handshake
            // completes (or fails / times out / is cancelled), OnAuthenticated /
            // OnSignInFailed raise ItemsChanged and this page re-renders.
            if (AuthService.Instance.PendingRequest is not null)
            {
                return
                [
                    new ListItem(new NoOpCommand())
                    {
                        Title = "Waiting for you to finish signing in",
                        Subtitle = "Complete the login and click Allow in your browser",
                        Icon = new IconInfo("\uE895"), // Sync / progress
                    },
                    new ListItem(new CancelSignInCommand(OnSignInFailed))
                    {
                        Title = "Cancel sign-in",
                        Subtitle = "Aborts the pending browser sign-in (e.g. you closed the tab)",
                        Icon = new IconInfo("\uE711"), // Cancel
                    },
                ];
            }

            // Not signed in → single-click connect right here (no nested page).
            return
            [
                new ListItem(new ConnectTrelloCommand(OnAuthenticated, OnSignInStarted, OnSignInFailed))
                {
                    Title = "Connect to Trello",
                    Subtitle = "Opens your browser - log in and click Allow; the extension finishes the rest automatically",
                    Icon = new IconInfo("\uE774"), // Globe
                },
            ];
        }

        // ── API error ────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Failed to load boards",
                    Subtitle = _errorMessage,
                    Icon = new IconInfo("\uE783"),
                },
                DisconnectItem(),
            ];
        }

        if (_boards.Count == 0 && !IsLoading)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "No boards found",
                    Subtitle = "No open boards visible to your Trello account",
                    Icon = TrelloIcon,
                },
                DisconnectItem(),
            ];
        }

        var search = SearchText?.Trim() ?? string.Empty;
        var items = new List<IListItem>();

        foreach (var board in _boards)
        {
            if (!string.IsNullOrEmpty(search) && !board.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            var listsPage = new ListsPage(_trello, board);
            // Stable Id so Command Palette's built-in Pin mechanism can restore
            // a pinned board via GetCommandItem(id) after restart. Same pattern
            // as ListsPage sets on CardsPage (trello.list.{…}).
            // Format: "trello.board.{boardId}|{boardName}" (name URL-encoded -
            // see TrelloExtensionCommandsProvider.EncodeIdName).
            listsPage.Id = $"trello.board.{board.Id}|{TrelloExtensionCommandsProvider.EncodeIdName(board.Name)}";

            items.Add(new ListItem(listsPage)
            {
                Title = board.Name,
                Icon = TrelloIcon,
            });
        }

        // Settings + Disconnect always at the bottom when authenticated.
        items.Add(new Separator());
        items.Add(SettingsItem());
        items.Add(DisconnectItem());

        return [.. items];
    }

    private static ListItem SettingsItem() =>
        new ListItem(TrelloSettingsManager.Instance.Settings.SettingsPage)
        {
            Title = "Settings",
            Subtitle = "New card position, auto-show details",
            Icon = new IconInfo("\uE713"), // Settings gear
        };

    private ListItem DisconnectItem() =>
        new ListItem(new DisconnectTrelloCommand(OnSignedOut))
        {
            Title = "Disconnect Trello",
            Subtitle = "Remove stored sign-in",
            Icon = new IconInfo("\uE7E8"), // LeaveChat / sign-out
        };

    /// <summary>
    /// Called by ConnectTrelloCommand right after the browser opens - swaps the
    /// Connect row for the "waiting" status row while the user is in the browser.
    /// </summary>
    private void OnSignInStarted()
    {
        RaiseItemsChanged();
    }

    /// <summary>
    /// Called when a sign-in attempt ends without success (failure, 5-minute
    /// timeout, or user cancellation) - the waiting rows flip back to the
    /// Connect row so the user is never stuck.
    /// </summary>
    private void OnSignInFailed()
    {
        RaiseItemsChanged();
    }

    /// <summary>Called by ConnectTrelloCommand after a successful OAuth 2.0 sign-in.</summary>
    private void OnAuthenticated()
    {
        LoadBoardsInBackground();
        RaiseItemsChanged();
    }

    /// <summary>Called by DisconnectTrelloCommand after signing out.</summary>
    private void OnSignedOut()
    {
        _boards = [];
        _errorMessage = string.Empty;
        RaiseItemsChanged();
    }

    private void LoadBoardsInBackground()
    {
        IsLoading = true;
        _errorMessage = string.Empty;

        Task.Run(async () =>
        {
            try
            {
                var all = await _trello.GetBoardsAsync(CancellationToken.None).ConfigureAwait(false);
                var open = new List<TrelloBoard>();
                foreach (var b in all)
                {
                    if (!b.Closed) open.Add(b);
                }
                open.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                _boards = open;
            }
            catch (Exception ex)
            {
                _boards = [];
                _errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        });
    }
}
