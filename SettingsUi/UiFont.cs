using FontFamily = System.Windows.Media.FontFamily;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §7: Segoe UI Variable for Latin and Cyrillic; an explicit family per CJK language
/// so glyphs never fall back to a mismatched font.</summary>
public static class UiFont
{
    public static FontFamily For(string lang) => new(lang switch
    {
        "ja" => "Yu Gothic UI",
        "ko" => "Malgun Gothic",
        "zh" => "Microsoft YaHei UI",
        _ => "Segoe UI Variable Text, Segoe UI",
    });
}
