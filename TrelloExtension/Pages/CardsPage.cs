using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using TrelloExtension.Auth;
using TrelloExtension.Commands;
using TrelloExtension.Models;
using TrelloExtension.Services;
using TrelloExtension.Settings;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System;
using WinColor = Microsoft.CommandPalette.Extensions.Color;

namespace TrelloExtension.Pages;

/// <summary>
/// Shows all open cards in a specific Trello list.
/// Supports live search (filters by card name client-side after initial load).
///
/// Card UX:
///   - Selecting a card (Enter): shows card detail panel on the right (Details).
///   - Right-clicking a card: shows MoreCommands context menu with all actions
///     (Open in browser, Copy URL, Mark complete/incomplete, Move to…, Archive).
///   - "＋ New card" quick-add at top when search text is typed.
///
/// ShowDetails = true enables the right-panel detail view.
/// Verified Details API:
///   Details { Title, Body (markdown), Metadata (DetailsElement) }
///   DetailsElement { Key, Data = DetailsLink | DetailsTags | DetailsCommands }
///   DetailsLink(string text, string url) - verified constructors
///   DetailsTags { Tags = ITag[] }
/// </summary>
internal sealed partial class CardsPage : DynamicListPage, IDisposable
{
    private readonly TrelloService _trello;
    private readonly TrelloList _list;
    private readonly IReadOnlyList<TrelloList> _allListsOnBoard;
    private List<TrelloCard> _cards = [];
    // Filter dropdown (All | Completed | Incomplete | Overdue). The host writes
    // Filters.CurrentFilterId on a background thread; CardFilters notifies via
    // its own PropChanged → OnFilterChanged → RaiseItemsChanged(-2) re-renders
    // with the filter applied in GetItems(). Display-only: _cards and pos are
    // never mutated, so move up/down still edits the true board order.
    private readonly CardFilters _filters;
    // Cached ListItem instances keyed by card ID. Reusing the same ListItem
    // object across GetItems() calls lets the host's ListItemViewModel cache
    // (vmCache) find the existing VM by COM identity → SecondaryCommand stays
    // initialized → Ctrl+Enter works after in-place refreshes (PowerToys #48895).
    private readonly ConcurrentDictionary<string, ListItem> _listItemCache = [];
    // Tracks the last-known DueComplete state per card ID. MoreCommands is only
    // rebuilt when this changes (Mark complete ↔ Mark incomplete toggle). Avoids
    // reassigning MoreCommands on every GetItems() call, which would force the
    // host to re-initialize SecondaryCommand → temporarily null → Ctrl+Enter fails.
    private readonly ConcurrentDictionary<string, bool> _cardDueCompleteState = [];
    // Tracks the last-known card name per card ID. MoreCommands is rebuilt when
    // the name changes (e.g. after a rename) so EditCardPage and other sub-pages
    // receive the updated TrelloCard with the new name.
    private readonly ConcurrentDictionary<string, string> _cardNameState = [];
    // Tracks the last-known card description per card ID. MoreCommands is rebuilt
    // when the description changes (e.g. after an edit) so EditCardPage's form is
    // pre-filled with the current description instead of the stale one captured
    // when the MoreCommands were first built.
    private readonly ConcurrentDictionary<string, string> _cardDescState = [];
    private string _errorMessage = string.Empty;
    private DateTime _lastLoadTime = DateTime.MinValue;
    // 30s: long enough that detail-fetch completions (which take ~1–5s) don't
    // trigger spurious card reloads via the stale-check cascade, but short
    // enough to pick up moved/copied cards when the user revisits after a
    // brief pause. The previous 3s value caused intermittent Ctrl+Enter
    // failures: detail fetches completed after ~3s → RaiseItemsChanged() →
    // GetItems() → stale check triggered LoadCardsInBackground() → IsLoading
    // = true → host rebuilt ListItems → selection jumped (PowerToys #48895)
    // → Ctrl+Enter activated the wrong card or did nothing.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    // Lazy-loaded card details (checklists + members + attachments), cached per card ID.
    // Fetched on first focus via BuildDetails; refreshes the list when complete.
    // Uses ConcurrentDictionary for thread-safe access from both UI and background tasks.
    private readonly ConcurrentDictionary<string, (List<TrelloChecklist> Checklists, List<TrelloMember> Members, List<TrelloAttachment> Attachments)> _detailCache = [];
    private readonly ConcurrentDictionary<string, byte> _detailFetchInProgress = [];

    // Debounce timer for detail-fetch completions. Collapses multiple
    // RaiseItemsChanged() calls (one per card) into a single list refresh
    // after 500ms of quiet, preventing rapid selection jumps (PowerToys
    // issue #48895) and the resulting intermittent Ctrl+Enter failures.
    private readonly Timer _detailRefreshDebounce;

    public CardsPage(TrelloService trello, TrelloList list, IReadOnlyList<TrelloList> allListsOnBoard)
    {
        _trello = trello;
        _list = list;
        _allListsOnBoard = allListsOnBoard;

        Icon = new IconInfo("\uE8FD"); // BulletedList
        Title = list.Name;
        Name = "Open";
        PlaceholderText = "Search cards or type name to create…";

        // Filters dropdown - CardFilters defaults CurrentFilterId to the first
        // entry ("All"), raising one harmless construction-time callback.
        _filters = new CardFilters(OnFilterChanged);
        Filters = _filters;
        // ShowDetails is re-evaluated in GetItems() so setting changes apply to
        // already-created (pinned) pages without requiring a page recreation.
        // MoreCommands order controls footer keyboard shortcuts (verified: ContentPageViewModel.cs):
        //   Position 0 → Enter        = Mark complete / incomplete
        //   Position 1 → Ctrl+Enter   = Open in browser

        // -2 = ListViewModel.IncrementalRefresh sentinel → keepSelection=true.
        // Verified: ListViewModel.RequestFetch(args.TotalItems == IncrementalRefresh)
        // with IncrementalRefresh = -2. This preserves selection after in-place
        // actions (move up/down, mark complete, etc.) so the host doesn't force
        // selection to the first row and lose the SecondaryCommand (Ctrl+Enter).
        _detailRefreshDebounce = new Timer(_ => RaiseItemsChanged(-2), null, Timeout.Infinite, Timeout.Infinite);

        // Deliberately NO LoadCardsInBackground() here. This page is constructed
        // for every row on every parent render (ListsPage.GetItems builds one
        // CardsPage per list; GetCommandItem builds them for pinned lists), so a
        // ctor load fired one Trello API call per constructed page - one per list
        // per search keystroke. The first GetItems() (when the user actually
        // navigates here) triggers the load via the stale-check below
        // (_lastLoadTime starts at DateTime.MinValue → always "stale").
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        // -2 = IncrementalRefresh → preserve selection during search filtering.
        RaiseItemsChanged(-2);
    }

