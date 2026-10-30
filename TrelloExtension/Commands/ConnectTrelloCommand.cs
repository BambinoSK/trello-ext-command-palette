using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TrelloExtension.Auth;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Commands;

/// <summary>
/// Starts the Trello OAuth 2.0 sign-in and opens the consent screen in the
/// system browser.
///
/// Flow:
///   1. AuthService.SignIn() binds a loopback listener on a registered callback
///      port and returns the authorize URL (PKCE + state embedded).
///   2. This command opens that URL in the default browser.
///   3. After the user clicks Allow, the browser redirects to
///      http://127.0.0.1:{port}/callback?code=… - the background task inside
///      AuthService validates state, exchanges the code, and saves the tokens.
///   4. Success → _onAuthenticated fires (BoardListPage reloads boards);
///      failure → error toast with the reason.
///
/// The command returns KeepOpen immediately - the palette never blocks on the
/// browser handshake (same fire-and-forget pattern as CreateCommentCommand).
/// </summary>
internal sealed partial class ConnectTrelloCommand : InvokableCommand
{
    private readonly Action _onAuthenticated;
    private readonly Action? _onStarted;
    private readonly Action? _onFailed;

    public ConnectTrelloCommand(Action onAuthenticated, Action? onStarted = null, Action? onFailed = null)
    {
        _onAuthenticated = onAuthenticated;
        _onStarted = onStarted;
        _onFailed = onFailed;
        Name = "Connect to Trello";
        Icon = new IconInfo("\uE774"); // Globe
    }

    public override CommandResult Invoke()
    {
        var result = AuthService.Instance.SignIn();

        if (!result.ShouldOpenBrowser || result.AuthorizeUrl is null)
        {
            // Setup error (missing client ID, all callback ports busy) - surface it.
            var failToast = new ToastStatusMessage(result.ErrorMessage ?? "Could not start Trello sign-in");
            failToast.Show();
            return CommandResult.KeepOpen();
        }

        // Open the consent screen in the default browser (system browser, not an
        // embedded WebView - RFC 8252 recommendation for native apps).
        Process.Start(new ProcessStartInfo(result.AuthorizeUrl) { UseShellExecute = true });

        // Let the host page swap the Connect row for a "waiting" status row.
        _onStarted?.Invoke();

        // Watch the background handshake: toast + reload on success, toast on failure.
        var completion = result.Completion;
        if (completion is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var done = await completion.ConfigureAwait(false);
                    if (done.IsSuccess)
                    {
                        _onAuthenticated();

                        var success = new ToastStatusMessage(new StatusMessage { Message = "Trello connected", State = MessageState.Success });
                        success.Show();
                    }
                    else
                    {
                        var toast = new ToastStatusMessage(done.ErrorMessage ?? "Trello sign-in failed");
                        toast.Show();

                        // Flip the waiting rows back to the Connect row so the
                        // user is never stuck on the waiting state.
                        _onFailed?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    var toast = new ToastStatusMessage($"Trello sign-in failed: {ex.Message}");
                    toast.Show();
                    _onFailed?.Invoke();
                }
            });
        }

        return CommandResult.KeepOpen();
    }
}
