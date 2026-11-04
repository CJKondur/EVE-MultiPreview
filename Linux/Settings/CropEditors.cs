using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

internal sealed partial class SettingsEditors
{
    /// <summary>The character the Crop windows page edits; kept while the page is built again.</summary>
    private string? _cropCharacter;
    /// <summary>The crop windows as the page shows them (CropEdits.Signature): a crop window dragged on the screen changes
    /// them, and the page is built again to show its new place.</summary>
    private string _cropShown = "";

    /// <summary>The character whose crop windows are edited, a card per crop window, Add, and the tip: "thumbs.crops.editor"
    /// (whose section already has the heading).</summary>
    internal Control CropList()
    {
        var page = new StackPanel { Spacing = 2 };
        CropList(page);
        return page;
    }

    private void CropList(StackPanel page)
    {
        var characters = CropEdits.Characters(S, _host.RunningCharacters());
        if (characters.Count == 0)
        {
            page.Children.Add(Ui.Note(T("L.S.Crops.NoCharacters", "No characters yet. Start EVE and log in first.")));
            return;
        }
        if (_cropCharacter == null || !characters.Contains(_cropCharacter, StringComparer.OrdinalIgnoreCase)) _cropCharacter = characters[0];
        var character = _cropCharacter;
        var pick = new ComboBox { Width = 280, ItemsSource = characters, SelectedItem = characters.First(c => string.Equals(c, character, StringComparison.OrdinalIgnoreCase)) };
        pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string c && c != _cropCharacter) { _cropCharacter = c; _refresh(); } };
        page.Children.Add(Ui.Row(T("L.S.thumbs.crops.editor", "Character"), pick,
            T("L.S.thumbs.crops.editor.Desc", "Whose crop windows to edit. Every character MultiPreview knows is listed, running ones first.")));

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4) };
        var crops = CropEdits.Of(S, character);
        if (crops.Count == 0)
            page.Children.Add(Ui.Note(string.Format(T("L.S.Crops.Empty", "{0} has no crop windows yet. Add one, then press Edit area while the client is running."), character)));
        foreach (var def in crops.ToList()) page.Children.Add(CropCard(character, def, characters, status));

        var add = new Button { Content = Ui.NoAccess(T("L.S.Act.thumbs.crops.add", "_Add a crop window")), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            CropEdits.Add(S, character, CropEdits.NextName(S, character, T("L.S.Crops.DefaultName", "Crop {0}")));
            Save();
            _refresh();
        };
        page.Children.Add(add);
        page.Children.Add(status);
        page.Children.Add(Ui.Note(T("L.S.Crops.Tip", "Drag a crop window with the right mouse button to move it; press the left button too to resize it.")));

        // A crop window moved or resized on the screen: build the page again so its boxes show where it is now.
        _cropShown = CropEdits.Signature(S, character);
        var watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (CropEdits.Signature(S, character) != _cropShown) _refresh();
        });
        page.AttachedToVisualTree += (_, _) => watch.Start();
        page.DetachedFromVisualTree += (_, _) => watch.Stop();
    }

    /// <summary>One crop window: its name, "Show the name", the part of the client and the window on screen (four numbers
    /// each), opacity, "Clicks pass through", and Edit area…, Copy to… and Delete….</summary>
    private Control CropCard(string character, CropDefinition def, IReadOnlyList<string> characters, TextBlock status)
    {
        void Changed() { Save(); _cropShown = CropEdits.Signature(S, character); }
        var body = new StackPanel { Spacing = 4 };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = Ui.Text(def.Name, v => { def.Name = v; Changed(); }, 260);
        name.FontWeight = FontWeight.SemiBold;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        title.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var edit = new Button { Content = T("L.S.Crops.EditArea", "Edit area…") };
        edit.Click += (_, _) =>
        {
            var error = _host.PickCropArea(character, def, () => { _cropShown = ""; });   // the page is built again when it shows
            LinuxLooks.Paint(status, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
            status.Text = error == null ? "" : string.Format(T(error, error), character);
        };
        buttons.Children.Add(edit);
        buttons.Children.Add(CopyButton(character, def, characters, status));
        buttons.Children.Add(DeleteButton(character, def));
        Grid.SetColumn(buttons, 1);
        title.Children.Add(buttons);
        body.Children.Add(title);

        body.Children.Add(Ui.Row(T("L.S.Crops.ShowName", "Show the name"), Ui.Check("", def.ShowLabel, v => { def.ShowLabel = v; Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Area", "Part of the client"),
            Rect((def.SourceX, def.SourceY, def.SourceWidth, def.SourceHeight), CropEdits.SourceMin, r => { CropEdits.SetSource(def, r); Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Window", "Window on screen"),
            Rect((def.PopupX, def.PopupY, def.PopupWidth, def.PopupHeight), CropEdits.PopupMin, r => { CropEdits.SetPopup(def, r); Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Opacity", "Opacity"),
            Ui.Slider(CropEdits.MinOpacity, CropEdits.MaxOpacity, def.Opacity, v => { def.Opacity = v; Changed(); }, v => v + " %")));
        body.Children.Add(Ui.Row(T("L.S.Crops.ClickThrough", "Clicks pass through"), Ui.Check("", def.ClickThrough, v => { def.ClickThrough = v; Changed(); })));
        return Ui.Card(body);
    }

    /// <summary>X, Y, width and height boxes: each held to its minimum and CropEdits.Limit.</summary>
    private static Control Rect((int X, int Y, int W, int H) value, (int X, int Y, int W, int H) min, Action<(int X, int Y, int W, int H)> set)
    {
        // Two lines, X and Y over width and height, so the boxes line up in every language.
        var r = value;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        void Box(int at, string label, int low, int v, Action<int> store)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.8 };
            var box = Ui.Number(low, CropEdits.Limit, v, n => { store(n); set(r); });
            box.Width = 120;
            box.Margin = new Thickness(0, 2, 16, 2);
            Grid.SetRow(text, at / 2);
            Grid.SetColumn(text, at % 2 * 2);
            Grid.SetRow(box, at / 2);
            Grid.SetColumn(box, at % 2 * 2 + 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }
        Box(0, T("L.S.Slot.X", "X"), min.X, r.X, n => r.X = n);
        Box(1, T("L.S.Slot.Y", "Y"), min.Y, r.Y, n => r.Y = n);
        Box(2, T("L.S.Slot.W", "Width"), min.W, r.W, n => r.W = n);
        Box(3, T("L.S.Slot.H", "Height"), min.H, r.H, n => r.H = n);
        return grid;
    }

    /// <summary>"Copy to…" (CropsEditor.ShowCopyMenu): all other characters, or one of them.</summary>
    private Control CopyButton(string character, CropDefinition def, IReadOnlyList<string> characters, TextBlock status)
    {
        var others = characters.Where(c => !string.Equals(c, character, StringComparison.OrdinalIgnoreCase)).ToList();
        var copy = new Button { Content = T("L.S.Crops.CopyTo", "Copy to…") };
        var menu = new ContextMenu();
        void CopyTo(IReadOnlyList<string> targets)
        {
            int made = CropEdits.CopyTo(S, def, targets);
            Save();
            LinuxLooks.Paint(status, TextBlock.ForegroundProperty, "Fx.InfoBrush");
            status.Text = made == 1
                ? string.Format(T("L.S.Crops.Copied", "Copied '{0}' to {1}."), def.Name, targets[0])
                : string.Format(T("L.S.Crops.CopiedMany", "Copied '{0}' to {1} characters."), def.Name, made);
        }
        if (others.Count == 0) menu.Items.Add(new MenuItem { Header = new TextBlock { Text = T("L.S.Crops.CopyNone", "No other characters") }, IsEnabled = false });
        else
        {
            var all = new MenuItem { Header = new TextBlock { Text = string.Format(T("L.S.Crops.CopyAll", "All other characters ({0})"), others.Count) } };
            all.Click += (_, _) => CopyTo(others);
            menu.Items.Add(all);
            menu.Items.Add(new Separator());
            foreach (var other in others)
            {
                var item = new MenuItem { Header = new TextBlock { Text = other } };
                item.Click += (_, _) => CopyTo(new[] { other });
                menu.Items.Add(item);
            }
        }
        copy.Click += (_, _) => menu.Open(copy);
        return copy;
    }

    /// <summary>"Delete…": asks first (L.S.Crops.DeleteConfirm), as the Windows page does.</summary>
    private Control DeleteButton(string character, CropDefinition def)
    {
        var delete = new Button { Content = T("L.S.Crops.Delete", "Delete…") };
        var yes = new Button { Content = T("L.Common.DeleteWord", "Delete") };
        var no = new Button { Content = T("L.Common.Cancel", "Cancel") };
        var ask = new StackPanel { Spacing = 8, MaxWidth = 320 };
        ask.Children.Add(new TextBlock { Text = T("L.S.Crops.DeleteTitle", "Delete crop window"), FontWeight = FontWeight.SemiBold });
        ask.Children.Add(new TextBlock { Text = string.Format(T("L.S.Crops.DeleteConfirm", "Delete the crop window '{0}'?"), def.Name), TextWrapping = TextWrapping.Wrap });
        var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        answers.Children.Add(yes);
        answers.Children.Add(no);
        ask.Children.Add(answers);
        var flyout = new Flyout { Content = ask };
        delete.Flyout = flyout;
        no.Click += (_, _) => flyout.Hide();
        yes.Click += (_, _) =>
        {
            flyout.Hide();
            CropEdits.Delete(S, character, def.Id);
            Save();
            _refresh();
        };
        return delete;
    }
}
