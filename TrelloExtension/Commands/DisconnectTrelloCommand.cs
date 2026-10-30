using System;
using TrelloExtension.Auth;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Removes the stored OAuth 2.0 tokens from Windows Credential Locker.
/// After this, BoardListPage will show the Connect item on next render.
/// </summary>
internal sealed partial class DisconnectTrelloCommand : InvokableCommand
{
    private readonly Action? _onCompleted;

    public DisconnectTrelloCommand(Action? onCompleted = null)
    {
        _onCompleted = onCompleted;
        Name = "Disconnect Trello";
        Icon = new IconInfo("\uE7E8"); // LeaveChat / sign-out icon
    }

    public override CommandResult Invoke()
    {
        AuthService.Instance.SignOut();
        _onCompleted?.Invoke();
        return CommandResult.Dismiss();
    }
}
