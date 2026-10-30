using System.Collections.Generic;
using TrelloExtension.Commands;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System;

namespace TrelloExtension.Pages;

/// <summary>
/// Per-card action menu.
/// Actions: Open in browser | Copy URL | Copy to… (sub-page) | Move up | Move down |
///          Move to… (sub-page) | Mark complete | Archive
/// Navigated to from CardsPage when a card is selected.
/// "Copy to…" navigates to CopyCardPage which lists all lists on the board (including current).
/// "Move to…" navigates to MoveCardPage which lists all other lists on the board.
/// "Move up"/"Move down" reposition the card within its current list using fractional pos.
/// </summary>
internal sealed partial class CardActionsPage : ListPage
{
    private readonly TrelloCard _card;
    private readonly TrelloService _trello;
    private readonly IReadOnlyList<TrelloList> _allLists;
    private readonly IReadOnlyList<TrelloCard> _orderedCards;
    private readonly System.Action _onActionCompleted;

    public CardActionsPage(TrelloCard card, TrelloService trello, IReadOnlyList<TrelloList> allLists, IReadOnlyList<TrelloCard> orderedCards, System.Action onActionCompleted)
    {
        _card = card;
        _trello = trello;
        _allLists = allLists;
        _orderedCards = orderedCards;
        _onActionCompleted = onActionCompleted;

        Icon = new IconInfo("\uE8A1"); // PreviewLink
        Title = card.Name;
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();

        // ── Complete / Incomplete toggle ─────────────────────────────────────
        // "Mark as complete"   → shown when card is NOT yet complete
        // "Mark as incomplete" → shown when card IS already complete
        if (!_card.DueComplete)
        {
            var subtitle = string.IsNullOrEmpty(_card.DueDisplay)
                ? "Mark this card as done"
                : _card.DueDisplay;
            items.Add(new ListItem(new MarkCompleteCommand(_trello, _card, _onActionCompleted))
            {
                Title = "Mark as complete",
                Subtitle = subtitle,
                Icon = new IconInfo("\uE73A"), // CheckboxComposite
            });
        }
        else
        {
            items.Add(new ListItem(new UnmarkCompleteCommand(_trello, _card, _onActionCompleted))
            {
                Title = "Mark as incomplete",
                Subtitle = "Remove the completed mark from this card",
                Icon = new IconInfo("\uE739"), // Checkbox (unchecked)
            });
        }

        // ── Edit card - navigates to EditCardPage (Adaptive Card form with title + description, single PUT) ──
        var editCardPage = new EditCardPage(_trello, _card, _onActionCompleted);
        items.Add(new ListItem(editCardPage)
        {
            Title = "Edit card",
            Subtitle = "Edit title and description",
            Icon = new IconInfo("\uE8AC"), // Rename
        });

        // ── Open in browser ──────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_card.ShortUrl))
        {
            items.Add(new ListItem(new OpenUrlCommand(_card.ShortUrl))
            {
                Title = "Open in browser",
                Subtitle = _card.ShortUrl,
                Icon = new IconInfo("\uE774"), // Globe
            });
        }

        // ── Copy card URL to clipboard ───────────────────────────────────────
        if (!string.IsNullOrEmpty(_card.ShortUrl))
        {
            items.Add(new ListItem(new CopyTextCommand(_card.ShortUrl))
            {
                Title = "Copy card URL",
                Subtitle = _card.ShortUrl,
                Icon = new IconInfo("\uE8C8"), // Copy
            });
        }

        // ── Move up / Move down - reposition within current list ─────────────
        // Verified: Trello uses fractional pos indexing; midpoint inserts between adjacent cards.
        // _orderedCards is sorted by pos ascending (top → bottom).
        items.Add(new ListItem(new MoveCardUpCommand(_trello, _card, _orderedCards, _onActionCompleted))
        {
            Title = "Move up",
            Subtitle = "Move this card toward the top of the list",
            Icon = new IconInfo("\uE74A"), // Up
        });

        items.Add(new ListItem(new MoveCardDownCommand(_trello, _card, _orderedCards, _onActionCompleted))
        {
            Title = "Move down",
            Subtitle = "Move this card toward the bottom of the list",
            Icon = new IconInfo("\uE74B"), // Down
        });

        // ── Copy to… - navigates to CopyCardPage which lists all lists (including current) ──
        var copyCardPage = new CopyCardPage(_card, _trello, _allLists, _onActionCompleted);
        items.Add(new ListItem(copyCardPage)
        {
            Title = "Copy to…",
            Subtitle = "Create a duplicate of this card in a list",
            Icon = new IconInfo("\uE8C8"), // Copy
        });

        // ── Move to… - navigates to MoveCardPage which lists all available lists ──
        var moveCardPage = new MoveCardPage(_card, _trello, _allLists, _onActionCompleted);
        items.Add(new ListItem(moveCardPage)
        {
            Title = "Move to…",
            Subtitle = "Choose a list to move this card to",
            Icon = new IconInfo("\uE8AB"), // MoveToFolder
        });

        // ── Archive card ─────────────────────────────────────────────────────
        items.Add(new ListItem(new ArchiveCardCommand(_trello, _card, _onActionCompleted))
        {
            Title = "Archive card",
            Subtitle = "Remove from board (can be unarchived in Trello)",
            Icon = new IconInfo("\uE74D"), // Delete
        });

        return [.. items];
    }
}
