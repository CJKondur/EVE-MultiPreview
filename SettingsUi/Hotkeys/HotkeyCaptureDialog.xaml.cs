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

    public HotkeyCaptureDialog(string title, string? current, Func<string, IReadOnlyList<HotkeyUse>> findClashes)
    {
        InitializeComponent();
        _model = new HotkeyCaptureModel(current, findClashes);
        DataContext = _model;
        TitleText.Text = title;
        Title = title;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
    }

    /// <summary>The new hotkey, "" to clear it, null when cancelled.</summary>
    public string? Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = _model.OnKey(key, Keyboard.Modifiers);
        if (_model.Cancelled) DialogResult = false;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = _model.OnMouse(e.ChangedButton, Keyboard.Modifiers);

    private void OnOk(object sender, RoutedEventArgs e) { Result = _model.Candidate; DialogResult = true; }
    private void OnClear(object sender, RoutedEventArgs e) { Result = ""; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
