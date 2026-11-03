using Avalonia.Controls;
using Avalonia.Input;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>A hotkey field. Click it (or press Enter on it), then press the combination: it is stored as the Windows
/// app's AHK-style string ("^!F5", "F1", "+a"; ^ Ctrl, ! Alt, + Shift, # Super). Escape on its own clears it. A key X has
/// no key for here (see <see cref="GlobalHotkeys.Keysym"/>) is refused and the box keeps waiting. Focus leaving ends
/// the capture.</summary>
public sealed class HotkeyBox : Button
{
    private string _value;
    private bool _capturing;

    /// <summary>The new hotkey ("" = cleared). Raised on the UI thread.</summary>
    public event Action<string>? Changed;

    /// <summary>True when a capture starts, false when it ends. The app lifts its global grabs meanwhile, or a key that
    /// is already a hotkey would never reach this window.</summary>
    public event Action<bool>? Capturing;

    public HotkeyBox(string value)
    {
        _value = value;
        MinWidth = 150;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        LostFocus += (_, _) => Stop();
        Show();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public string Value
    {
        get => _value;
        set { _value = value; if (!_capturing) Show(); }
    }

    private void Show()
    {
        Content = _capturing ? LocalizationService.Str("L.Linux.PressKey", "Press a key…")
            : _value.Length > 0 ? _value : LocalizationService.Str("L.Linux.NotSet", "Not set");
        Opacity = !_capturing && _value.Length == 0 ? 0.6 : 1;
    }

    private void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        Show();
        Capturing?.Invoke(false);
    }

    protected override void OnClick()
    {
        if (_capturing) return;
        _capturing = true;
        Show();
        Capturing?.Invoke(true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_capturing) e.Handled = true;
        else base.OnKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        e.Handled = true;
        if (IsModifier(e.Key)) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { Set(""); return; }
        if (NameOf(e.Key) is not { } name || GlobalHotkeys.Keysym(name) == null)
        {
            Content = LocalizationService.Str("L.Linux.KeyRefused", "That key can't be used");
            return;
        }
        Set(Compose(e.KeyModifiers, name));
    }

    private void Set(string value)
    {
        _value = value;
        Stop();
        Changed?.Invoke(value);
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;

    /// <summary>The AHK string for a key and the modifiers held: modifiers in the order ^ ! + #.</summary>
    public static string Compose(KeyModifiers mods, string name) =>
        (mods.HasFlag(KeyModifiers.Control) ? "^" : "") + (mods.HasFlag(KeyModifiers.Alt) ? "!" : "")
        + (mods.HasFlag(KeyModifiers.Shift) ? "+" : "") + (mods.HasFlag(KeyModifiers.Meta) ? "#" : "") + name;

    /// <summary>The AHK key name for an Avalonia key, or null for one with no name here.</summary>
    public static string? NameOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Numpad" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace", Key.Delete => "Delete",
        Key.Insert => "Insert", Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
        Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right", Key.Escape => "Escape",
        Key.Pause => "Pause", Key.Scroll => "ScrollLock", Key.CapsLock => "CapsLock", Key.NumLock => "NumLock",
        Key.PrintScreen => "PrintScreen",
        Key.Multiply => "NumpadMult", Key.Add => "NumpadAdd", Key.Subtract => "NumpadSub", Key.Divide => "NumpadDiv",
        Key.Decimal => "NumpadDot",
        Key.OemMinus => "-", Key.OemPlus => "=", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
        Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\", Key.OemTilde => "`",
        _ => null,
    };
}
