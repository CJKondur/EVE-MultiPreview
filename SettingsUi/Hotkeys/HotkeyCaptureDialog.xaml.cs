using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EveMultiPreview.SettingsUi;

/// <summary>Records one key, key combination or Middle / Mouse 4 / Mouse 5 (spec §6). Writes
/// nothing itself: the caller stores Result. No Mouse.Capture, so alt-tab can't strand it.</summary>
public partial class HotkeyCaptureDialog : Window
{
    private readonly HotkeyCaptureModel _model;
    private int _rawKey;   // the Windows code of the key being pressed, noted before WPF raises PreviewKeyDown

    public HotkeyCaptureDialog(string title, string? current, Func<string, IReadOnlyList<HotkeyUse>> findClashes)
    {
        UiStyles.Attach(this);
        InitializeComponent();
        _model = new HotkeyCaptureModel(current, findClashes);
        DataContext = _model;
        TitleText.Text = title;
        Title = title;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        // WPF reports some keys as Key.None (IDEAS P22), so the raw code is read from the message first. Only observed,
        // never handled; the filter runs on this thread's message loop only while the dialog is open.
        System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += NoteRawKey;
        Closed += (_, _) => System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= NoteRawKey;
    }

    /// <summary>The new hotkey, "" to clear it, null when cancelled.</summary>
    public string? Result { get; private set; }

    /// <summary>The key code of a key-down message (WM_KEYDOWN, WM_SYSKEYDOWN), else null.</summary>
    public static int? RawKeyOf(int message, IntPtr wParam) =>
        message is 0x0100 or 0x0104 ? (int)(wParam.ToInt64() & 0xFF) : null;

    private void NoteRawKey(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        if (RawKeyOf(msg.message, msg.wParam) is int vk) _rawKey = vk;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        // The raw code; for a key WPF does name (a media key), its own mapping if the message wasn't seen.
        e.Handled = _model.OnKey(key, Keyboard.Modifiers, _rawKey != 0 ? _rawKey : KeyInterop.VirtualKeyFromKey(key));
        _rawKey = 0;
        if (_model.Cancelled) DialogResult = false;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = _model.OnMouse(e.ChangedButton, Keyboard.Modifiers);

    private void OnOk(object sender, RoutedEventArgs e) { Result = _model.Candidate; DialogResult = true; }
    private void OnClear(object sender, RoutedEventArgs e) { Result = ""; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
