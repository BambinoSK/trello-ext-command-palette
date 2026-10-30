using System;
using TrelloExtension.Auth;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Aborts an in-flight Trello OAuth 2.0 sign-in. Shown while BoardListPage is in
/// its "waiting" state - the escape hatch for a user who closed the browser tab
/// without approving (otherwise they would wait out the 5-minute timeout with
/// nothing to do).
///
/// AuthService.CancelSignIn() cancels the sign-in's CancellationTokenSource -
/// the background loop-abort listener stops, PendingRequest is cleared, and the
/// completion task resolves as failed (which fires the page's failure callback
/// and re-renders the Connect row).
/// </summary>
internal sealed partial class CancelSignInCommand : InvokableCommand
{
    private readonly Action _onCancelled;

    public CancelSignInCommand(Action onCancelled)
    {
        _onCancelled = onCancelled;
        Name = "Cancel sign-in";
        Icon = new IconInfo("\uE711"); // Cancel
    }

    public override CommandResult Invoke()
    {
        AuthService.Instance.CancelSignIn();

        // The background completion task fires _onFailed via the watcher in
        // ConnectTrelloCommand, but also refresh immediately in case that
        // watcher was never installed (defensive - RaiseItemsChanged is cheap).
        _onCancelled();

        var toast = new ToastStatusMessage("Sign-in cancelled");
        toast.Show();

        return CommandResult.KeepOpen();
    }
}
