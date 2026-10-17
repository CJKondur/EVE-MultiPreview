using System.Runtime.CompilerServices;
using System.Windows;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;

namespace EveMultiPreview.SettingsUi;

/// <summary>The fonts of the app's windows (spec §7; UI styles spec §13): a window's text is drawn in the look's Fx.Font by
/// reference, and the look's three font tokens hold the chain for the UI language. A family the app carries comes first,
/// then the language's family for Chinese, Japanese and Korean (so glyphs never fall back to a mismatched font), then
/// Segoe UI; a family of Windows gives way to the language's family whole, as before plan S3 (owner decision S3-11).</summary>
public static class UiFont
{
    /// <summary>The family of a Chinese, Japanese or Korean UI, or null for a language that takes the look's.</summary>
    private static string? Cjk(string lang) => lang switch
    {
        "ja" => "Yu Gothic UI",
        "ko" => "Malgun Gothic",
        "zh" => "Microsoft YaHei UI",
        _ => null,
    };

    /// <summary>The language's own family, else Windows 11's (tests' harnesses set a window's font with it).</summary>
    public static FontFamily For(string lang) => new(Cjk(lang) ?? "Segoe UI Variable Text, Segoe UI");

    /// <summary>A family the app carries: its source names where its files are ("…/#Figtree").</summary>
    public static bool IsBundled(FontFamily family) => family.Source.Contains('#');

    /// <summary>The chain of a look's family for a language (spec §13).</summary>
    public static FontFamily Chain(FontFamily family, string lang)
    {
        if (Cjk(lang) is not { } cjk) return family;
        return IsBundled(family) ? new FontFamily(family.BaseUri, family.Source + ", " + cjk + ", Segoe UI") : new FontFamily(cjk);
    }

    private sealed record Written(FontFamily Font, FontFamily Title, FontFamily Mono, FontStyle TitleStyle);

    /// <summary>The families and the title's slant each look dictionary wrote, kept the first time it is localised.</summary>
    private static readonly ConditionalWeakTable<ResourceDictionary, Written> Originals = new();

    /// <summary>Puts the chains of <paramref name="lang"/> into a look's three font tokens, from the families the look wrote;
    /// in Chinese, Japanese and Korean its title is upright (O28: a simulated slant reads badly). Only a value that changes
    /// is written: a window's references follow each write.</summary>
    public static void Localize(ResourceDictionary look, string lang)
    {
        var written = Originals.GetValue(look, d => new Written((FontFamily)d["Fx.Font"], (FontFamily)d["Fx.TitleFont"],
            (FontFamily)d["Fx.MonoFont"], (FontStyle)d["Fx.Style.Title"]));
        Set(look, "Fx.Font", Chain(written.Font, lang));
        Set(look, "Fx.TitleFont", Chain(written.Title, lang));
        Set(look, "Fx.MonoFont", Chain(written.Mono, lang));
        Set(look, "Fx.Style.Title", Cjk(lang) != null ? FontStyles.Normal : written.TitleStyle);
    }

    private static void Set(ResourceDictionary look, string key, object value)
    {
        if (!Equals(look[key], value)) look[key] = value;
    }

    /// <summary>Gives a window (or any element) the look's font for the language, by reference, so a change of look changes
    /// it too; the look dictionaries of the thread take the language's chains first.</summary>
    public static void Apply(FrameworkElement element, string lang)
    {
        UiStyles.UseLanguage(lang);
        element.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Fx.Font");
    }

    /// <summary>The same as a family, for text drawn in code (the hub's): read when the text is made.</summary>
    public static FontFamily Of(string lang)
    {
        UiStyles.UseLanguage(lang);
        return (FontFamily)UiStyles.Current.Dictionary["Fx.Font"];
    }
}
