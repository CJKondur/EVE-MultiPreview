using System.Reflection;
using System.Xml.Linq;

namespace EveMultiPreview.Services;

/// <summary>The Linux side of the Windows app's LocalizationService, with the same members. It reads the same
/// Resources/Strings.&lt;code&gt;.xaml files plus the Linux-only Linux/Resources/LinuxStrings.&lt;code&gt;.xaml (embedded in the exe): English is the base, the chosen language is laid
/// over it, and a key missing from both gives the caller's fallback.</summary>
public static class LocalizationService
{
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
        ("it", "Italiano"),
    };

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Dictionary<string, string> English = Load("en");
    private static Dictionary<string, string> _overlay = new();

    public static string Str(string key, string fallback)
    {
        var s = _overlay.GetValueOrDefault(key) ?? English.GetValueOrDefault(key) ?? DetachedStrings?.Invoke(key);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    /// <summary>Kept for the shared code's tests; the Linux app does not set it.</summary>
    public static Func<string, string?>? DetachedStrings { get; set; }

    public static string CurrentLanguage { get; private set; } = "en";

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Array.Exists(Languages, l => l.Code == code);

    public static string Resolve(string? setting)
    {
        if (IsSupported(setting)) return setting!;
        var os = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsSupported(os) ? os : "en";
    }

    public static void SetLanguage(string? code)
    {
        code = Resolve(code);
        _overlay = code == "en" ? new() : Load(code);
        CurrentLanguage = _overlay.Count > 0 || code == "en" ? code : "en";
    }

    /// <summary>The language's strings by key: the Windows app's Strings.&lt;code&gt;.xaml with the Linux-only
    /// LinuxStrings.&lt;code&gt;.xaml (the L.Linux.* keys) laid over it. Empty when neither file is readable (English then shows).</summary>
    private static Dictionary<string, string> Load(string code)
    {
        var strings = ReadFile($"Strings.{code}.xaml");
        foreach (var (key, value) in ReadFile($"LinuxStrings.{code}.xaml")) strings[key] = value;
        return strings;
    }

    /// <summary>One embedded string file by key; empty when it is missing or unreadable.</summary>
    private static Dictionary<string, string> ReadFile(string resource)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream == null) return new();
            return XDocument.Load(stream).Root!.Elements()
                .Where(e => e.Attribute(X + "Key") != null)
                .GroupBy(e => e.Attribute(X + "Key")!.Value)
                .ToDictionary(g => g.Key, g => g.Last().Value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Localization] Failed to load '{resource}': {ex.Message}");
            return new();
        }
    }
}
