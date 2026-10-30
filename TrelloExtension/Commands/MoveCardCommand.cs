using System;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;
using TrelloExtension.Settings;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Moves a card to a target list via PUT /1/cards/{cardId}?idList={newListId}.
/// </summary>
internal sealed partial class MoveCardCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private readonly TrelloList _targetList;
    private readonly Action? _onCompleted;

    public MoveCardCommand(TrelloService trello, TrelloCard card, TrelloList targetList, Action? onCompleted = null)
    {
        _trello = trello;
        _card = card;
        _targetList = targetList;
        _onCompleted = onCompleted;
        Name = $"Move to \"{targetList.Name}\"";
        Icon = new IconInfo("\uE8AB"); // MoveToFolder icon
    }

    public override CommandResult Invoke()
    {
        var pos = TrelloSettingsManager.Instance.NewCardPosition;

        Task.Run(async () =>
        {
            try
            {
                await _trello.MoveCardAsync(_card.Id, _targetList.Id, pos, CancellationToken.None).ConfigureAwait(false);
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                var toast = new ToastStatusMessage($"Failed to move card: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.GoBack();
    }
}
