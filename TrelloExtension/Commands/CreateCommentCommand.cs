using System;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Posts a new comment with pre-supplied text to a card.
/// Invoked from CardCommentsPage "Post comment" quick-add item.
/// After posting, the optional onCompleted callback reloads the comments list.
/// Trello API: POST /1/cards/{cardId}/actions/comments?text={text}
/// </summary>
internal sealed partial class CreateCommentCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly string _cardId;
    private readonly string _commentText;
    private readonly Action? _onCompleted;

    public CreateCommentCommand(TrelloService trello, string cardId, string commentText, Action? onCompleted = null)
    {
        _trello = trello;
        _cardId = cardId;
        _commentText = commentText;
        _onCompleted = onCompleted;
        Name = "Post comment";
        Icon = new IconInfo("\uE710"); // Add icon
    }

    public override CommandResult Invoke()
    {
        Task.Run(async () =>
        {
            try
            {
                await _trello.AddCommentAsync(_cardId, _commentText, CancellationToken.None).ConfigureAwait(false);
                _onCompleted?.Invoke();

                var toast = new ToastStatusMessage(new StatusMessage { Message = "Comment added", State = MessageState.Success });
                toast.Show();
            }
            catch (Exception ex)
            {
                var toast = new ToastStatusMessage($"Failed to add comment: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}
