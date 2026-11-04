// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows hotkey-string rules without their WPF members (KeyToAhkName, ModifierPrefix, MouseButtonName, IsUsable). The
// settings window shows keys and checks clashes with these, so they are the Windows rules copied unchanged, not throwing stubs.
namespace EveMultiPreview.Services;

public static class HotkeyText
{
    private const string ModifierChars = "^!+#";

    // Copy of Services/HotkeyText.cs:Split — keep in sync until the Windows file is split.
    /// <summary>"^!XButton1" -> ("^!", "XButton1"). The last character is always the key, so a
    /// bare "+" or "#" is never mistaken for a modifier.</summary>
    public static (string Mods, string Key) Split(string ahk)
    {
        var s = ahk.Trim();
        int i = 0;
        while (i < s.Length - 1 && ModifierChars.IndexOf(s[i]) >= 0) i++;
        return (s[..i], s[i..]);
    }

    // Copy of Services/HotkeyText.cs:ToDisplay — keep in sync until the Windows file is split.
    /// <summary>Player-facing text: "^XButton1" -> "Ctrl + Mouse 4". Modifiers always read
    /// Ctrl, Alt, Shift, Win. Key labels stay English, as on the keyboard.</summary>
    public static string ToDisplay(string? ahk)
    {
        if (string.IsNullOrWhiteSpace(ahk)) return "";
        var (mods, key) = Split(ahk);
        var parts = new List<string>();
        if (mods.Contains('^')) parts.Add("Ctrl");
        if (mods.Contains('!')) parts.Add("Alt");
        if (mods.Contains('+')) parts.Add("Shift");
        if (mods.Contains('#')) parts.Add("Win");
        parts.Add(key.ToLowerInvariant() switch
        {
            "xbutton1" => "Mouse 4",
            "xbutton2" => "Mouse 5",
            "mbutton" => "Middle mouse",
            _ when key.Length == 1 => key.ToUpperInvariant(),
            _ => key,
        });
        return string.Join(" + ", parts);
    }

    // Copy of Services/HotkeyText.cs:Clashes — keep in sync until the Windows file is split.
    /// <summary>Whether two hotkeys fire together. Equal ignoring case and modifier order; with
    /// wildcard modifiers on (HotkeyWildcardModifiers), a bare key also claims every modifier
    /// combination of the same key.</summary>
    public static bool Clashes(string a, string b, bool wildcard)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var (ma, ka) = Split(a.ToLowerInvariant());
        var (mb, kb) = Split(b.ToLowerInvariant());
        if (ka != kb) return false;
        if (new string(ma.OrderBy(c => c).ToArray()) == new string(mb.OrderBy(c => c).ToArray())) return true;
        return wildcard && (ma.Length == 0 || mb.Length == 0);
    }
}
