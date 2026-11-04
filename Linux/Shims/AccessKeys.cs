// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The access-key marker rules of SettingsUi/AccessKeys.cs, without its WPF members (Verbatim, AccessKeyName). The search index
// reads them while it is built, so these are the Windows rules copied unchanged, not throwing stubs.
using System.Text.RegularExpressions;

namespace EveMultiPreview.SettingsUi;

public static class AccessKeys
{
    // Copy of SettingsUi/AccessKeys.cs:Of — keep in sync until S2/S3 shares it.
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

    // Copy of SettingsUi/AccessKeys.cs:Strip — keep in sync until S2/S3 shares it.
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

    // Copy of SettingsUi/AccessKeys.cs:Spoken — keep in sync until S2/S3 shares it.
    /// <summary>What a screen reader says: <see cref="Strip"/>, and for a Chinese, Japanese or Korean text without the Latin
    /// key in brackets.</summary>
    public static string Spoken(string? content) =>
        Strip(Regex.Replace(content ?? "", @"[(（]_[A-Za-z][)）]", ""));
}
