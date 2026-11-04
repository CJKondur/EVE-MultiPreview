using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>A thumbnail menu's "Edit label…" (the Windows LabelEditorWindow): one character's label, its text colour and its
/// text size. A blank colour and size 0 use the thumbnails' own style. OK and Remove save; Esc and Cancel change nothing.</summary>
public sealed class LabelDialog : Window
{
    public LabelDialog(string character, AppSettings s, Action saved)
    {
        string T(string key, string english) => LocalizationService.Str(key, english);
        Title = T("L.Lbl.Title", "Edit Thumbnail Label");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var style = s.ThumbnailLabelStyles.GetValueOrDefault(character);
        var text = new TextBox { Text = CharacterFields.Label(s, character) };
        var colour = new TextBox { Text = style?.Color is { Length: > 0 } c ? Ui.Hex(c) : "", Width = 120, MaxLength = 9, PlaceholderText = "#RRGGBB" };
        var size = new TextBox { Text = style is { Size: > 0 } ? style.Size.ToString() : "", Width = 80, MaxLength = 3, HorizontalAlignment = HorizontalAlignment.Left };
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        void Paint()
        {
            var t = colour.Text?.Trim() ?? "";
            bool good = t.Length == 0 || ThumbnailFields.IsPaintable(t);
            if (good) colour.ClearValue(TextBox.ForegroundProperty); else colour.Foreground = Brushes.IndianRed;
            var parsed = AppSettings.ParseColor(good && t.Length > 0 ? t : "", System.Windows.Media.Color.FromArgb(0, 0, 0, 0));
            swatch.Background = new SolidColorBrush(Color.FromArgb(parsed.A, parsed.R, parsed.G, parsed.B));
        }
        Paint();
        Ui.Watch(colour, TextBox.TextProperty, _ => Paint());

        var ok = new Button { Content = T("L.Common.Ok", "OK"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = T("L.Common.Cancel", "Cancel"), IsCancel = true, MinWidth = 80 };
        var remove = new Button { Content = Ui.NoAccess(Ui.Plain(T("L.Lbl.Remove", "Remove Label"))) };
        ok.Click += (_, _) =>
        {
            var c = colour.Text?.Trim() ?? "";
            if (c.Length > 0 && !ThumbnailFields.IsPaintable(c)) { colour.Focus(); return; }
            int px = int.TryParse(size.Text?.Trim(), out var n) && n > 0 ? Math.Min(n, 200) : 0;
            CharacterFields.SetLabel(s, character, text.Text);
            if (c.Length == 0 && px == 0) s.ThumbnailLabelStyles.Remove(character);
            else s.ThumbnailLabelStyles[character] = new ThumbnailLabelStyle { Color = c.Length > 0 ? Ui.Hex(c) : "", Size = px };
            saved();
            Close();
        };
        remove.Click += (_, _) =>
        {
            s.ThumbnailAnnotations.Remove(character);
            s.ThumbnailLabelStyles.Remove(character);
            saved();
            Close();
        };
        cancel.Click += (_, _) => Close();

        var colourRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        colourRow.Children.Add(swatch);
        colourRow.Children.Add(colour);
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(remove, Dock.Left);
        buttons.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(cancel);
        right.Children.Add(ok);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var body = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = string.Format(T("L.Lbl.Header", "Label for “{0}”"), StreamerMask.Name(StreamerKind.Character, character)),
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
        });
        body.Children.Add(new TextBlock { Text = T("L.Lbl.LabelText", "Label text (e.g. Scout, DPS, Logi)") });
        body.Children.Add(text);
        body.Children.Add(new TextBlock { Text = T("L.Lbl.TextColor", "Text color"), Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(colourRow);
        body.Children.Add(new TextBlock { Text = T("L.Lbl.TextSize", "Text size (pt) — leave blank or 0 to use default"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(size);
        body.Children.Add(buttons);
        Content = body;

        Opened += (_, _) => { text.Focus(); text.SelectAll(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
