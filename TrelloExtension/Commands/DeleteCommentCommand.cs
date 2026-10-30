using System;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Deletes a comment (commentCard action) via DELETE /1/actions/{actionId}.
/// Verified from the Trello REST API docs: only comment actions can be
/// deleted, and only by the member who created the comment.
///
/// Holds the action ID (not the TrelloComment object) so the command stays valid
/// across background reloads that replace the TrelloComment instances - same
/// staleness rationale documented in CardsPage for cached MoreCommands.
/// </summary>
internal sealed partial class DeleteCommentCommand : InvokableCommand
{
    private readonly TrelloService _trello;
    private readonly string _actionId;
    private readonly Action? _onCompleted;

    public DeleteCommentCommand(TrelloService trello, string actionId, Action? onCompleted = null)
    {
        _trello = trello;
        _actionId = actionId;
        _onCompleted = onCompleted;
        Name = "Delete comment";
        Icon = new IconInfo("\uE74D"); // Delete
    }

    public override CommandResult Invoke()
    {
        Task.Run(async () =>
        {
            try
            {
                await _trello.DeleteCommentAsync(_actionId, CancellationToken.None).ConfigureAwait(false);
                _onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                // Show a toast so the user knows the delete failed (e.g. not the
                // comment author, token revoked, rate limit).
                var toast = new ToastStatusMessage($"Failed to delete comment: {ex.Message}");
                toast.Show();
            }
        });

        return CommandResult.KeepOpen();
    }
}