    public override IListItem[] GetItems()
    {
        // Re-evaluate ShowDetails on each render so setting changes apply to
        // already-created (pinned) pages without requiring a page recreation.
        ShowDetails = TrelloSettingsManager.Instance.AutoShowDetails;

        // Auto-sign-out may have fired while this page was open (revoked
        // refresh token) - show the signed-out state, not the raw API error.
        if (!AuthService.Instance.IsAuthenticated)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Trello is not connected",
                    Subtitle = "Access was revoked or expired - go back to Trello Extension and sign in",
                    Icon = new IconInfo("\uE783"),
                },
            ];
        }

        // If data is stale (older than threshold), reload in the background.
        // This ensures moved/copied cards appear in the target list when the user
        // navigates to it, even if the page was cached from a previous visit.
        if (!IsLoading && DateTime.UtcNow - _lastLoadTime > StaleThreshold && _detailFetchInProgress.IsEmpty)
        {
            LoadCardsInBackground();
        }

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Failed to load cards",
                    Subtitle = _errorMessage,
                    Icon = new IconInfo("\uE783"), // Error icon
                },
            ];
        }

        var search = SearchText?.Trim() ?? string.Empty;
        var items = new List<IListItem>();

        // ── "＋ New card" quick-add item ─────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(search))
        {
            items.Add(new ListItem(new CreateCardCommand(_trello, _list.Id, search, OnCardCreated))
            {
                Title = $"Create \"{search}\"",
                Subtitle = $"Add new card to \"{_list.Name}\"",
                Icon = new IconInfo("\uE710"), // Add
            });
        }

        // ── Card rows ────────────────────────────────────────────────────────
        // Filter (dropdown) and search (text box) combine - both applied here.
        // The filter is display-only; _cards keeps the full set so stale-check
        // reloads, move up/down, and count-based logic all see every card.
        var filtered = _cards;
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        filtered = _filters.CurrentFilterId switch
        {
            CardFilters.CompletedId => filtered.Where(c => c.DueComplete).ToList(),
            CardFilters.IncompleteId => filtered.Where(c => !c.DueComplete).ToList(),
            CardFilters.OverdueId => filtered.Where(c => c.IsOverdue).ToList(),
            _ => filtered.ToList(),
        };

        foreach (var card in filtered)
        {
            // ── Separator detection ───────────────────────────────────────────
            if (IsSeparatorCard(card.Name))
            {
                var sectionTitle = ExtractSeparatorTitle(card.Name);
                items.Add(new Separator(sectionTitle));
                continue;
            }

            // ── Icon: three states ────────────────────────────────────────────
            // Due-complete  → CheckboxComposite \uE73A
            // Overdue       → Important         \uE814
            // Normal        → Checkbox          \uE739
            var icon = card.DueComplete
                ? new IconInfo("\uE73A") // CheckboxComposite - due date completed
                : card.IsOverdue
                    ? new IconInfo("\uE814") // Important - overdue
                    : new IconInfo("\uE739"); // Checkbox - normal card

            // ── Subtitle: due date if present ────────────────────────────────
            var subtitle = string.IsNullOrEmpty(card.DueDisplay)
                ? string.Empty
                : card.DueDisplay;

            // ── Tags: label chips with overflow ──────────────────────────────
            const int maxVisibleTags = 2;
            var tags = BuildTags(card, maxVisibleTags);

            // ── Details panel (right side) ───────────────────────────────────
            // Verified: Details { Title, Body (markdown), Metadata (IDetailsElement) }
            // DetailsElement.Data accepts DetailsLink, DetailsTags, DetailsCommands.
            var details = BuildDetails(card);

            // ── MoreCommands (right-click context menu) ──────────────────────
            // Verified: CommandContextItem(ICommand) - accepts any ICommand incl. ListPage.
            var moreCommands = BuildMoreCommands(card);

            // Primary action: OpenUrlCommand - Enter opens card in browser.
            // "Open in browser" duplicate is gone; host no longer injects it since we
            // moved to NoOpCommand. Actually: keeping OpenUrlCommand here BUT removing
            // the redundant "Open in browser" from MoreCommands avoids duplication.
            // The host injects "Open" for the primary command - so we remove our "Open in browser".
            // Primary = NoOpCommand so Enter does nothing (details shown via right-click "Show details").
            // Reuse the same ListItem instance for this card across refreshes.
            // The host caches ListItemViewModel by COM object identity; creating
            // a new ListItem each time forces a new VM → SafeSlowInit runs again
            // → SecondaryCommand is null until init completes → Ctrl+Enter fails.
            // By reusing the instance, the host finds the existing VM with its
            // already-initialized SecondaryCommand (verified: ListViewModel._vmCache).
            var listItem = _listItemCache.GetOrAdd(card.Id, _ => new ListItem(new NoOpCommand()));
            listItem.Title = card.Name;
            listItem.Subtitle = subtitle;
            listItem.Icon = icon;
            listItem.Tags = tags;
            listItem.Details = details;

            // Only rebuild MoreCommands when the card's DueComplete state or name
            // changed (Mark complete ↔ Mark incomplete toggle, or after a rename).
            // Reassigning MoreCommands with a new array every time forces the host's
            // ListItemViewModel to re-initialize SecondaryCommand via SafeSlowInit on
            // a background thread → SecondaryCommand is null until init completes →
            // Ctrl+Enter does nothing.
            var prevDueComplete = _cardDueCompleteState.GetValueOrDefault(card.Id, !card.DueComplete);
            var prevName = _cardNameState.GetValueOrDefault(card.Id, string.Empty);
            var prevDesc = _cardDescState.GetValueOrDefault(card.Id, string.Empty);
            if (prevDueComplete != card.DueComplete || prevName != card.Name || prevDesc != card.Desc ||
                listItem.MoreCommands is null || listItem.MoreCommands.Length == 0)
            {
                listItem.MoreCommands = moreCommands;
                _cardDueCompleteState[card.Id] = card.DueComplete;
                _cardNameState[card.Id] = card.Name;
                _cardDescState[card.Id] = card.Desc;
            }

            items.Add(listItem);
        }

        if (items.Count == 0 && !IsLoading)
        {
            // Context-aware empty state: different hint when a filter is active.
            // Subtitle uses CurrentFilterName (display name, e.g. "Overdue") -
            // NOT CurrentFilterId, which is the lowercase machine id ("overdue").
            var filterActive = _filters.CurrentFilterId != CardFilters.AllId;
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = filterActive ? "No matching cards" : "No cards",
                Subtitle = filterActive
                    ? $"No cards match the \"{_filters.CurrentFilterName}\" filter"
                    : $"Type a name above to create the first card in \"{_list.Name}\"",
                Icon = new IconInfo("\uE8A1"),
            });
        }

        return [.. items];
    }

    /// <summary>
    /// Builds the Details panel shown on the right side of Command Palette when a card is selected.
    /// </summary>
    private Details BuildDetails(TrelloCard card)
    {
        var metaElements = new List<DetailsElement>();

        // Due date - text-only metadata row (no link); includes time in the detail panel.
        if (!string.IsNullOrEmpty(card.DueDisplayWithTime))
        {
            metaElements.Add(new DetailsElement
            {
                Key = "Due",
                Data = new DetailsLink() { Text = card.DueDisplayWithTime },
            });
        }

        // Labels as tags in the detail panel
        if (card.Labels != null && card.Labels.Count > 0)
        {
            metaElements.Add(new DetailsElement
            {
                Key = "Labels",
                Data = new DetailsTags() { Tags = BuildTags(card, card.Labels.Count) },
            });
        }

        // ── Members / Attachments - fetched lazily ────────────────────────────
        // Members:  GET /1/cards/{id}/members
        // Attachments: GET /1/cards/{id}/attachments
        var fetchMembers = TrelloSettingsManager.Instance.FetchMembers;
        var fetchChecklists = TrelloSettingsManager.Instance.FetchChecklists;
        var fetchAttachments = TrelloSettingsManager.Instance.FetchAttachments;

        if (fetchMembers || fetchChecklists || fetchAttachments)
        {
            EnsureCardDetailsFetched(card.Id, fetchChecklists, fetchMembers, fetchAttachments);
        }

        if (fetchMembers && _detailCache.TryGetValue(card.Id, out var cached) && cached.Members.Count > 0)
        {
            var memberNames = string.Join(", ", cached.Members.ConvertAll(m => m.DisplayText));
            metaElements.Add(new DetailsElement
            {
                Key = "Members",
                Data = new DetailsLink() { Text = memberNames },
            });
        }


        // Link to card - LAST metadata row. DetailsCommands + OpenUrlCommand instead of a linked DetailsLink.
        // Key is intentionally EMPTY - no "URL" header above the button.
        if (!string.IsNullOrEmpty(card.ShortUrl))
        {
            metaElements.Add(new DetailsElement
            {
                Key = string.Empty,
                Data = new DetailsCommands
                {
                    Commands =
                    [
                        new OpenUrlCommand(card.ShortUrl)
                        {
                            Name = "Open in Trello",
                            Icon = new IconInfo("\uE774"), // Globe
                        },
                    ],
                },
            });
        }

        // ── Checklists - rendered as markdown in the body ────────────────────
        // Card descriptions are markdown BY DESIGN (Trello renders them as
        // markdown, and Command Palette renders the Details Body as markdown),
        // so card.Desc is passed through RAW - bold/links/etc. in the
        // description render exactly like they do in Trello.
        var body = card.Desc ?? string.Empty;

        if (fetchChecklists)
        {
            if (_detailCache.TryGetValue(card.Id, out var cachedDetail) && cachedDetail.Checklists.Count > 0)
            {
                var checklistMarkdown = BuildChecklistMarkdown(cachedDetail.Checklists);
                if (!string.IsNullOrEmpty(checklistMarkdown))
                {
                    body = string.IsNullOrEmpty(body)
                        ? checklistMarkdown
                        : $"{body}\n\n{checklistMarkdown}";
                }
            }
            else if (_detailFetchInProgress.ContainsKey(card.Id))
            {
                // Still loading - show a hint.
                body = string.IsNullOrEmpty(body)
                    ? "_Loading checklists…_"
                    : $"{body}\n\n_Loading checklists…_";
            }
        }

        // ── Attachments - rendered as a markdown bullet list in the body ────
        // (same rendering path as checklists; one bullet per attachment keeps
        // names+sizes readable instead of one long comma-separated string).
        // Names containing markdown control characters are escaped so a file
        // called "readme_(v2).md" doesn't inject formatting/link syntax.
        if (fetchAttachments)
        {
            if (_detailCache.TryGetValue(card.Id, out var cachedAtt) && cachedAtt.Attachments.Count > 0)
            {
                var attachmentMarkdown = BuildAttachmentMarkdown(cachedAtt.Attachments);
                body = string.IsNullOrEmpty(body)
                    ? attachmentMarkdown
                    : $"{body}\n\n{attachmentMarkdown}";
            }
            else if (_detailFetchInProgress.ContainsKey(card.Id))
            {
                // Still loading - show a hint.
                body = string.IsNullOrEmpty(body)
                    ? "_Loading attachments…_"
                    : $"{body}\n\n_Loading attachments…_";
            }
        }

        // Verified: Metadata is IDetailsElement[] - use spread syntax [.. list]
        return new Details
        {
            Title = card.Name,
            Body = body,
            Metadata = [.. metaElements],
        };
    }

    /// <summary>
    /// Lazily fetches checklists and/or members for a card if not yet cached.
    /// Verified endpoints:
    ///   GET /1/cards/{id}/checklists?checkItem_fields=name,state,pos
    ///   GET /1/cards/{id}/members?fields=avatarHash,fullName,initials,username
    /// </summary>
    private void EnsureCardDetailsFetched(string cardId, bool fetchChecklists, bool fetchMembers, bool fetchAttachments)
    {
        if (_detailCache.ContainsKey(cardId) || _detailFetchInProgress.ContainsKey(cardId))
            return;

        _detailFetchInProgress.TryAdd(cardId, 0);

        Task.Run(async () =>
        {
            try
            {
                List<TrelloChecklist> checklists = [];
                List<TrelloMember> members = [];
                List<TrelloAttachment> attachments = [];

                // Kick off each enabled fetch independently. Each sub-task is
                // individually try/catch-wrapped: Task.WhenAll aborts on the
                // FIRST fault, and an unwrapped attachment failure would
                // discard already-fetched members/checklists for this card.
                // Wrapped tasks always complete successfully (null on failure),
                // so WhenAll waits for all three regardless of individual
                // outcomes and partial results are preserved.
                var checklistTask = fetchChecklists ? FetchSafeAsync(_trello.GetCardChecklistsAsync(cardId, CancellationToken.None)) : null;
                var memberTask = fetchMembers ? FetchSafeAsync(_trello.GetCardMembersAsync(cardId, CancellationToken.None)) : null;
                var attachmentTask = fetchAttachments ? FetchSafeAsync(_trello.GetCardAttachmentsAsync(cardId, CancellationToken.None)) : null;

                if (checklistTask is not null || memberTask is not null || attachmentTask is not null)
                {
                    await Task.WhenAll(
                        checklistTask ?? Task.CompletedTask,
                        memberTask ?? Task.CompletedTask,
                        attachmentTask ?? Task.CompletedTask).ConfigureAwait(false);

                    checklists = await (checklistTask ?? Task.FromResult<List<TrelloChecklist>?>(null)) ?? [];
                    members = await (memberTask ?? Task.FromResult<List<TrelloMember>?>(null)) ?? [];
                    attachments = await (attachmentTask ?? Task.FromResult<List<TrelloAttachment>?>(null)) ?? [];
                }

                _detailCache[cardId] = (checklists, members, attachments);
            }
            catch (Exception)
            {
                // Transient failure: DO NOT cache empty results - that would
                // permanently blank this card's details until a manual refresh.
                // Removing the in-progress marker lets the next GetItems()
                // (after the debounce) retry the fetch.
                _detailCache.TryRemove(cardId, out _);
            }
            finally
            {
                _detailFetchInProgress.TryRemove(cardId, out _);
                // Debounce: collapse multiple detail-fetch completions into a single
                // RaiseItemsChanged() call. Without this, N cards completing in quick
                // succession each trigger a list rebuild → selection jumps (issue #48895)
                // → intermittent Ctrl+Enter failures.
                // Guarded: the page may have been disposed while this fetch was in
                // flight - Timer.Change on a disposed timer throws ObjectDisposedException
                // from this finally block (unobserved task exception).
                try
                {
                    _detailRefreshDebounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Page disposed - the refresh is moot.
                }
            }
        });
    }

    /// <summary>
    /// Wraps a detail fetch so it always completes successfully, yielding null
    /// on failure - see EnsureCardDetailsFetched for why partial results must
    /// survive an individual sub-fetch failure.
    /// </summary>
    private static async Task<List<T>?> FetchSafeAsync<T>(Task<List<T>> fetch)
    {
        try
        {
            return await fetch.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds markdown for checklists: each checklist as a heading with [x]/[ ] task list items.
    /// Checklist/item names are markdown BY DESIGN in Trello (links, bold) -
    /// passed through raw so they render the same as in Trello.
    /// </summary>
    internal static string BuildChecklistMarkdown(List<TrelloChecklist> checklists)
    {
        if (checklists == null || checklists.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var checklist in checklists)
        {
            sb.Append(CultureInfo.InvariantCulture, $"### {checklist.Name} ({checklist.ProgressText})").AppendLine();
            if (checklist.CheckItems != null)
            {
                foreach (var item in checklist.CheckItems)
                {
                    var marker = item.IsComplete ? "[x]" : "[ ]";
                    sb.Append(CultureInfo.InvariantCulture, $"- {marker} {item.Name}").AppendLine();
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds markdown for attachments: "### Attachments" heading with one
    /// bullet per attachment. Uploads show name + size; links show their URL
    /// as the bullet text (the URL IS the name for link attachments).
    /// Markdown control characters in names (e.g. "[", "]", "(", ")") are
    /// backslash-escaped so a file named "report_(final).md" cannot inject
    /// formatting or link syntax into the rendered body.
    /// </summary>
    internal static string BuildAttachmentMarkdown(List<TrelloAttachment> attachments)
    {
        if (attachments == null || attachments.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Attachments ({attachments.Count})").AppendLine();
        foreach (var attachment in attachments)
        {
            var name = EscapeMarkdown(attachment.Name);
            var bullet = string.IsNullOrEmpty(attachment.DisplaySize)
                ? name
                : $"{name} ({attachment.DisplaySize})";
            sb.Append(CultureInfo.InvariantCulture, $"- {bullet}").AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Backslash-escapes CommonMark control characters that would otherwise
    /// be interpreted as formatting/link syntax inside the details body.
    /// Applied ONLY to plain-text fields that carry no legitimate markdown
    /// (attachment names) - content Trello itself renders as markdown (card
    /// descriptions, comments, checklist names/items) is passed through raw
    /// so links and formatting work exactly as in Trello.
    /// </summary>
    internal static string EscapeMarkdown(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`")
            .Replace("#", "\\#");

    /// <summary>
    /// Builds the right-click context menu (MoreCommands) for a card row.
    /// Verified from ContentPageViewModel.cs (PowerToys source):
    ///   Position 0 (Enter)      = Mark complete / incomplete
    ///   Position 1 (Ctrl+Enter) = Open in browser
    /// Separators do NOT count as positions for Enter/Ctrl+Enter.
    /// RequestedShortcut rules (verified against host source + #48725):
    ///   - OK for idempotent InvokableCommands (KeepOpen): Ctrl+Up/Down move,
    ///     Ctrl+R refresh - double invocation (see below) is harmless.
    ///   - NEVER for commands that navigate to a Page: the host handles
    ///     TryCommandKeybindingMessage in BOTH ContextMenu and CommandBar
    ///     (unfixed: PowerToys #48725, Needs-Triage), so a shortcut→page
    ///     action pushes the page TWICE - user needs two Esc / Save's GoBack
    ///     only pops one duplicate. This bit Ctrl+E for "Edit card…" once;
    ///     do not re-add it for any page navigation until #48725 is fixed.
    /// ShowDetails = true → detail panel opens automatically on card focus.
    /// </summary>
    private IContextItem[] BuildMoreCommands(TrelloCard card)
    {
        var cmds = new List<IContextItem>();

        // ── Position 0: Mark complete / incomplete ───────────────────
        // Uses OnCardDueCompleteChanged (not RefreshCardsLocally) so the DueComplete
        // state is synced from the command's card to the current _cards list by ID.
        // This handles the case where _cards was replaced by a background reload
        // (stale-check) after MoreCommands was cached - the command holds a stale
        // TrelloCard, but OnCardDueCompleteChanged finds the current one in _cards.
        if (!card.DueComplete)
        {
            cmds.Add(new CommandContextItem(new MarkCompleteCommand(_trello, card, () => OnCardDueCompleteChanged(card)))
            {
                Title = "Mark as complete",
            });
        }
        else
        {
            cmds.Add(new CommandContextItem(new UnmarkCompleteCommand(_trello, card, () => OnCardDueCompleteChanged(card)))
            {
                Title = "Mark as incomplete",
            });
        }

        // ── Position 1: Open in browser ─────────────────────────
        if (!string.IsNullOrEmpty(card.ShortUrl))
        {
            cmds.Add(new CommandContextItem(new OpenUrlCommand(card.ShortUrl))
            {
                Title = "Open in browser",
            });
        }

        if (!string.IsNullOrEmpty(card.ShortUrl))
        {
            cmds.Add(new CommandContextItem(new CopyTextCommand(card.ShortUrl))
            {
                Title = "Copy card URL",
            });
        }

        // ── Card editing actions ───────────────────────────────────────────────
        cmds.Add(new Separator());

        // Move up/down use OnCardPositionChanged (not ReorderCardsLocally) so the
        // pos change is synced from the command's card to the current _cards list
        // by ID. This handles the case where _cards was replaced by a background
        // reload (stale-check) after MoreCommands was cached.
        cmds.Add(new CommandContextItem(new MoveCardUpCommand(_trello, card, _cards, () => OnCardPositionChanged(card)))
        {
            Title = "Move up",
            RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.Up),
        });

        cmds.Add(new CommandContextItem(new MoveCardDownCommand(_trello, card, _cards, () => OnCardPositionChanged(card)))
        {
            Title = "Move down",
            RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.Down),
        });

        var moveCardPage = new MoveCardPage(card, _trello, _allListsOnBoard, LoadCardsInBackground);
        cmds.Add(new CommandContextItem(moveCardPage)
        {
            Title = "Move to…",
        });

        var copyCardPage = new CopyCardPage(card, _trello, _allListsOnBoard, LoadCardsInBackground);
        cmds.Add(new CommandContextItem(copyCardPage)
        {
            Title = "Copy to…",
        });

        // NO RequestedShortcut here: EditCardPage is a page navigation, and
        // shortcut→page actions double-push the page onto the nav stack
        // (unfixed host bug PowerToys #48725 - TryCommandKeybindingMessage
        // handled by both ContextMenu and CommandBar). Ctrl+E was tried and
        // reverted for exactly this reason. See BuildMoreCommands doc header.
        cmds.Add(new CommandContextItem(new EditCardPage(_trello, card, LoadCardsInBackground))
        {
            Title = "Edit card…",
        });

        cmds.Add(new CommandContextItem(new AddCommentPage(_trello, card, LoadCardsInBackground))
        {
            Title = "Add comment…",
        });

        // ── Comments - list + edit/delete ────────────────────────────────────
        // Opens CardCommentsPage (GET /1/cards/{id}/actions?filter=commentCard),
        // which shows every comment with a details pane and per-comment
        // Edit…/Delete actions (PUT/DELETE /1/actions/{id}).
        cmds.Add(new CommandContextItem(new CardCommentsPage(_trello, card))
        {
            Title = "Show comments",
        });

        // ── Destructive ──────────────────────────────────────────────────────
        cmds.Add(new Separator());

        // OnCardArchived (not LoadCardsInBackground): archives the SELECTED
        // card, so selection would be lost when the item disappears. The
        // handler re-keys the archived card's ListItem under the previous
        // card's ID before reloading - the host's sticky-selection restore
        // then lands the selection on the card above instead of falling to the first row.
        cmds.Add(new CommandContextItem(new ArchiveCardCommand(_trello, card, () => OnCardArchived(card)))
        {
            Title = "Archive card",
            IsCritical = true,
        });

        // ── List-level action ───────────────────────────────────────────────
        // Refresh is a page-level concern (not card-specific), but Command
        // Palette only exposes the selected item's MoreCommands in the Ctrl+K
        // menu - pages have no MoreCommands of their own (verified: Page/Command
        // base classes only define Icon, Id, Name). Adding it here makes the
        // action available regardless of which card is selected.
        cmds.Add(new Separator());

        cmds.Add(new CommandContextItem(new AnonymousCommand(ManualRefresh)
        {
            Name = "Refresh",
            Result = CommandResult.KeepOpen(),
        })
        {
            Title = "Refresh list",
            Icon = new IconInfo("\uE72C"), // Refresh
            RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.R),
        });

        return [.. cmds];
    }

    /// <summary>
    /// Fetches cards from GET /1/lists/{listId}/cards in a background task.
    /// </summary>
    public void LoadCardsInBackground()
    {
        IsLoading = true;
        _errorMessage = string.Empty;

        Task.Run(async () =>
        {
            try
            {
                var loaded = await _trello.GetCardsAsync(_list.Id, CancellationToken.None).ConfigureAwait(false);
                // Sort by pos ascending so cards display in the correct order (top → bottom).
                _cards = loaded.OrderBy(c => c.Pos).ToList();
                _lastLoadTime = DateTime.UtcNow;

                // Prune stale ListItem cache entries for cards no longer in the list
                // (moved away, archived, or deleted). Keeps the cache from growing
                // unbounded and ensures the host doesn't hold references to dead items.
                var currentCardIds = new HashSet<string>(loaded.Select(c => c.Id));
                foreach (var staleId in _listItemCache.Keys.Where(id => !currentCardIds.Contains(id)))
                {
                    _listItemCache.TryRemove(staleId, out _);
                    _cardDueCompleteState.TryRemove(staleId, out _);
                    _cardNameState.TryRemove(staleId, out _);
                    _cardDescState.TryRemove(staleId, out _);
                }
            }
            catch (Exception ex)
            {
                _cards = [];
                _errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
                // -2 = IncrementalRefresh → keepSelection=true.
                // After a card move/copy/mark-complete, preserving selection keeps
                // the focused card's SecondaryCommand (Ctrl+Enter) active instead
                // of forcing selection to the first row (PowerToys #48895).
                RaiseItemsChanged(-2);
            }
        });
    }

    /// <summary>
    /// Callback for the "＋ New card" quick-add (CreateCardCommand). Clears the
    /// search box, then reloads the cards so the freshly created card appears
    /// in the full (unfiltered) list - standard quick-add UX: submit → clear
    /// input → refreshed list + success toast.
    /// </summary>
    private void OnCardCreated()
    {
        ClearSearch();
        LoadCardsInBackground();
    }

    /// <summary>
    /// Clears the search text and syncs the host's search box.
    ///
    /// Verified from PowerToys source - the DynamicListPage.SearchText setter
    /// (Toolkit DynamicListPage.cs) only calls SetSearchNoUpdate + UpdateSearchText;
    /// it does NOT raise PropChanged, so the host's search box would keep the old text.
    /// OnPropertyChanged is protected on BaseObservable - callable from this subclass.
    /// </summary>
    private void ClearSearch()
    {
        SearchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
    }

    /// <summary>
    /// Manual refresh: clears the detail cache (checklists/members) so stale
    /// data is replaced, then reloads cards from the Trello API. Called by the
    /// "Refresh list" action in each card's MoreCommands (Ctrl+K) menu.
    /// </summary>
    private void ManualRefresh()
    {
        _detailCache.Clear();
        _detailFetchInProgress.Clear();
        LoadCardsInBackground();
    }

    /// <summary>
    /// Reorders the local card list after a move-up/down operation WITHOUT
    /// reloading from the API. Preserves the same TrelloCard objects → same
    /// ListItem instances → same MoreCommands → SecondaryCommand stays
    /// initialized → Ctrl+Enter works immediately after the move.
    /// </summary>
    public void ReorderCardsLocally()
    {
        // Re-sort the existing _cards list by pos (already updated by the move
        // command via the API). No new TrelloCard objects are created.
        _cards = _cards.OrderBy(c => c.Pos).ToList();

        // Update _lastLoadTime so the stale-reload check in GetItems() doesn't
        // trigger a full API reload after this local reorder. Without this, after
        // 30s the stale check would fire LoadCardsInBackground() → IsLoading=true
        // → new TrelloCard objects → new ListItem instances → MoreCommands rebuilt
        // → SecondaryCommand null → Ctrl+Enter fails.
        _lastLoadTime = DateTime.UtcNow;
        // -2 = IncrementalRefresh → keepSelection=true. Selection stays on the
        // moved card; the host finds the existing ListItemViewModel in _vmCache
        // (same COM identity) → SecondaryCommand is already initialized.
        RaiseItemsChanged(-2);
    }

    /// <summary>
    /// Callback for MoveCardUpCommand / MoveCardDownCommand. Syncs the pos
    /// change from the command's card object to the matching card in _cards
    /// (by ID), then reorders locally.
    ///
    /// Same rationale as OnCardDueCompleteChanged: MoreCommands may be cached,
    /// so the TrelloCard held by the move command can become stale after a
    /// background reload. Without this sync, the command updates the old card's
    /// pos while ReorderCardsLocally sorts the current _cards where pos is unchanged.
    /// </summary>
    public void OnCardPositionChanged(TrelloCard updatedCard)
    {
        // Find the card in _cards by ID and sync the pos.
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Id == updatedCard.Id)
            {
                _cards[i].Pos = updatedCard.Pos;
                break;
            }
        }

        ReorderCardsLocally();
    }

    /// <summary>
    /// Called by ArchiveCardCommand AFTER the archive succeeded. The archived
    /// card is (almost always) the SELECTED row; when the reload removes its
    /// item, the host's selection repair falls back to the first row and
    /// scrolls to top (verified: ListItemsView.xaml.cs - sticky restore only
    /// succeeds when the same ListItemViewModel is present in the new items).
    ///
    /// Fix: re-key the archived card's ListItem under the PREVIOUS card's ID
    /// before reloading. The host's ListViewModel reuses ListItemViewModels by
    /// IListItem COM-object identity (verified: _vmCache, ProxyReferenceComparer),
    /// so when GetItems() repaints that same instance with the previous card's
    /// data, the selected VM survives the reload → the host's sticky-restore
    /// keeps the selection on the card above the archived one and scrolls it
    /// into view. Same-instance repaint is exactly how move-up/down already
    /// preserves selection today.
    /// </summary>
    public void OnCardArchived(TrelloCard archivedCard)
    {
        // Called on a background thread (ArchiveCardCommand.Task.Run) BEFORE
        // LoadCardsInBackground replaces _cards - find neighbors in the
        // current order. _listItemCache is a ConcurrentDictionary.
        var index = -1;
        for (var i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Id == archivedCard.Id)
            {
                index = i;
                break;
            }
        }

        // Archived card not in this list (e.g. stale MoreCommands after a
        // reload swapped cards) - plain reload, host falls to first row.
        if (index <= 0)
        {
            // index == 0: archiving the FIRST card - there is no previous
            // card to hand selection to; the host's first-row fallback is the
            // correct behavior (the next card becomes the new first row).
            // index == -1: card no longer present - same fallback.
            LoadCardsInBackground();
            return;
        }

        var prevCard = _cards[index - 1];

        // Take the archived card's ListItem out of the cache. This instance is
        // the row the host currently has selected (the archived card was
        // selected when its context command ran).
        if (!_listItemCache.TryRemove(archivedCard.Id, out var archivedListItem))
        {
            // No cached instance (never rendered) - nothing to re-key.
            LoadCardsInBackground();
            return;
        }

        // Remove the previous card's own entry, then re-key the ARCHIVED
        // card's (still-selected) instance under the previous card's ID.
        // GetItems() will GetOrAdd on prevCard.Id → gets archivedListItem →
        // repaints it with prevCard's data. The host's ListViewModel._vmCache
        // reuses the ListItemViewModel for the same IListItem COM identity
        // (verified) → the selected VM survives the reload → sticky-restore
        // lands the selection on the card above the archived one.
        _listItemCache.TryRemove(prevCard.Id, out _);
        _listItemCache[prevCard.Id] = archivedListItem;

        // Clear the prev card's cached MoreCommands-rebuild state so the row
        // fully repaints (including its context menu) with prevCard's data -
        // the guard in GetItems() compares against these dictionaries.
        _cardDueCompleteState.TryRemove(prevCard.Id, out _);
        _cardNameState.TryRemove(prevCard.Id, out _);
        _cardDescState.TryRemove(prevCard.Id, out _);

        LoadCardsInBackground();
    }

    /// <summary>
    /// Refreshes the list after a local state change (e.g., mark/unmark complete)
    /// WITHOUT reloading from the API. Preserves the same TrelloCard objects and
    /// ListItem instances. The MoreCommands guard in GetItems() will detect the
    /// DueComplete change and rebuild MoreCommands only for the affected card.
    /// </summary>
    public void RefreshCardsLocally()
    {
        // Update _lastLoadTime so the stale-reload check in GetItems() doesn't
        // trigger a full API reload after this local refresh. Without this, after
        // 30s of repeated mark/unmark complete, the stale check would fire
        // LoadCardsInBackground() → IsLoading=true (loading bar appears) → new
        // TrelloCard objects → new ListItem instances → MoreCommands rebuilt →
        // SecondaryCommand null → Ctrl+Enter fails for several seconds.
        _lastLoadTime = DateTime.UtcNow;
        // -2 = IncrementalRefresh → keepSelection=true. The host reuses existing
        // ListItemViewModels (same COM identity) → SecondaryCommand stays
        // initialized → Ctrl+Enter works after the state change.
        RaiseItemsChanged(-2);
    }

    /// <summary>
    /// Callback for MarkCompleteCommand / UnmarkCompleteCommand. Syncs the
    /// DueComplete state from the command's card object to the matching card in
    /// _cards (by ID), then refreshes locally.
    ///
    /// This is necessary because MoreCommands may be cached (not rebuilt on every
    /// GetItems() call), so the TrelloCard held by the command can become stale
    /// after a background reload (stale-check at 30s replaces _cards with fresh
    /// objects). Without this sync, the command modifies the old card while
    /// GetItems() iterates over the new _cards where DueComplete is unchanged.
    /// </summary>
    public void OnCardDueCompleteChanged(TrelloCard updatedCard)
    {
        // Find the card in _cards by ID and sync the DueComplete state.
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Id == updatedCard.Id)
            {
                _cards[i].DueComplete = updatedCard.DueComplete;
                break;
            }
        }

        RefreshCardsLocally();
    }

    // ── Separator helpers ────────────────────────────────────────────────────

    private static bool IsSeparatorCard(string name)
    {
        var trimmed = name.AsSpan().Trim();
        if (trimmed.IsEmpty) return false;
        int i = 0;
        while (i < trimmed.Length && (trimmed[i] == '-' || trimmed[i] == '\u2014'))
        {
            i++;
        }
        return i >= 2;
    }

    private static string ExtractSeparatorTitle(string name)
    {
        var span = name.AsSpan().Trim();
        int i = 0;
        while (i < span.Length && (span[i] == '-' || span[i] == '\u2014'))
        {
            i++;
        }
        return span[i..].Trim().ToString();
    }

    // ── Tag building ─────────────────────────────────────────────────────────

    private static Tag[] BuildTags(TrelloCard card, int maxVisible)
    {
        if (card.Labels == null || card.Labels.Count == 0) return [];

        // Read the effective theme once per BuildTags call so all tags in this
        // batch use the same palette. TrelloSettingsManager.IsDarkTheme resolves
        // the override setting (auto/light/dark) → final boolean.
        var isDarkTheme = TrelloSettingsManager.Instance.IsDarkTheme;

        var result = new List<Tag>();
        var visible = Math.Min(card.Labels.Count, maxVisible);

        for (int i = 0; i < visible; i++)
        {
            var label = card.Labels[i];
            var tag = new Tag(label.DisplayText);

            if (!string.IsNullOrEmpty(label.Color))
            {
                var (r, g, b) = TrelloColorToRgb(label.Color, isDarkTheme);
                tag.Background = new OptionalColor { HasValue = true, Color = new WinColor { A = 255, R = r, G = g, B = b } };
                byte fg = IsLightColor(label.Color, isDarkTheme) ? (byte)0x10 : (byte)0xFF;
                tag.Foreground = new OptionalColor { HasValue = true, Color = new WinColor { A = 255, R = fg, G = fg, B = fg } };
            }

            result.Add(tag);
        }

        int overflow = card.Labels.Count - visible;
        if (overflow > 0) result.Add(new Tag($"+{overflow} more"));

        return [.. result];
    }

    // ── Accent token mapping ──────────────────────────────────────────
    //
    // Trello label color names map to accent hues:
    //   sky   → teal accent tokens
    //   pink  → magenta accent tokens
    //   black → gray accent tokens
    //   lime  → lime accent tokens
    //
    // Emphasis mapping (Trello suffix → emphasis level):
    //   normal (no suffix) → .subtle   - the "standard" label color
    //   _dark              → .bolder   - the darker/stronger variant
    //   _light             → .subtler  - the lighter/softer variant
    //
    // In dark theme, the emphasis levels are semantically inverted:
    //   .bolder = bright (more contrast against dark background)
    //   .subtle = medium-dark
    //   .subtler = darkest (least contrast)
    // This means dark-theme ".bolder" colors are lighter than light-theme ".bolder".
    //
    // ── Light theme ─────────────────
    private static (byte R, byte G, byte B) TrelloColorToRgbLight(string color) => color switch
    {
        // ── normal (subtle) ─────────────────────────────────────────────────
        "green"  => (0x4B, 0xCE, 0x97), // background.accent.green.subtle   #4BCE97
        "yellow" => (0xE2, 0xB2, 0x03), // background.accent.yellow.subtle  #E2B203
        "orange" => (0xFA, 0xA5, 0x3D), // background.accent.orange.subtle  #FAA53D
        "red"    => (0xF8, 0x74, 0x62), // background.accent.red.subtle     #F87462
        "purple" => (0x9F, 0x8F, 0xEF), // background.accent.purple.subtle  #9F8FEF
        "blue"   => (0x57, 0x9D, 0xFF), // background.accent.blue.subtle    #579DFF
        "sky"    => (0x60, 0xC6, 0xD2), // background.accent.teal.subtle    #60C6D2
        "lime"   => (0x94, 0xC7, 0x48), // background.accent.lime.subtle    #94C748
        "pink"   => (0xE7, 0x74, 0xBB), // background.accent.magenta.subtle #E774BB
        "black"  => (0x85, 0x90, 0xA2), // background.accent.gray.subtle    #8590A2

        // ── dark (bolder) ───────────────────────────────────────────────────
        "green_dark"  => (0x1F, 0x84, 0x5A), // background.accent.green.bolder   #1F845A
        "yellow_dark" => (0x94, 0x6F, 0x00), // background.accent.yellow.bolder  #946F00
        "orange_dark" => (0xB6, 0x5C, 0x02), // background.accent.orange.bolder  #B65C02
        "red_dark"    => (0xCA, 0x35, 0x21), // background.accent.red.bolder     #CA3521
        "purple_dark" => (0x6E, 0x5D, 0xC6), // background.accent.purple.bolder  #6E5DC6
        "blue_dark"   => (0x0C, 0x66, 0xE4), // background.accent.blue.bolder    #0C66E4
        "sky_dark"    => (0x1D, 0x7F, 0x8C), // background.accent.teal.bolder    #1D7F8C
        "lime_dark"   => (0x4C, 0x6B, 0x1F), // background.accent.lime.bolder    #4C6B1F
        "pink_dark"   => (0xAE, 0x47, 0x87), // background.accent.magenta.bolder #AE4787
        "black_dark"  => (0x44, 0x54, 0x6F), // background.accent.gray.bolder    #44546F

        // ── light (subtler) ─────────────────────────────────────────────────
        "green_light"  => (0xBA, 0xF3, 0xDB), // background.accent.green.subtler   #BAF3DB
        "yellow_light" => (0xF8, 0xE6, 0xA0), // background.accent.yellow.subtler  #F8E6A0
        "orange_light" => (0xFF, 0xE2, 0xBD), // background.accent.orange.subtler  #FFE2BD
        "red_light"    => (0xFF, 0xD2, 0xCC), // background.accent.red.subtler     #FFD2CC
        "purple_light" => (0xDF, 0xD8, 0xFD), // background.accent.purple.subtler  #DFD8FD
        "blue_light"   => (0xCC, 0xE0, 0xFF), // background.accent.blue.subtler    #CCE0FF
        "sky_light"    => (0xC1, 0xF0, 0xF5), // background.accent.teal.subtler    #C1F0F5
        "lime_light"   => (0xD3, 0xF1, 0xA7), // background.accent.lime.subtler    #D3F1A7
        "pink_light"   => (0xFD, 0xD0, 0xEC), // background.accent.magenta.subtler #FDD0EC
        "black_light"  => (0xDC, 0xDF, 0xE4), // background.accent.gray.subtler    #DCDFE4

        _              => (0x97, 0xA0, 0xAF),
    };

    // ── Dark theme ──
    //
    // Dark-theme emphasis is inverted: .bolder = bright, .subtle = dark, .subtler = darkest.
    // The _dark suffix still maps to .bolder (brightest in dark theme),
    // _light still maps to .subtler (darkest in dark theme).
    private static (byte R, byte G, byte B) TrelloColorToRgbDark(string color) => color switch
    {
        // ── normal (subtle) - dark-theme medium-dark values ────────────────
        "green"  => (0x21, 0x6E, 0x4E), // background.accent.green.subtle   #216E4E
        "yellow" => (0x7F, 0x5F, 0x01), // background.accent.yellow.subtle  #7F5F01
        "orange" => (0x9E, 0x4C, 0x00), // background.accent.orange.subtle  #9E4C00
        "red"    => (0xAE, 0x2E, 0x24), // background.accent.red.subtle     #AE2E24
        "purple" => (0x80, 0x3F, 0xA5), // background.accent.purple.subtle  #803FA5
        "blue"   => (0x15, 0x58, 0xBC), // background.accent.blue.subtle    #1558BC
        "sky"    => (0x20, 0x6A, 0x83), // background.accent.teal.subtle    #206A83
        "lime"   => (0x4C, 0x6B, 0x1F), // background.accent.lime.subtle    #4C6B1F
        "pink"   => (0x94, 0x3D, 0x73), // background.accent.magenta.subtle #943D73
        "black"  => (0x63, 0x66, 0x6B), // background.accent.gray.subtle    #63666B

        // ── dark (bolder) - dark-theme bright values ────────────────────────
        "green_dark"  => (0x4B, 0xCE, 0x97), // background.accent.green.bolder   #4BCE97
        "yellow_dark" => (0xDD, 0xB3, 0x0E), // background.accent.yellow.bolder  #DDB30E
        "orange_dark" => (0xFC, 0xA7, 0x00), // background.accent.orange.bolder  #FCA700
        "red_dark"    => (0xF8, 0x71, 0x68), // background.accent.red.bolder     #F87168
        "purple_dark" => (0xC9, 0x7C, 0xF4), // background.accent.purple.bolder  #C97CF4
        "blue_dark"   => (0x66, 0x9D, 0xF1), // background.accent.blue.bolder    #669DF1
        "sky_dark"    => (0x6C, 0xC3, 0xE0), // background.accent.teal.bolder    #6CC3E0
        "lime_dark"   => (0x94, 0xC7, 0x48), // background.accent.lime.bolder    #94C748
        "pink_dark"   => (0xE7, 0x74, 0xBB), // background.accent.magenta.bolder #E774BB
        "black_dark"  => (0x96, 0x99, 0x9E), // background.accent.gray.bolder    #96999E

        // ── light (subtler) - dark-theme darkest values ─────────────────────
        "green_light"  => (0x16, 0x4B, 0x35), // background.accent.green.subtler   #164B35
        "yellow_light" => (0x53, 0x3F, 0x04), // background.accent.yellow.subtler  #533F04
        "orange_light" => (0x69, 0x32, 0x00), // background.accent.orange.subtler  #693200
        "red_light"    => (0x5D, 0x1F, 0x1A), // background.accent.red.subtler     #5D1F1A
        "purple_light" => (0x48, 0x24, 0x5D), // background.accent.purple.subtler  #48245D
        "blue_light"   => (0x12, 0x32, 0x63), // background.accent.blue.subtler    #123263
        "sky_light"    => (0x16, 0x45, 0x55), // background.accent.teal.subtler    #164555
        "lime_light"   => (0x37, 0x47, 0x1F), // background.accent.lime.subtler    #37471F
        "pink_light"   => (0x50, 0x25, 0x3F), // background.accent.magenta.subtler #50253F
        "black_light"  => (0x4B, 0x4D, 0x51), // background.accent.gray.subtler    #4B4D51

        _              => (0x97, 0xA0, 0xAF),
    };

    /// <summary>
    /// Maps a Trello label color name to an RGB tuple using the appropriate theme palette .
    /// </summary>
    private static (byte R, byte G, byte B) TrelloColorToRgb(string color, bool isDarkTheme) =>
        isDarkTheme ? TrelloColorToRgbDark(color) : TrelloColorToRgbLight(color);

    // ── Foreground (text) color logic ──────────────────────────────────────────
    //
    // LIGHT THEME:
    //   _dark (bolder) backgrounds are dark → white text (0xFF)
    //   everything else (normal/subtle, _light/subtler) → dark text (0x10)
    //
    // DARK THEME:
    //   _dark (bolder) backgrounds are bright → dark text (0x10)
    //   everything else (normal/subtle, _light/subtler) → white text (0xFF)
    //
    // In both themes, ONLY the bolder (_dark) variant flips the text color.
    private static bool IsLightColor(string color, bool isDarkTheme)
    {
        if (isDarkTheme)
        {
            return color.EndsWith("_dark", StringComparison.Ordinal);
        }
        else
        {
            return !color.EndsWith("_dark", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Callback for CardFilters (host pushed a new CurrentFilterId via the
    /// FiltersDropDown). Re-renders the list with the filter applied -
    /// -2 = IncrementalRefresh → keepSelection=true so the focused card keeps
    /// its SecondaryCommand (Ctrl+Enter) active. No API call needed: the filter
    /// is applied client-side in GetItems() over the already-loaded _cards.
    /// </summary>
    private void OnFilterChanged()
    {
        // Construction-time callback guard (CardFilters defaults CurrentFilterId
        // in its ctor → one PropChanged fires before the page has any items):
        // GetItems() re-render would be a harmless no-op, but guarding here
        // keeps the callback strictly a user-driven refresh.
        if (_lastLoadTime == DateTime.MinValue && _cards.Count == 0)
        {
            return;
        }

        RaiseItemsChanged(-2);
    }

    /// <summary>
    /// Disposes the debounce timer. Called by the host when the page is cleaned up.
    /// </summary>
    public void Dispose()
    {
        _detailRefreshDebounce.Dispose();
        GC.SuppressFinalize(this);
    }
}
