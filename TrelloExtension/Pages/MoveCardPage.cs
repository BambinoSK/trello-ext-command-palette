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
/// Sub-page shown when the user selects "Move to…" on a card.
/// Lists all available lists on the same board (excluding the card's current list).
/// Selecting a list executes MoveCardCommand immediately.
///
/// When constructed from a pinned CardsPage (via GetCommandItem), allListsOnBoard is empty.
/// In that case, lazy-loads the board's lists from GET /1/boards/{boardId}/lists using
/// TrelloCard.IdBoard. Uses DynamicListPage so IsLoading/RaiseItemsChanged work correctly.
/// </summary>
internal sealed partial class MoveCardPage : DynamicListPage
{
    private readonly TrelloCard _card;
    private readonly TrelloService _trello;
    private readonly IReadOnlyList<TrelloList> _allListsPreloaded;
    private readonly Action _onMoved;

    // Lazily fetched when _allListsPreloaded is empty (pinned CardsPage scenario)
    private IReadOnlyList<TrelloList> _fetchedLists = [];
    private string _errorMessage = string.Empty;
    private bool _listsLoadStarted;

    public MoveCardPage(TrelloCard card, TrelloService trello, IReadOnlyList<TrelloList> allListsPreloaded, Action onMoved)
    {
        _card = card;
        _trello = trello;
        _allListsPreloaded = allListsPreloaded;
        _onMoved = onMoved;

        Icon = new IconInfo("\uE8AB"); // MoveToFolder
        Title = $"Move \"{card.Name}\"";
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
            // Skip the list the card is already in.
            if (list.Id == _card.IdList) continue;

            items.Add(new ListItem(new MoveCardCommand(_trello, _card, list, _onMoved))
            {
                Title = list.Name,
                Icon = new IconInfo("\uE8AB"), // MoveToFolder
            });
        }

        if (items.Count == 0 && !IsLoading)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "No other lists available",
                Subtitle = "This board only has one list",
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
