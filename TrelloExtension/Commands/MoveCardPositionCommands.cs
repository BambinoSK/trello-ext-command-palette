using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Moves a card up (toward the top) by exactly one position within its list.
/// Verified: Trello uses fractional indexing for card positions (pos field).
/// The new pos is the midpoint of the destination slot - strictly between the
/// card two above and the card directly above - so the card always lands
/// exactly one position up, regardless of gap size. If the destination is the
/// absolute top slot, sends pos="top" and lets Trello assign the definitive pos.
/// Trello auto-renumbers positions when cards get too close to represent.
/// </summary>
internal sealed partial class MoveCardUpCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private readonly IReadOnlyList<TrelloCard> _orderedCards;
    private readonly Action? _onCompleted;

    public MoveCardUpCommand(TrelloService trello, TrelloCard card, IReadOnlyList<TrelloCard> orderedCards, Action? onCompleted = null)
    {
        _trello = trello;
        _card = card;
        _orderedCards = orderedCards;
        _onCompleted = onCompleted;
        Name = "Move up";
        Icon = new IconInfo("\uE74A"); // Up icon
    }

    public override CommandResult Invoke()
    {
        // Sort cards by pos ascending (top → bottom) to ensure correct adjacency.
        // The Trello API may not return cards in pos order, so we sort here.
        var sortedCards = _orderedCards.OrderBy(c => c.Pos).ToList();

        // Find the current card's index; the card above is at index-1.
        var currentIndex = -1;
        for (var i = 0; i < sortedCards.Count; i++)
        {
            if (sortedCards[i].Id == _card.Id)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            // Card not found in the list - nothing to do.
            return CommandResult.KeepOpen();
        }

        string newPos;
        double newPosNumeric;

        if (currentIndex <= 1)
        {
            // Destination is the ABSOLUTE TOP slot (index 0 or 1). There is no
            // card two above to midpoint against - use Trello's documented
            // pos="top" and let the server assign the definitive pos.
            newPos = "top";
            // Placeholder for local sort until the next reload: anything
            // strictly below the current first card. The server-assigned pos
            // (from the PUT response) overrides this immediately after.
            newPosNumeric = sortedCards[0].Pos - 1.0;
        }
        else
        {
            // To move UP one position, the card must land strictly between the
            // card two above (currentIndex-2) and the card directly above
            // (currentIndex-1). The midpoint of the destination slot is always
            // strictly inside it → exactly one position up, regardless of gap
            // size. (A fixed delta like pos-1.0 overshoots small gaps → the
            // server assigns a distant pos → move-by-2.)
            var cardTwoAbovePos = sortedCards[currentIndex - 2].Pos;
            var cardAbovePos = sortedCards[currentIndex - 1].Pos;
            newPosNumeric = (cardTwoAbovePos + cardAbovePos) / 2.0;
            newPos = newPosNumeric.ToString("R", CultureInfo.InvariantCulture);
        }

        Task.Run(async () =>
        {
            try
            {
                var serverPos = await _trello.UpdateCardPositionAsync(_card.Id, newPos, CancellationToken.None).ConfigureAwait(false);

                // Apply the SERVER-assigned pos (from the PUT response) - it is
                // the ground truth for the real Trello order. NaN means the
                // response body had no parseable pos; fall back to the locally
                // computed value.
                _card.Pos = double.IsNaN(serverPos) ? newPosNumeric : serverPos;
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                // Show a toast so the user knows the move failed (e.g. rate limit).
                var toast = new ToastStatusMessage($"Failed to move card: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}

/// <summary>
/// Moves a card down (toward the bottom) by exactly one position within its list.
/// Verified: Trello uses fractional indexing for card positions (pos field).
/// The new pos is the midpoint of the destination slot - strictly between the
/// card directly below and the card two below - so the card always lands
/// exactly one position down, regardless of gap size. If the destination is the
/// absolute bottom slot, sends pos="bottom" and lets Trello assign the definitive pos.
/// Trello auto-renumbers positions when cards get too close to represent.
/// </summary>
internal sealed partial class MoveCardDownCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private readonly IReadOnlyList<TrelloCard> _orderedCards;
    private readonly Action? _onCompleted;

    public MoveCardDownCommand(TrelloService trello, TrelloCard card, IReadOnlyList<TrelloCard> orderedCards, Action? onCompleted = null)
    {
        _trello = trello;
        _card = card;
        _orderedCards = orderedCards;
        _onCompleted = onCompleted;
        Name = "Move down";
        Icon = new IconInfo("\uE74B"); // Down icon
    }

    public override CommandResult Invoke()
    {
        // Sort cards by pos ascending (top → bottom) to ensure correct adjacency.
        // The Trello API may not return cards in pos order, so we sort here.
        var sortedCards = _orderedCards.OrderBy(c => c.Pos).ToList();

        // Find the current card's index; the card below is at index+1.
        var currentIndex = -1;
        for (var i = 0; i < sortedCards.Count; i++)
        {
            if (sortedCards[i].Id == _card.Id)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            // Card not found in the list - nothing to do.
            return CommandResult.KeepOpen();
        }

        string newPos;
        double newPosNumeric;

        if (currentIndex >= sortedCards.Count - 2)
        {
            // Destination is the ABSOLUTE BOTTOM slot (last or second-to-last
            // index). There is no card two below to midpoint against - use
            // Trello's documented pos="bottom" and let the server assign the
            // definitive pos.
            newPos = "bottom";
            // Placeholder for local sort until the next reload: anything
            // strictly above the current last card. The server-assigned pos
            // (from the PUT response) overrides this immediately after.
            newPosNumeric = sortedCards[^1].Pos + 1.0;
        }
        else
        {
            // To move DOWN one position, the card must land strictly between the
            // card directly below (currentIndex+1) and the card two below
            // (currentIndex+2). The midpoint of the destination slot is always
            // strictly inside it → exactly one position down, regardless of gap
            // size. (A fixed delta like pos+1.0 overshoots small gaps → the
            // server assigns a distant pos → move-by-2.)
            var cardBelowPos = sortedCards[currentIndex + 1].Pos;
            var cardTwoBelowPos = sortedCards[currentIndex + 2].Pos;
            newPosNumeric = (cardBelowPos + cardTwoBelowPos) / 2.0;
            newPos = newPosNumeric.ToString("R", CultureInfo.InvariantCulture);
        }

        Task.Run(async () =>
        {
            try
            {
                var serverPos = await _trello.UpdateCardPositionAsync(_card.Id, newPos, CancellationToken.None).ConfigureAwait(false);

                // Apply the SERVER-assigned pos (from the PUT response) - it is
                // the ground truth for the real Trello order. NaN means the
                // response body had no parseable pos; fall back to the locally
                // computed value.
                _card.Pos = double.IsNaN(serverPos) ? newPosNumeric : serverPos;
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                // Show a toast so the user knows the move failed (e.g. rate limit).
                var toast = new ToastStatusMessage($"Failed to move card: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}
