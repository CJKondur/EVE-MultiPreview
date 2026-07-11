using System;
using System.Windows;

using Application = System.Windows.Application;

namespace EveMultiPreview.Services;

/// <summary>
/// Runtime UI language switching (issue #86). UI strings live in per-language
/// ResourceDictionaries (Resources/Strings.&lt;code&gt;.xaml) referenced from XAML
/// via {DynamicResource L.*}. English (Strings.en.xaml) is always merged as a
/// base in App.xaml, so any key missing from the selected language falls back to
/// English instead of rendering blank; the selected language is merged on top so
/// its keys win. Because DynamicResource re-resolves on dictionary change, an
/// open window updates live when the language is switched.
/// </summary>
public static class LocalizationService
{
    /// <summary>UI languages offered in the selector. Extend as new
    /// Strings.&lt;code&gt;.xaml resource files are added to the project.</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("zh", "中文 (简体)"),
        ("ru", "Русский"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("es", "Español"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    };

    /// <summary>Resolve a localized string for the active language, falling back to
    /// <paramref name="fallback"/> (English) when the key isn't in the current
    /// language overlay. Used for menus/text built in code rather than XAML
    /// (tray menu, thumbnail context menu) — issue #86.</summary>
    public static string Str(string key, string fallback)
    {
        var s = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    private static ResourceDictionary? _overlay;

    /// <summary>Currently applied language code (two-letter). "en" when the base is showing.</summary>
    public static string CurrentLanguage { get; private set; } = "en";

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Array.Exists(Languages, l => l.Code == code);

    /// <summary>Effective language: the saved setting if we ship it, else the OS
    /// UI language if we ship it, else English.</summary>
    public static string Resolve(string? setting)
    {
        if (IsSupported(setting)) return setting!;
        var os = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsSupported(os) ? os : "en";
    }

    /// <summary>Swap the active language overlay. The English base stays merged
    /// (App.xaml) so missing keys fall back to English. Safe to call before any
    /// window exists; missing/broken resource files fail closed to English.</summary>
    public static void SetLanguage(string? code)
    {
        var app = Application.Current;
        if (app == null) return;

        code = Resolve(code);
        CurrentLanguage = code;

        if (_overlay != null)
        {
            app.Resources.MergedDictionaries.Remove(_overlay);
            _overlay = null;
        }

        if (code == "en") return; // English base already supplies every key

        try
        {
            var uri = new Uri($"pack://application:,,,/Resources/Strings.{code}.xaml", UriKind.Absolute);
            var dict = new ResourceDictionary { Source = uri };
            app.Resources.MergedDictionaries.Add(dict);
            _overlay = dict;
        }
        catch (Exception ex)
        {
            // Fail closed to English rather than blanking the UI.
            System.Diagnostics.Debug.WriteLine($"[Localization] Failed to load '{code}': {ex.Message}");
            CurrentLanguage = "en";
        }
    }
}
