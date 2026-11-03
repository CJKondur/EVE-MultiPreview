namespace EveMultiPreview.Linux;

/// <summary>Which hotkeys are the same key. Two AHK strings are the same key when their modifiers (^ Ctrl, ! Alt, +
/// Shift, # Super, in any order) and key name (any case, and the names GlobalHotkeys.Keysym takes for one key) are the
/// same. The Windows app's own HotkeyText logic is not compiled on Linux.</summary>
internal static class HotkeyClashes
{
    private static readonly Dictionary<string, string> Alias = new()
    {
        ["esc"] = "escape", ["return"] = "enter", ["bs"] = "backspace", ["del"] = "delete", ["ins"] = "insert",
    };

    /// <summary>The key in one spelling: modifiers in the order ^ ! + #, the key name lower case. "" for none.</summary>
    public static string Normalize(string? ahk)
    {
        if (string.IsNullOrWhiteSpace(ahk)) return "";
        ahk = ahk.Trim();
        bool ctrl = false, alt = false, shift = false, super = false;
        int i = 0;
        for (; i < ahk.Length - 1 && "^!+#".Contains(ahk[i]); i++)
        {
            switch (ahk[i]) { case '^': ctrl = true; break; case '!': alt = true; break; case '+': shift = true; break; default: super = true; break; }
        }
        var name = ahk[i..].ToLowerInvariant();
        if (Alias.TryGetValue(name, out var canonical)) name = canonical;
        return (ctrl ? "^" : "") + (alt ? "!" : "") + (shift ? "+" : "") + (super ? "#" : "") + name;
    }

    /// <summary>The labels of the other actions whose key is the same as the key of the action <paramref name="id"/>.</summary>
    public static IReadOnlyList<string> With(IEnumerable<(string Id, string Label, string Key)> all, string id)
    {
        var list = all.ToList();
        var own = Normalize(list.FirstOrDefault(a => a.Id == id).Key);
        if (own.Length == 0) return Array.Empty<string>();
        return list.Where(a => a.Id != id && Normalize(a.Key) == own).Select(a => a.Label).ToList();
    }
}
