using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// Search page for finding cards across all lists on a board.
/// Uses GET /1/search?query=…&modelTypes=cards&idBoards={boardId} to scope
/// the search to the current board.
///
/// The page is a DynamicListPage - it calls the Trello search API in the
/// background when the user types, debounced by the host's search-text update.
/// Results show card name + due date subtitle. Selecting a card opens it
/// in the browser (OpenUrlCommand).
///
/// The Trello search API returns both open and archived cards. This page
/// filters client-side based on the searchArchived parameter:
///   false → only open (non-archived) cards
///   true  → only archived (closed) cards
/// </summary>
internal sealed partial class SearchCardsPage : DynamicListPage, IDisposable
{
    private readonly TrelloService _trello;
    private readonly TrelloBoard _board;
    private readonly bool _searchArchived;
    private List<TrelloCard> _results = [];
    private string _errorMessage = string.Empty;
    private CancellationTokenSource? _searchCts;
    // Set by Dispose; guards UpdateSearchText and the running search task from
    // touching a disposed CTS (the host may still deliver search-text updates
    // briefly after the page is dropped).
    private volatile bool _disposed;

    private static readonly IconInfo TrelloIcon = IconHelpers.FromRelativePaths(@"Assets\StoreLogo.png", @"Assets\StoreLogo.png");

    public SearchCardsPage(TrelloService trello, TrelloBoard board, bool searchArchived = false)
    {
        _trello = trello;
        _board = board;
        _searchArchived = searchArchived;

        Icon = TrelloIcon;
        Title = searchArchived
            ? $"Search archived cards on \"{board.Name}\""
            : $"Search cards on \"{board.Name}\"";
        Name = searchArchived ? "Search archived" : "Search cards";
        PlaceholderText = searchArchived
            ? "Type to search archived cards on this board…"
            : "Type to search cards on this board…";
        ShowDetails = false;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        // Cancel any in-flight search and start a new one. _disposed guards the
        // window where Dispose() has already cancelled-and-disposed the CTS but
        // this host-driven callback is still queued: calling .Cancel() on a
        // disposed CTS throws ObjectDisposedException.
        if (_disposed)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        var search = newSearch?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(search))
        {
            _results = [];
            _errorMessage = string.Empty;
            IsLoading = false;
            RaiseItemsChanged();
            return;
        }

        IsLoading = true;
        var ct = _searchCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // Small delay to debounce rapid typing - avoids hammering the API.
                await Task.Delay(200, ct).ConfigureAwait(false);
                var cards = await _trello.SearchCardsOnBoardAsync(search, _board.Id, ct).ConfigureAwait(false);
                // Filter client-side: the Trello search API returns both open and
                // archived cards. Filter based on the _searchArchived flag.
                _results = _searchArchived
                    ? cards.Where(c => c.Closed).ToList()
                    : cards.Where(c => !c.Closed).ToList();
                _errorMessage = string.Empty;
            }
            catch (OperationCanceledException)
            {
                // Search was superseded by a newer one - do nothing.
                return;
            }
            catch (Exception ex)
            {
                _results = [];
                _errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    IsLoading = false;
                    RaiseItemsChanged();
                }
            }
        }, ct);
    }

    public override IListItem[] GetItems()
    {
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Search failed",
                    Subtitle = _errorMessage,
                    Icon = new IconInfo("\uE783"), // Error icon
                },
            ];
        }

        if (_results.Count == 0 && !IsLoading)
        {
            var search = SearchText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(search))
            {
                return
                [
                    new ListItem(new NoOpCommand())
                    {
                        Title = "Start typing to search",
                        Subtitle = _searchArchived
                            ? $"Search archived cards across all lists on \"{_board.Name}\""
                            : $"Search across all lists on \"{_board.Name}\"",
                        Icon = new IconInfo("\uE721"), // Search icon
                    },
                ];
            }

            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "No cards found",
                    Subtitle = _searchArchived
                        ? $"No archived cards match \"{search}\" on this board"
                        : $"No cards match \"{search}\" on this board",
                    Icon = new IconInfo("\uE721"),
                },
            ];
        }

        var items = new List<IListItem>();

        foreach (var card in _results)
        {
            // Archived cards get a distinct icon (Delete/Archive) regardless of due state.
            var icon = _searchArchived
                ? new IconInfo("\uE74D") // Delete/Archive icon
                : card.DueComplete
                    ? new IconInfo("\uE73A") // CheckboxComposite - due date completed
                    : card.IsOverdue
                        ? new IconInfo("\uE814") // Important - overdue
                        : new IconInfo("\uE739"); // Checkbox - normal card

            var subtitle = string.IsNullOrEmpty(card.DueDisplay)
                ? string.Empty
                : card.DueDisplay;

            // Primary action: open card in browser.
            // NoOpCommand is used for cards without a ShortUrl (shouldn't happen
            // with search results, but defensive).
            ICommand command = !string.IsNullOrEmpty(card.ShortUrl)
                ? new OpenUrlCommand(card.ShortUrl)
                : new NoOpCommand();

            items.Add(new ListItem(command)
            {
                Title = card.Name,
                Subtitle = subtitle,
                Icon = icon,
            });
        }

        return [.. items];
    }

    /// <summary>
    /// Disposes the cancellation token source. Called by the host when the page is cleaned up.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
