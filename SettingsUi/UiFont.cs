using FontFamily = System.Windows.Media.FontFamily;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §7: an explicit family per CJK language so glyphs never fall back to a mismatched font; for every other
/// language the look's own family (UI styles spec §13, plan S2: Fx.Font; Windows 11's is Segoe UI Variable, as before).</summary>
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

    public static FontFamily For(string lang) => new(Cjk(lang) ?? "Segoe UI Variable Text, Segoe UI");

    /// <summary>Gives a window (or any element) the font of the language and the look: the language's family for Chinese,
    /// Japanese and Korean, else the look's Fx.Font by reference, so a change of look changes it too.</summary>
    public static void Apply(System.Windows.FrameworkElement element, string lang)
    {
        if (Cjk(lang) is { } family) element.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, new FontFamily(family));
        else element.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Fx.Font");
    }

    /// <summary>The same as a family, for text drawn in code (the hub's): read when the text is made.</summary>
    public static FontFamily Of(string lang) =>
        Cjk(lang) is { } family ? new FontFamily(family) : (FontFamily)UiStyles.Current.Dictionary["Fx.Font"];
}
