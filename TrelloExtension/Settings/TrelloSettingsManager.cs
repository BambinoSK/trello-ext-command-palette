using System.Collections.Generic;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TrelloExtension.Helpers;

// Alias to avoid collision between the namespace "TrelloExtension.Settings"
// and the Toolkit type "Microsoft.CommandPalette.Extensions.Toolkit.Settings".
using ToolkitSettings = Microsoft.CommandPalette.Extensions.Toolkit.Settings;

namespace TrelloExtension.Settings;

/// <summary>
/// Extends JsonSettingsManager.
/// JsonSettingsManager is a BASE CLASS (not instantiable directly).
///
/// Exposes two typed settings:
///   NewCardPosition  string  "top" | "bottom"   default "top"
///   AutoShowDetails  bool                        default true
/// </summary>
internal sealed partial class TrelloSettingsManager : JsonSettingsManager
{
    // ── Setting keys ──────────────────────────────────────────────────────────
    // These are stable identifiers - never rename them (would lose persisted values).
    private const string KeyAutoShowDetails = "trello.autoShowDetails";
    private const string KeyNewCardPosition = "trello.newCardPosition";
    private const string KeyThemeOverride   = "trello.themeOverride";
    private const string KeyFetchChecklists  = "trello.fetchChecklists";
    private const string KeyFetchMembers     = "trello.fetchMembers";
    private const string KeyFetchAttachments = "trello.fetchAttachments";

    public const string PosTop    = "top";
    public const string PosBottom = "bottom";

    // Theme override values - stored as strings in settings.json.
    // "auto"  → follow Windows system theme (registry AppsUseLightTheme)
    // "light" → always use light-theme token values
    // "dark"  → always use dark-theme token values
    public const string ThemeAuto  = "auto";
    public const string ThemeLight = "light";
    public const string ThemeDark  = "dark";

    // ── Singleton ─────────────────────────────────────────────────────────────
    private static TrelloSettingsManager? _instance;
    public static TrelloSettingsManager Instance => _instance ??= new TrelloSettingsManager();

    // ── Typed setting objects (for reading .Value) ────────────────────────────
    private readonly ToggleSetting    _autoShowDetails;
    private readonly ChoiceSetSetting _newCardPosition;
    private readonly ChoiceSetSetting _themeOverride;
    private readonly ToggleSetting    _fetchChecklists;
    private readonly ToggleSetting    _fetchMembers;
    private readonly ToggleSetting    _fetchAttachments;

    private TrelloSettingsManager()
    {
        // FilePath: persisted in MSIX LocalState → "settings.json"
        // Verified: Utilities.BaseSettingsPath(string) is correct for packaged apps.
        FilePath = Path.Combine(Utilities.BaseSettingsPath("TrelloExtension"), "settings.json");

        _autoShowDetails = new ToggleSetting(KeyAutoShowDetails, defaultValue: true)
        {
            Label = "Auto-show card details",
            Description = "Open the detail panel automatically when a card is focused. Off = use right-click → Show details",
        };

        _newCardPosition = new ChoiceSetSetting(KeyNewCardPosition, new List<ChoiceSetSetting.Choice>
        {
            new("Top of the list", PosTop),
            new("Bottom of the list", PosBottom),
        })
        {
            Label = "New card position",
            Description = "Where new, copied and moved cards are added to the list",
        };

        _themeOverride = new ChoiceSetSetting(KeyThemeOverride, new List<ChoiceSetSetting.Choice>
        {
            new("Follow system theme", ThemeAuto),
            new("Light", ThemeLight),
            new("Dark", ThemeDark),
        })
        {
            Label = "Label color theme",
            Description = "Color palette for Trello label tags. 'Follow system theme' uses your Windows dark/light setting.",
        };

        _fetchChecklists = new ToggleSetting(KeyFetchChecklists, defaultValue: false)
        {
            Label = "Fetch card checklists",
            Description = "Load checklist items in card details. Off reduces the risk of hitting Trello's rate limit.",
        };

        _fetchMembers = new ToggleSetting(KeyFetchMembers, defaultValue: false)
        {
            Label = "Fetch card members",
            Description = "Load assigned members in card details. Off reduces the risk of hitting Trello's rate limit.",
        };

        _fetchAttachments = new ToggleSetting(KeyFetchAttachments, defaultValue: false)
        {
            Label = "Fetch card attachments",
            Description = "Load attachments (files and links) in card details. Off reduces the risk of hitting Trello's rate limit.",
        };

        Settings.Add(_autoShowDetails);
        Settings.Add(_newCardPosition);
        Settings.Add(_themeOverride);
        Settings.Add(_fetchChecklists);
        Settings.Add(_fetchMembers);
        Settings.Add(_fetchAttachments);

        LoadSettings();

        // Auto-save whenever any setting changes.
        Settings.SettingsChanged += (_, _) => SaveSettings();
    }

    // ── Typed accessors ───────────────────────────────────────────────────────

    /// <summary>Returns "top" or "bottom" for the Trello API pos= parameter.</summary>
    public string NewCardPosition =>
        Settings.TryGetSetting<string>(KeyNewCardPosition, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : PosTop;

    /// <summary>
    /// True → CardsPage.ShowDetails = true (panel opens automatically on card focus).
    /// False → ShowDetails = false (user right-clicks → Show details to open the panel).
    /// </summary>
    public bool AutoShowDetails =>
        Settings.TryGetSetting<bool>(KeyAutoShowDetails, out var v) ? v : true;

    /// <summary>
    /// Returns "auto", "light", or "dark" - the user's theme override preference.
    /// Default is "auto" (follows Windows system theme via ThemeHelper).
    /// </summary>
    public string ThemeOverride =>
        Settings.TryGetSetting<string>(KeyThemeOverride, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : ThemeAuto;

    /// <summary>
    /// Resolves the effective dark-theme flag by combining the user's override
    /// setting with the Windows system theme (when override is "auto").
    ///
    /// Logic:
    ///   "light" → false (always light)
    ///   "dark"  → true  (always dark)
    ///   "auto"  → ThemeHelper.IsSystemDarkTheme() (reads registry, cached 5s)
    /// </summary>
    public bool IsDarkTheme =>
        ThemeOverride switch
        {
            ThemeLight => false,
            ThemeDark  => true,
            _           => ThemeHelper.IsSystemDarkTheme(), // "auto" or any unknown value
        };

    /// <summary>
    /// True → CardsPage fetches checklist items (GET /1/cards/{id}/checklists) for
    /// each card and renders them in the detail panel.
    /// Default false - each card triggers an API call; with many cards this can
    /// exceed the per-token rate limit (100 requests / 10 seconds).
    /// </summary>
    public bool FetchChecklists =>
        Settings.TryGetSetting<bool>(KeyFetchChecklists, out var v) ? v : false;

    /// <summary>
    /// True → CardsPage fetches assigned members (GET /1/cards/{id}/members) for
    /// each card and renders them in the detail panel.
    /// Default false - each card triggers an API call; with many cards this can
    /// exceed the per-token rate limit (100 requests / 10 seconds).
    /// </summary>
    public bool FetchMembers =>
        Settings.TryGetSetting<bool>(KeyFetchMembers, out var v) ? v : false;

    /// <summary>
    /// True → CardsPage fetches attachments (GET /1/cards/{id}/attachments) for
    /// each card and renders them in the detail panel.
    /// Default false - each card triggers an API call; with many cards this can
    /// exceed the per-token rate limit (100 requests / 10 seconds).
    /// </summary>
    public bool FetchAttachments =>
        Settings.TryGetSetting<bool>(KeyFetchAttachments, out var v) ? v : false;
}
