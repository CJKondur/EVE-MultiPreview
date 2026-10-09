using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace EveMultiPreview.Services;

/// <summary>AHK-style hotkey strings ("^!F5", "XButton1"): naming, display and clash rules.
/// The format is shared with HotkeyService and the AHK builds; never change what it produces.</summary>
public static class HotkeyText
{
    private const string ModifierChars = "^!+#";

    /// <summary>AHK key name for a WPF key. Moved unchanged from SettingsWindow.KeyToAhkName.</summary>
    public static string KeyToAhkName(Key key)
    {
        return key switch
        {
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15", Key.F16 => "F16",
            Key.F17 => "F17", Key.F18 => "F18", Key.F19 => "F19", Key.F20 => "F20",
            Key.F21 => "F21", Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",
            Key.Escape => "Escape",
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab",
            Key.Back => "Backspace", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Pause => "Pause", Key.Scroll => "ScrollLock",
            Key.CapsLock => "CapsLock", Key.NumLock => "NumLock",
            Key.PrintScreen => "PrintScreen",
            Key.NumPad0 => "Numpad0", Key.NumPad1 => "Numpad1", Key.NumPad2 => "Numpad2",
            Key.NumPad3 => "Numpad3", Key.NumPad4 => "Numpad4", Key.NumPad5 => "Numpad5",
            Key.NumPad6 => "Numpad6", Key.NumPad7 => "Numpad7", Key.NumPad8 => "Numpad8",
            Key.NumPad9 => "Numpad9", Key.Multiply => "NumpadMult", Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSub", Key.Divide => "NumpadDiv", Key.Decimal => "NumpadDot",
            // Top-row digits: WPF stringifies these as "D0".."D9" which the hotkey
            // parser can't resolve. Emit the bare digit so ParseVirtualKey maps to
            // VK_0..VK_9 directly.
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            // Distinct ISO <>| / extra-backslash key — emit a token the parser
            // resolves to VK_OEM_102 rather than the "oembackslash" fallback (#67).
            Key.OemBackslash => "OEM102",
            _ => key.ToString().ToLowerInvariant()
        };
    }

    /// <summary>A name HotkeyService parses for a raw Windows key code, for a key WPF can't name (Key.None: vendor keys,
    /// F13–F24 sent by keyboard software) or one with no AHK name above (media keys): F1–F24 by name, any other key in
    /// AHK's vkNN form (IDEAS P22). Null for no key, a modifier or a mouse button, which are never captured this way.</summary>
    public static string? VkToAhkName(int vk)
    {
        if (vk is <= 0x07 or >= 0xFF) return null;                                        // no key, mouse buttons, "no mapping"
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) return null;   // Shift, Ctrl, Alt, Win
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture);
        return "vk" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>Modifier prefix in the fixed order the capture has always used: ^ ! + #.</summary>
    public static string ModifierPrefix(ModifierKeys mods) =>
        (mods.HasFlag(ModifierKeys.Control) ? "^" : "") +
        (mods.HasFlag(ModifierKeys.Alt) ? "!" : "") +
        (mods.HasFlag(ModifierKeys.Shift) ? "+" : "") +
        (mods.HasFlag(ModifierKeys.Windows) ? "#" : "");

    /// <summary>Mouse buttons HotkeyService can bind (via its mouse hook); null for the others.</summary>
    public static string? MouseButtonName(MouseButton button) => button switch
    {
        MouseButton.Middle => "MButton",
        MouseButton.XButton1 => "XButton1",
        MouseButton.XButton2 => "XButton2",
        _ => null,
    };

    /// <summary>Whether HotkeyService can bind this string: a mouse button its hook handles, or a
    /// key its parser resolves to a virtual key. Anything else (e.g. "apps", "browserback") would
    /// be saved and silently never fire.</summary>
    public static bool IsUsable(string? ahk)
    {
        if (string.IsNullOrWhiteSpace(ahk)) return false;
        var (_, key) = Split(ahk);
        return HotkeyService.IsMouseButton(key) || HotkeyService.ParseAhkHotkeyString(ahk).VirtualKey != 0;
    }

    /// <summary>"^!XButton1" -> ("^!", "XButton1"). The last character is always the key, so a
    /// bare "+" or "#" is never mistaken for a modifier.</summary>
    public static (string Mods, string Key) Split(string ahk)
    {
        var s = ahk.Trim();
        int i = 0;
        while (i < s.Length - 1 && ModifierChars.IndexOf(s[i]) >= 0) i++;
        return (s[..i], s[i..]);
    }

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
