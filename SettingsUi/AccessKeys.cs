using System.Globalization;
using System.Windows.Data;

namespace EveMultiPreview.SettingsUi;

/// <summary>The access-key marker of a button's text (IDEAS #51): "Back up the _ticked profiles" is reached with Alt+T.
/// A doubled "__" is a literal underscore. A text with two markers, or a marker that is not followed by a letter, has no
/// access key (<see cref="Of"/> says null) so a stray underscore never takes a key by accident.</summary>
public static class AccessKeys
{
    /// <summary>The key the text names, as written ('t' for "_ticked"); null when it has none or the marking is invalid.</summary>
    public static char? Of(string? content)
    {
        char? key = null;
        if (content == null) return null;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '_') continue;
            if (i + 1 < content.Length && content[i + 1] == '_') { i++; continue; }   // "__": an underscore
            if (key != null || i + 1 >= content.Length || !char.IsLetter(content[i + 1])) return null;
            key = content[i + 1];
        }
        return key;
    }

    /// <summary>The text as a person reads it, and a screen reader says it: markers removed, "__" an underscore.</summary>
    public static string Strip(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var text = new System.Text.StringBuilder(content.Length);
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '_') { text.Append(content[i]); continue; }
            if (i + 1 < content.Length && content[i + 1] == '_') { text.Append('_'); i++; }
        }
        return text.ToString();
    }

    /// <summary>What a screen reader says: <see cref="Strip"/>, and for a Chinese, Japanese or Korean text without the Latin
    /// key in brackets ("导出（_E）…" is said "导出…").</summary>
    public static string Spoken(string? content) =>
        Strip(System.Text.RegularExpressions.Regex.Replace(content ?? "", @"[(（]_[A-Za-z][)）]", ""));

    /// <summary>A text of the player's or the game's (a profile, a character, a group, a slot), shown as it is. A string given
    /// as a button's, check box's or menu item's content is read for "_" markers by most looks, which eats an
    /// underscore ("settings_Default" shown as "settingsDefault") and takes Alt+D for it; a TextBlock is never read so.
    /// It wraps, as the text of a check box or radio does when its column is narrow (Terminal at the window's smallest
    /// width cut "settings_Default · 0 accounts" off; nothing had measured such a label while it was an AccessText).</summary>
    public static System.Windows.Controls.TextBlock Verbatim(string text) =>
        new() { Text = text, TextWrapping = System.Windows.TextWrapping.Wrap };
}

/// <summary>The name a screen reader is given for a button whose label (an AccessText) marks an access key: the text
/// without the marker (and without a Chinese, Japanese or Korean text's key in brackets). WPF gives a button with an AccessText inside no name at all. Bound as
/// <c>AutomationProperties.Name="{Binding Text, ElementName=Label, Converter={x:Static local:AccessKeyName.Instance}}"</c>,
/// it follows a live language switch.</summary>
public sealed class AccessKeyName : IValueConverter
{
    public static readonly AccessKeyName Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => AccessKeys.Spoken(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
