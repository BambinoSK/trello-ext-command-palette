using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Commands;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// Shows all comments (commentCard actions) on a specific Trello card.
/// Data source: GET /1/cards/{cardId}/actions?filter=commentCard&limit=1000
/// (TrelloService.GetCardCommentsAsync - API returns newest-first).
/// Supports live search (filters by comment text client-side after initial load).
/// </summary>
internal sealed partial class CardCommentsPage : DynamicListPage
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private List<TrelloComment> _comments = [];
    // Cached ListItem instances keyed by comment ID. Reusing the same ListItem
    // object across GetItems() calls keeps the host's SecondaryCommand (Ctrl+Enter)
    // initialized after in-place refreshes (PowerToys #48895) - same rationale as
    // CardsPage._listItemCache.
    private readonly ConcurrentDictionary<string, ListItem> _listItemCache = [];
    // Tracks the last-known comment text per comment ID. MoreCommands is rebuilt
    // when the text changes (after an edit) so EditCommentPage receives the new text.
    private readonly ConcurrentDictionary<string, string> _commentTextState = [];
    // Tracks the last-known comment ownership per comment ID. MoreCommands is
    // rebuilt when ownership becomes known (the /me fetch lands) so Edit/Delete
    // appear for my comments without a page re-open. bool is sufficient: while
    // /me is unknown, IsMyComment returns false and the menu renders without
    // Edit/Delete - identical to "not mine" - and the false→true transition
    // when the fetch lands still triggers the rebuild below.
    private readonly ConcurrentDictionary<string, bool> _commentMineState = [];
    private string _errorMessage = string.Empty;
    private DateTime _lastLoadTime = DateTime.MinValue;
    // The member the current token authenticates as (GET /1/members/me), used
    // to gate Edit/Delete: Trello only allows the comment CREATOR to edit or
    // delete a comment - everyone else gets 401 from the API. Null until the
    // one-time background fetch completes (or if it failed - then Edit/Delete
    // stay hidden rather than risk a guaranteed-401 action).
    private TrelloMember? _me;
    private bool _meFetchStarted;
    // 30s - same threshold and rationale as CardsPage.StaleThreshold: picks up
    // externally-added comments when the user revisits the page after a pause,
    // without triggering spurious reloads during normal browsing.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    public CardCommentsPage(TrelloService trello, TrelloCard card)
    {
        _trello = trello;
        _card = card;

        Icon = new IconInfo("\uE8BD"); // Message
        Title = $"Comments on \"{card.Name}\"";
        Name = "Show comments";
        PlaceholderText = "Search comments or type a comment to add…";

        // Deliberately NO LoadCommentsInBackground() here: this page is
        // constructed for every card in CardsPage.BuildMoreCommands (per card,
        // per MoreCommands rebuild), so a ctor load fired one comments API call
        // per card per render. The stale-check in GetItems() - which the user
        // only reaches by actually opening this page - triggers the first load.
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        // -2 = IncrementalRefresh → preserve selection during search filtering.
        RaiseItemsChanged(-2);
    }

    public override IListItem[] GetItems()
    {
        // Always show the details pane: row Titles are single-line truncated, so
        // the Details body is the only place the full comment text is readable.
        ShowDetails = true;

        // Fetch "me" once per page instance (in the background). Until it lands,
        // ownership is unknown → Edit/Delete stay hidden; the MoreCommands
        // ownership guard below rebuilds rows when the fetch completes and
        // RaiseItemsChanged(-2) re-renders the page.
        EnsureMeFetched();

        // If data is stale (older than threshold), reload in the background.
        // IsLoading is set synchronously in LoadCommentsInBackground, so this guard
        // also prevents stacking loads while one is in flight.
        if (!IsLoading && DateTime.UtcNow - _lastLoadTime > StaleThreshold)
        {
            LoadCommentsInBackground();
        }

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Failed to load comments",
                    Subtitle = _errorMessage,
                    Icon = new IconInfo("\uE783"), // Error icon
                },
            ];
        }

        var search = SearchText?.Trim() ?? string.Empty;
        var items = new List<IListItem>();

        // ── "Post comment" quick-add item ─
        // Posted via CreateCommentCommand → POST /1/cards/{id}/actions/comments;
        // the search box then clears and the list reloads unfiltered so the new
        // comment appears at the top (OnCommentPosted).
        if (!string.IsNullOrWhiteSpace(search))
        {
            items.Add(new ListItem(new CreateCommentCommand(_trello, _card.Id, search, OnCommentPosted))
            {
                Title = $"Post \"{search}\"",
                Subtitle = $"Add this comment to \"{_card.Name}\"",
                Icon = new IconInfo("\uE710"), // Add
            });
        }

        // ── Comment rows ───────────────────────────────────────────────────────
        var filtered = string.IsNullOrEmpty(search)
            ? _comments
            : _comments.Where(c => c.Text.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var comment in filtered)
        {
            // Reuse the same ListItem instance for this comment across refreshes
            // (host caches ListItemViewModels by COM object identity - PowerToys #48895).
            var listItem = _listItemCache.GetOrAdd(comment.Id, _ => new ListItem(new NoOpCommand()));
            listItem.Title = comment.TitleText;
            listItem.Subtitle = $"{comment.AuthorDisplay} · {comment.DateDisplay}";
            listItem.Icon = new IconInfo("\uE90A"); // Comment
            listItem.Details = BuildCommentDetails(comment);

            // Only rebuild MoreCommands when the comment text changed (after an
            // edit) OR when the ownership state changed (the /me fetch landed).
            // Both guards keep the host's SecondaryCommand initialized between
            // renders while still refreshing the menu after edits and once the
            // current member is known.
            var prevText = _commentTextState.GetValueOrDefault(comment.Id, string.Empty);
            var prevMine = _commentMineState.GetValueOrDefault(comment.Id);
            var isMine = IsMyComment(comment);
            if (prevText != comment.Text || prevMine != isMine || listItem.MoreCommands is null || listItem.MoreCommands.Length == 0)
            {
                listItem.MoreCommands = BuildCommentMoreCommands(comment);
                _commentTextState[comment.Id] = comment.Text;
                _commentMineState[comment.Id] = isMine;
            }

            items.Add(listItem);
        }

        if (filtered.Count == 0 && !IsLoading)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = string.IsNullOrEmpty(search) ? "No comments" : "No matching comments",
                Subtitle = string.IsNullOrEmpty(search)
                    ? "Type a comment in the search box above to post the first comment"
                    : $"Press Enter on \"Post …\" above to add this as a comment",
                Icon = new IconInfo("\uE8A1"), // PreviewLink
            });
        }

        return [.. items];
    }

    /// <summary>
    /// Builds the Details panel shown on the right side of Command Palette when a
    /// comment is selected. Verified Details API (same as CardsPage.BuildDetails):
    ///   Details { Title, Body (markdown), Metadata (IDetailsElement[]) }
    ///   DetailsLink has NO multi-arg constructor; always use object initializer.
    /// </summary>
    private Details BuildCommentDetails(TrelloComment comment)
    {
        var metaElements = new List<DetailsElement>();

        metaElements.Add(new DetailsElement
        {
            Key = "Author",
            Data = new DetailsLink() { Text = comment.AuthorDisplay },
        });

        if (!string.IsNullOrEmpty(comment.DateDisplay))
        {
            metaElements.Add(new DetailsElement
            {
                Key = "Date",
                Data = new DetailsLink() { Text = comment.DateDisplay },
            });
        }

        if (!string.IsNullOrEmpty(_card.ShortUrl))
        {
            // DetailsCommands + OpenUrlCommand instead of a linked DetailsLink.
            // Verified from PowerToys source (ShellPage.xaml "DetailsLinkTemplate"):
            // the HyperlinkButton binds BOTH NavigateUri="{x:Bind Link}" AND
            // Command="{x:Bind NavigateCommand}" (DetailsLinkViewModel creates
            // NavigateCommand → OpenInShell when Link is set) - so clicking a
            // linked DetailsLink launches the URI TWICE (host bug). A
            // DetailsCommands button renders as a plain Button invoking the
            // command once (OpenUrlCommand.Invoke → single OpenInShell).
            // Command.Name is settable (verified: Toolkit Command.cs) so the
            // button reads "Open card in Trello".
            //
            // Key is intentionally EMPTY - no "Card" header above the button.
            // Verified host limitation: the "DetailsCommandsTemplate"
            // (ShellPage.xaml:125) renders the Key TextBlock unconditionally (it
            // lacks the EmptyOrWhitespaceToCollapsed converter the Separator
            // template has), so an empty Key leaves only a caption-height blank
            // gap instead of a label.
            metaElements.Add(new DetailsElement
            {
                Key = string.Empty,
                Data = new DetailsCommands
                {
                    Commands =
                    [
                        new OpenUrlCommand(_card.ShortUrl)
                        {
                            Name = "Open card in Trello",
                            Icon = new IconInfo("\uE774"), // Globe
                        },
                    ],
                },
            });
        }

        // Verified: Metadata is IDetailsElement[] - use spread syntax [.. list]
        return new Details
        {
            Title = comment.AuthorDisplay,
            // Comment text is markdown BY DESIGN (Trello renders comments as
            // markdown) and the Details Body is markdown-rendered by Command
            // Palette - pass through raw so links/bold in comments work.
            Body = comment.Text,
            Metadata = [.. metaElements],
        };
    }

    /// <summary>
    /// Builds the right-click context menu (MoreCommands) for a comment row.
    /// Verified from ContentPageViewModel.cs (PowerToys source), same as CardsPage:
    ///   Position 0 (Enter)      = Edit comment (my comments only)
    ///   Position 1 (Ctrl+Enter) = Copy comment text (always present; becomes
    ///                             position 0/Enter for other members' comments)
    /// The destructive Delete sits after a separator (never on a shortcut slot)
    /// and is offered for my comments only - Trello 401s edit/delete for anyone
    /// but the creator.
    /// Do NOT use RequestedShortcut - known double-nav bug (#48725).
    /// </summary>
    private IContextItem[] BuildCommentMoreCommands(TrelloComment comment)
    {
        var cmds = new List<IContextItem>();
        var mine = IsMyComment(comment);

        // ── Position 0: Edit comment ─────────────────────────────────────────
        // ONLY for comments authored by the current member - verified: Trello
        // returns 401 when anyone but the creator edits a comment. While the
        // /me fetch is still in flight (null _me), ownership is unknown and the
        // action stays hidden; the ownership guard in GetItems() rebuilds the
        // menu once the fetch lands.
        if (mine)
        {
            // EditCommentPage pre-fills the form with the current text; on submit the
            // comments list reloads and the text-change guard rebuilds MoreCommands.
            cmds.Add(new CommandContextItem(new EditCommentPage(_trello, comment, _card.Name, LoadCommentsInBackground))
            {
                Title = "Edit comment…",
            });
        }

        // ── Position 1: Copy comment text ────────────────────────────────────
        // Always available - copying text is client-side, no Trello permission.
        cmds.Add(new CommandContextItem(new CopyTextCommand(comment.Text))
        {
            Title = "Copy comment text",
        });

        // ── Destructive ──────────────────────────────────────────────────────
        if (mine)
        {
            // ONLY for comments authored by the current member - verified: Trello
            // returns 401 when anyone but the creator deletes a comment.
            cmds.Add(new Separator());

            cmds.Add(new CommandContextItem(new DeleteCommentCommand(_trello, comment.Id, LoadCommentsInBackground))
            {
                Title = "Delete comment",
                IsCritical = true,
            });
        }

        // ── Page-level action ────────────────────────────────────────────────
        // Refresh is a page-level concern (not comment-specific), but Command
        // Palette only exposes the selected item's MoreCommands in the Ctrl+K
        // menu - pages have no MoreCommands of their own (verified in CardsPage).
        cmds.Add(new Separator());

        cmds.Add(new CommandContextItem(new AnonymousCommand(LoadCommentsInBackground)
        {
            Name = "Refresh",
            Result = CommandResult.KeepOpen(),
        })
        {
            Title = "Refresh comments",
            Icon = new IconInfo("\uE72C"), // Refresh
        });

        return [.. cmds];
    }

    /// <summary>
    /// True when the comment was authored by the member the current token
    /// authenticates as. While the /me fetch is in flight (null _me), returns
    /// false - the caller treats unknown as not-mine (actions hidden).
    /// Compare BOTH IdMemberCreator and MemberCreator.Id - the actions response
    /// always carries idMemberCreator, memberCreator may be absent.
    /// </summary>
    private bool IsMyComment(TrelloComment comment) =>
        _me is not null &&
        (comment.IdMemberCreator == _me.Id || comment.MemberCreator?.Id == _me.Id);

    /// <summary>
    /// Fetches GET /1/members/me once per page instance. On success, stores
    /// the member and re-renders (keep-selection) so each comment row's
    /// MoreCommands is rebuilt with the ownership gate applied. On failure,
    /// leaves _me null - Edit/Delete remain hidden for every comment (safe:
    /// showing them would only surface guaranteed-401 actions).
    /// </summary>
    private void EnsureMeFetched()
    {
        if (_meFetchStarted || _me is not null)
        {
            return;
        }

        _meFetchStarted = true;
        Task.Run(async () =>
        {
            try
            {
                _me = await _trello.GetCurrentMemberAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Network/auth failure - keep _me null; actions stay hidden.
            }

            if (_me is not null && _comments.Count > 0)
            {
                // -2 = IncrementalRefresh → keepSelection (verified host behavior,
                // same sentinel as every other in-place refresh in this extension).
                RaiseItemsChanged(-2);
            }
        });
    }

    /// <summary>
    /// Callback for the "Post …" quick-add (CreateCommentCommand). Clears the
    /// search box, then reloads the comments so the freshly posted comment
    /// appears at the top of the full (unfiltered) list - standard quick-add UX:
    /// submit → clear input → refreshed list + success toast.
    /// </summary>
    private void OnCommentPosted()
    {
        ClearSearch();
        LoadCommentsInBackground();
    }

    /// <summary>
    /// Clears the search text and syncs the host's search box.
    /// </summary>
    private void ClearSearch()
    {
        SearchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
    }

    /// <summary>
    /// Fetches comments from GET /1/cards/{cardId}/actions?filter=commentCard in
    /// background task. The API returns newest-first (verified sample response has
    /// descending dates); no client-side sort is applied.
    /// </summary>
    public void LoadCommentsInBackground()
    {
        IsLoading = true;
        _errorMessage = string.Empty;

        Task.Run(async () =>
        {
            try
            {
                var loaded = await _trello.GetCardCommentsAsync(_card.Id, CancellationToken.None).ConfigureAwait(false);
                _comments = loaded;
                _lastLoadTime = DateTime.UtcNow;

                // Prune stale ListItem cache entries for comments that were deleted.
                // Keeps the cache from growing unbounded and ensures the host doesn't
                // hold references to dead items - same rationale as CardsPage.
                var currentCommentIds = new HashSet<string>(loaded.Select(c => c.Id));
                foreach (var staleId in _listItemCache.Keys.Where(id => !currentCommentIds.Contains(id)))
                {
                    _listItemCache.TryRemove(staleId, out _);
                    _commentTextState.TryRemove(staleId, out _);
                }
            }
            catch (Exception ex)
            {
                _comments = [];
                _errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
                // -2 = IncrementalRefresh → keepSelection=true (same rationale as
                // CardsPage: after an edit/delete the selection stays put and the
                // focused comment's SecondaryCommand (Ctrl+Enter) stays active).
                RaiseItemsChanged(-2);
            }
        });
    }
}
