using System;
using Microsoft.Win32;

namespace TrelloExtension.Helpers;

/// <summary>
/// Detects whether the Windows system is in dark or light theme.
///
///   Windows stores the current app theme in the registry under
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
///   - AppsUseLightTheme = 1 → light theme
///   - AppsUseLightTheme = 0 → dark theme
///
/// The Command Palette extension SDK (v0.9.260303001) does NOT expose a Theme property on IExtensionHost.
/// The only viable approach is reading the Windows system theme from the registry.
///
/// The result is cached for a short period so that theme switches take effect
/// without requiring an extension restart, but we don't hit the registry on every
/// single tag render.
/// </summary>
internal static class ThemeHelper
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    // Cache the result for 5 seconds so that a theme switch is picked up
    // on the next GetItems() call without hammering the registry on every tag render.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private static bool _cachedIsDark;
    private static DateTime _cacheTimestamp = DateTime.MinValue;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns true if the Windows system is currently in dark theme.
    /// Re-reads the registry if the cache is older than CacheDuration.
    /// </summary>
    public static bool IsSystemDarkTheme()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cacheTimestamp < CacheDuration)
            {
                return _cachedIsDark;
            }

            _cachedIsDark = ReadIsDarkFromRegistry();
            _cacheTimestamp = DateTime.UtcNow;
            return _cachedIsDark;
        }
    }

    /// <summary>
    /// Forces a re-read of the registry on the next IsSystemDarkTheme() call.
    /// Useful after a known theme change event.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cacheTimestamp = DateTime.MinValue;
        }
    }

    private static bool ReadIsDarkFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var value = key?.GetValue(AppsUseLightThemeValue);
            // value is int i && i > 0 → light theme; otherwise dark.
            // If the key doesn't exist (older Windows), default to light (0 = light = false dark).
            return value is int i && i == 0;
        }
        catch
        {
            // If registry access fails for any reason, default to light theme.
            return false;
        }
    }
}
