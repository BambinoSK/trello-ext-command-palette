using System;
using TrelloExtension.Pages;
using TrelloExtension.Services;
using TrelloExtension.Settings;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension;

/// <summary>
/// Root CommandProvider for the Trello extension.
///
/// TopLevelCommands(): returns the single "Trello" board-list entry.
///   MoreCommands on the top-level item exposes the Settings page.
///
/// Settings:
///   Wired via CommandProvider.Settings = _settingsManager.Settings.
///   The settings page is automatically accessible in the CmdPal extension manager.
///
/// GetCommandItem(string id):
///   Enables the Command Palette host's built-in Pin mechanism for nested commands.
///   When a user right-clicks a list's CardsPage and pins it, Command Palette calls
///   GetCommandItem(id) to restore it after restart. The item then appears in the
///   global "Pinned" section at the top of Command Palette.
///
///   Id format: "trello.list.{listId}|{listName}|{boardName}"
/// </summary>
public partial class TrelloExtensionCommandsProvider : CommandProvider
{
    private static readonly IconInfo TrelloIcon =
        IconHelpers.FromRelativePaths(@"Assets\StoreLogo.png", @"Assets\StoreLogo.png");

    private readonly TrelloService _trello = new();
    private readonly TrelloSettingsManager _settingsManager = TrelloSettingsManager.Instance;
    private readonly BoardListPage _boardListPage;
    private readonly ICommandItem[] _commands;

    public TrelloExtensionCommandsProvider()
    {
        DisplayName = "Trello";
        Icon = TrelloIcon;
        Id = "trello";

        _boardListPage = new BoardListPage(_trello);

        _commands =
        [
            new CommandItem(_boardListPage)
            {
                Title = "Trello",
                Subtitle = "Browse boards, lists, and cards - create, move, archive",
                Icon = TrelloIcon,
                MoreCommands =
                [
                    new CommandContextItem(_settingsManager.Settings.SettingsPage)
                    {
                        Title = "Settings",
                    },
                ],
            },
        ];

        Settings = _settingsManager.Settings;
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    /// <summary>
    /// Enables the host's built-in Pin mechanism for nested commands.
    /// Called by Command Palette when it needs to restore a pinned item by its stable Id.
    ///
    /// Supported Id formats (names are URL-encoded so a board/list name
    /// containing '|' cannot corrupt the id|name|board split - see
    /// EncodeIdName/DecodeIdName):
    ///   "trello.board.{boardId}|{boardName}"    - ListsPage for a board (set in BoardListPage)
    ///   "trello.list.{listId}|{listName}|{boardName}" - CardsPage for a list (set in ListsPage)
    /// </summary>
    public override ICommandItem? GetCommandItem(string id)
    {
        // ── Pinned board: "trello.board.{boardId}|{boardName}" ──────────────
        if (id.StartsWith("trello.board.", System.StringComparison.Ordinal))
        {
            var boardPayload = id["trello.board.".Length..];
            var boardParts = boardPayload.Split('|', 2);

            var boardId   = boardParts[0];
            var boardName = boardParts.Length > 1 ? DecodeIdName(boardParts[1]) : "Trello";

            var board = new Models.TrelloBoard { Id = boardId, Name = boardName };
            var boardPage = new Pages.ListsPage(_trello, board);
            boardPage.Id = id;

            return new CommandItem(boardPage)
            {
                Title = boardName,
                Subtitle = "Trello",
                Icon = TrelloIcon,
            };
        }

        // ── Pinned list: "trello.list.{listId}|{listName}|{boardName}" ──────
        if (!id.StartsWith("trello.list.", System.StringComparison.Ordinal))
            return null;

        // Parse: "trello.list.{listId}|{listName}|{boardName}"
        var listPayload = id["trello.list.".Length..];
        var listParts = listPayload.Split('|', 3);

        var listId   = listParts[0];
        var listName = listParts.Length > 1 ? DecodeIdName(listParts[1]) : listId;
        var listBoardName = listParts.Length > 2 ? DecodeIdName(listParts[2]) : "Trello";

        var list = new Models.TrelloList { Id = listId, Name = listName };
        var listPage = new CardsPage(_trello, list, []);
        listPage.Id = id;

        return new CommandItem(listPage)
        {
            Title = listName,
            Subtitle = listBoardName,
            Icon = TrelloIcon,
        };
    }

    /// <summary>
    /// Encodes a board/list name for embedding in a stable pin Id
    /// ("trello.board.{id}|{name}" / "trello.list.{id}|{name}|{board}") so a
    /// name containing '|' cannot corrupt the split in GetCommandItem.
    /// URL-encoding ('%' + hex) never emits a raw '|' or '%'-free ambiguity.
    /// </summary>
    internal static string EncodeIdName(string name) =>
        Uri.EscapeDataString(name ?? string.Empty);

    /// <summary>Reverses EncodeIdName; tolerant of legacy (unencoded) ids.</summary>
    internal static string DecodeIdName(string encoded) =>
        Uri.UnescapeDataString(encoded ?? string.Empty);
}
