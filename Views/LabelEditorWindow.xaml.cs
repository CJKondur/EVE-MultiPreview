using System;
using System.Windows;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

// Disambiguate WPF media types from System.Drawing (pulled in by WinForms interop
// elsewhere in the project) — without these, Color/Brush/ColorConverter are
// ambiguous references.
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace EveMultiPreview.Views;

/// <summary>
/// Modal popup for editing one character's thumbnail label: the label text, a
/// per-character text color override, and a per-character text size override.
/// Empty color / zero size mean "inherit the global thumbnail text style."
/// Invoked from the Visibility settings panel (edit button) and from right-
/// clicking a thumbnail (Ctrl+Right-click).
/// </summary>
public partial class LabelEditorWindow : Window
{
    private readonly string _characterName;
    private readonly AppSettings _settings;
    private readonly ThumbnailManager? _manager;
    private readonly Action? _onSaved;

    public LabelEditorWindow(string characterName, AppSettings settings,
        ThumbnailManager? manager, Action? onSaved = null)
    {
        InitializeComponent();
        _characterName = characterName;
        _settings = settings;
        _manager = manager;
        _onSaved = onSaved;

        HeaderText.Text = $"Label for “{characterName}”";

        // Pre-fill from existing settings.
        TxtLabel.Text = settings.ThumbnailAnnotations.TryGetValue(characterName, out var text)
            ? text : "";
        if (settings.ThumbnailLabelStyles.TryGetValue(characterName, out var style))
        {
            TxtColor.Text = style.Color ?? "";
            TxtSize.Text = style.Size > 0 ? style.Size.ToString() : "";
        }

        UpdateSwatch();
        Loaded += (_, _) => { TxtLabel.Focus(); TxtLabel.SelectAll(); };
    }

    private void OnColorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateSwatch();

    private void UpdateSwatch()
    {
        try
        {
            string hex = TxtColor.Text.Trim();
            if (string.IsNullOrEmpty(hex))
            {
                ColorSwatch.Background = (Brush)FindResource("BgPanelBrush");
                return;
            }
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = "#" + hex.Substring(2);
            if (!hex.StartsWith("#")) hex = "#" + hex;
            var c = (Color)ColorConverter.ConvertFromString(hex);
            ColorSwatch.Background = new SolidColorBrush(c);
        }
        catch
        {
            ColorSwatch.Background = Brushes.Transparent;
        }
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (!string.IsNullOrEmpty(TxtColor.Text))
        {
            try
            {
                string hex = TxtColor.Text.Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = "#" + hex.Substring(2);
                if (!hex.StartsWith("#")) hex = "#" + hex;
                var c = (Color)ColorConverter.ConvertFromString(hex);
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch { }
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtColor.Text = $"0x{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
        }
    }

    private void OnClearColor(object sender, RoutedEventArgs e)
    {
        TxtColor.Text = "";
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        _settings.ThumbnailAnnotations.Remove(_characterName);
        _settings.ThumbnailLabelStyles.Remove(_characterName);
        _manager?.UpdateCharacterLabel(_characterName, null, null, 0);
        _onSaved?.Invoke();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string text = TxtLabel.Text.Trim();
        string color = TxtColor.Text.Trim();
        int size = int.TryParse(TxtSize.Text.Trim(), out var s) && s > 0 ? s : 0;

        if (string.IsNullOrWhiteSpace(text))
            _settings.ThumbnailAnnotations.Remove(_characterName);
        else
            _settings.ThumbnailAnnotations[_characterName] = text;

        if (string.IsNullOrEmpty(color) && size == 0)
        {
            _settings.ThumbnailLabelStyles.Remove(_characterName);
        }
        else
        {
            _settings.ThumbnailLabelStyles[_characterName] = new ThumbnailLabelStyle
            {
                Color = color,
                Size = size,
            };
        }

        _manager?.UpdateCharacterLabel(_characterName,
            string.IsNullOrWhiteSpace(text) ? null : text,
            string.IsNullOrEmpty(color) ? null : color,
            size);

        _onSaved?.Invoke();
        DialogResult = true;
        Close();
    }
}
