# Privacy Policy

**Trello for Command Palette** (the "extension") is an unofficial, open-source extension for PowerToys Command Palette.

Last updated: 2026-10-30

## Summary

- The extension talks only to Trello and to Atlassian's sign-in service.
- The developer runs no server and receives no data from you: no analytics, no telemetry, no crash reports, no ads.
- Your password is never seen by the extension. You sign in on Atlassian's own page in your browser.
- Your sign-in tokens stay on your PC, in Windows Credential Locker.

## What the extension accesses

When you sign in, you grant the extension these permissions on your Trello account:

| Permission | Why it is needed |
|---|---|
| `read:member:trello` | Identify the signed-in account |
| `read:board:trello` | Show your boards, lists, cards, labels, comments, checklists, members and attachments |
| `write:board:trello` | Create, edit, move, copy and archive cards, and add, edit and delete comments, only when you run those commands |
| `offline_access` | Keep you signed in without asking every time (refresh token) |

The extension changes data in your Trello account only when you run a command that does so.

## Where data goes

The extension connects only to these services:

- `auth.atlassian.com`: sign-in and token refresh (Trello sign-in is provided by Atlassian)
- `api.trello.com`: reading and changing your Trello data

Those services have their own privacy policies (Atlassian and Trello). Nothing is sent to the developer or to any other third party.

## What is stored on your PC

- **Sign-in tokens** (access and refresh token): in Windows Credential Locker, the same protected store Windows uses for other app credentials. They are never written to a plain file and never leave your PC except to authenticate with Atlassian and Trello.
- **Preferences** (for example new card position, label colors, which card details to load): in a small local `settings.json` in the extension's own settings folder. It contains no Trello content.
- **Pinned boards and lists**: Command Palette stores these itself, as identifiers, in its own settings.
- **Board, list and card content**: loaded from Trello when you open a page and kept in memory while the extension runs. The extension does not write it to disk.

## Sign-in on your PC

Signing in opens your default browser at Atlassian's page. When you approve, the browser returns to a temporary listener on your own computer (`127.0.0.1`, one of the ports 8756 to 8759). The listener only exists during sign-in and accepts connections from your PC only. It closes when sign-in finishes, is cancelled, or times out.

## Your choices

- **Sign out and delete stored tokens:** run **Disconnect Trello** in the extension.
- **Revoke access completely:** remove the app's access in your Atlassian or Trello account settings (connected apps). After that, the stored tokens stop working.
- **Remove everything:** run **Disconnect Trello**, then uninstall the extension.

## Changes to this policy

If this policy changes, the new version is published in this repository with a new "Last updated" date.

## Contact

Open an issue on the Issues page of this repository.

## Disclaimer

This extension is not affiliated with, endorsed by, or sponsored by Atlassian or Trello. Trello is a trademark of Atlassian.
