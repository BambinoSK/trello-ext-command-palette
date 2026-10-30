using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Commands;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// Sub-page shown when the user selects "Copy to…" on a card.
/// Lists all available lists on the same board (including the card's current list,
/// since copying to the same list creates a duplicate - which is valid for copy).
/// Selecting a list executes CopyCardCommand immediately.
///
/// When constructed from a pinned CardsPage (via GetCommandItem), allListsOnBoard is empty.
/// In that case, lazy-loads the board's lists from GET /1/boards/{boardId}/lists using
/// TrelloCard.IdBoard. Uses DynamicListPage so IsLoading/RaiseItemsChanged work correctly.
/// </summary>
internal sealed partial class CopyCardPage : DynamicListPage
{
    private readonly TrelloCard _card;
    private readonly TrelloService _trello;
    private readonly IReadOnlyList<TrelloList> _allListsPreloaded;
    private readonly Action _onCopied;

    // Lazily fetched when _allListsPreloaded is empty (pinned CardsPage scenario)
    private IReadOnlyList<TrelloList> _fetchedLists = [];
    private string _errorMessage = string.Empty;
    private bool _listsLoadStarted;

    public CopyCardPage(TrelloCard card, TrelloService trello, IReadOnlyList<TrelloList> allListsPreloaded, Action onCopied)
    {
        _card = card;
        _trello = trello;
        _allListsPreloaded = allListsPreloaded;
        _onCopied = onCopied;

        Icon = new IconInfo("\uE8C8"); // Copy
        Title = $"Copy \"{card.Name}\"";
        Name = "Open";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override IListItem[] GetItems()
    {
        // Lazy-load lists on first GetItems() call (not in constructor) to avoid
        // making API calls when the page is merely constructed as a context menu
        // entry (e.g. when MoreCommands is rebuilt after mark-complete on a pinned page).
        if (!_listsLoadStarted && _allListsPreloaded.Count == 0 && !string.IsNullOrEmpty(_card.IdBoard))
        {
            _listsLoadStarted = true;
            LoadListsInBackground();
        }

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Failed to load lists",
                    Subtitle = _errorMessage,
                    Icon = new IconInfo("\uE783"),
                },
            ];
        }

        // Use pre-populated list if available; otherwise use lazily fetched list.
        var source = _allListsPreloaded.Count > 0 ? _allListsPreloaded : _fetchedLists;
        var items = new List<IListItem>();

        foreach (var list in source)
        {
            // Copy to the same list is valid (creates a duplicate) - do NOT skip the current list.
            items.Add(new ListItem(new CopyCardCommand(_trello, _card, list, _onCopied))
            {
                Title = list.Name,
                // Indicate when copying to the same list (creates a duplicate).
                Subtitle = list.Id == _card.IdList ? "Same list (creates a duplicate)" : string.Empty,
                Icon = new IconInfo("\uE8C8"), // Copy
            });
        }

        if (items.Count == 0 && !IsLoading)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "No lists available",
                Subtitle = "This board has no lists",
                Icon = new IconInfo("\uE8FD"), // BulletedList
            });
        }

        return [.. items];
    }

    /// <summary>
    /// Fetches all lists for the card's board from GET /1/boards/{boardId}/lists.
    /// Only called when allListsPreloaded is empty (pinned CardsPage scenario).
    /// </summary>
    private void LoadListsInBackground()
    {
        IsLoading = true;
        _errorMessage = string.Empty;

        Task.Run(async () =>
        {
            try
            {
                _fetchedLists = await _trello.GetListsAsync(_card.IdBoard, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _fetchedLists = [];
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
