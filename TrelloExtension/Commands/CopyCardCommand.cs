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
/// Copies a card to a target list via POST /1/cards?idCardSource={sourceId}&idList={targetListId}&keepFromSource=all.
/// Unlike MoveCardCommand, copying to the card's current list is valid (creates a duplicate).
/// </summary>
internal sealed partial class CopyCardCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly TrelloCard _card;
    private readonly TrelloList _targetList;
    private readonly Action? _onCompleted;

    public CopyCardCommand(TrelloService trello, TrelloCard card, TrelloList targetList, Action? onCompleted = null)
    {
        _trello = trello;
        _card = card;
        _targetList = targetList;
        _onCompleted = onCompleted;
        Name = $"Copy to \"{targetList.Name}\"";
        Icon = new IconInfo("\uE8C8"); // Copy icon
    }

    public override CommandResult Invoke()
    {
        var pos = TrelloSettingsManager.Instance.NewCardPosition;

        Task.Run(async () =>
        {
            try
            {
                await _trello.CopyCardAsync(_card.Id, _targetList.Id, pos, CancellationToken.None).ConfigureAwait(false);
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                var toast = new ToastStatusMessage($"Failed to copy card: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.GoBack();
    }
}
