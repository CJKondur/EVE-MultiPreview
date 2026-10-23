using System;

namespace EveMultiPreview.SettingsUi;

/// <summary>Windows' app theme, light or dark (IDEAS P3): the one value Windows keeps for "Choose your default app mode"
/// (Settings, Personalization, Colors). It is only read, never written. The reader is replaceable so a test says what Windows
/// says; the real one is reached only while the "follow Windows' app theme" switch is on.</summary>
public static class WindowsAppTheme
{
    /// <summary>The key and the value Windows writes the app mode to: 1 is light, 0 is dark.</summary>
    private const string Key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Value = "AppsUseLightTheme";

    /// <summary>Whether Windows' app theme is light now. A value that is missing, of another type or not readable is "not
    /// light": the player's own look then shows.</summary>
    public static Func<bool> IsLight { get; set; } = Read;

    /// <summary>Light only for the number 1 (a DWORD); 0 is dark, and anything else cannot be told.</summary>
    public static bool FromRegistryValue(object? raw) => raw is int number && number == 1;

    private static bool Read()
    {
        try { return FromRegistryValue(Microsoft.Win32.Registry.GetValue(Key, Value, null)); }
        catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
