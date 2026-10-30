using System;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Marks the due date on a card as complete via PUT /1/cards/{cardId}?dueComplete=true.
/// Only meaningful when the card has a due date set; shown regardless so the action is always available.
/// </summary>
internal sealed partial class MarkCompleteCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private readonly Action? _onCompleted;

    public MarkCompleteCommand(TrelloService trello, TrelloCard card, Action? onCompleted = null)
    {
        _trello = trello;
        _card = card;
        _onCompleted = onCompleted;
        Name = "Mark as complete";
        Icon = new IconInfo("\uE73E"); // CheckMark icon
    }

    public override CommandResult Invoke()
    {
        Task.Run(async () =>
        {
            try
            {
                await _trello.MarkCardCompleteAsync(_card.Id, CancellationToken.None).ConfigureAwait(false);
                // Update local state so the UI reflects the change without a full
                // API reload. This keeps the same TrelloCard object → same ListItem
                // → same MoreCommands (with the toggle now showing "Mark as incomplete")
                // → SecondaryCommand stays initialized → Ctrl+Enter works.
                _card.DueComplete = true;
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                // Show a toast so the user knows the action failed (e.g. rate limit).
                var toast = new ToastStatusMessage($"Failed to mark complete: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}
