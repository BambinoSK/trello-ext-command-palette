using System;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Services;
using TrelloExtension.Settings;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Creates a new card with a pre-supplied name in the specified list.
/// Invoked from CardsPage "＋ New card" item.
/// After creation the optional onCompleted callback refreshes the parent page.
///
/// The pos parameter (top/bottom) is read from TrelloSettingsManager.Instance.NewCardPosition.
/// </summary>
internal sealed partial class CreateCardCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly string _listId;
    private readonly string _cardName;
    private readonly Action? _onCompleted;

    public CreateCardCommand(TrelloService trello, string listId, string cardName, Action? onCompleted = null)
    {
        _trello = trello;
        _listId = listId;
        _cardName = cardName;
        _onCompleted = onCompleted;
        Name = "Create card";
        Icon = new IconInfo("\uE710"); // Add icon
    }

    public override CommandResult Invoke()
    {
        // Read position from settings at invoke time (not construction) so it's always fresh.
        var pos = TrelloSettingsManager.Instance.NewCardPosition;

        Task.Run(async () =>
        {
            try
            {
                await _trello.CreateCardAsync(_listId, _cardName, pos, CancellationToken.None).ConfigureAwait(false);
                _onCompleted?.Invoke();

                var toast = new ToastStatusMessage(new StatusMessage { Message = $"Card \"{_cardName}\" created", State = MessageState.Success });
                toast.Show();
            }
            catch (Exception ex)
            {
                var toast = new ToastStatusMessage($"Failed to create card: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}
