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
    /// <summary>The characters table (label, hotkey, hidden, second thumbnail) and the row that adds a character: the
    /// "clients.characters.table" (also Home's "home.tiles").</summary>
    internal Control CharactersTable()
    {
        var page = new StackPanel { Spacing = 2 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,170,210,Auto,Auto"), Margin = new Thickness(0, 6) };
        var host = new StackPanel { Spacing = 0 };
        page.Children.Add(Ui.Card(host));
        host.Children.Add(grid);

        int row = 0;
        void Cell(Control c, int column)
        {
            c.Margin = new Thickness(0, 3, 10, 3);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }
        void Header()
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (text, column) in new[] { (T("L.Common.Character", "Character"), 0), (T("L.Common.Label", "Label"), 1), (T("L.Common.Hotkey", "Hotkey"), 2), (T("L.Linux.Hidden", "Hidden"), 3), (T("L.S.character.pip", "Second thumbnail (PiP)"), 4) })
                Cell(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 }, column);
            row++;
        }
        void Add(string name)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var running = _host.RunningCharacters().Contains(name, StringComparer.OrdinalIgnoreCase);
            Cell(new TextBlock { Text = name, Opacity = running ? 1 : 0.6 }, 0);
            var label = Ui.Text(CharacterFields.Label(S, name), v => { CharacterFields.SetLabel(S, name, v); Save(); }, 155);
            label.Margin = new Thickness(0);
            Cell(label, 1);
            var profile = CharacterDirectory.Current(S);
            Cell(HotkeyField("char:" + name, profile.Hotkeys.TryGetValue(name, out var b) ? b.Key : "", key =>
            {
                if (key.Length == 0) profile.Hotkeys.Remove(name);
                else profile.Hotkeys[name] = new HotkeyBinding { Key = key };
            }), 2);
            Cell(Ui.Check("", S.ThumbnailVisibility.TryGetValue(name, out var hidden) && hidden != 0, on =>
            {
                if (on) S.ThumbnailVisibility[name] = 1;
                else S.ThumbnailVisibility.Remove(name);
                Save();
            }), 3);
            // The second thumbnail (CharacterFields.SetPip: off keeps its place and size) and its opacity in percent (8–100).
            // The app makes or closes it within 2 s (SyncPips) and reads the opacity at every tick.
            var pipOpacity = Ui.Number(8, 100, CharacterFields.PipOpacity(S, name), v => { CharacterFields.SetPipOpacity(S, name, v); Save(); });
            pipOpacity.Width = 95;
            pipOpacity.IsEnabled = CharacterFields.PipOn(S, name);
            ToolTip.SetTip(pipOpacity, T("L.S.character.pipOpacity", "Second thumbnail opacity"));
            var pip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            pip.Children.Add(Ui.Check("", CharacterFields.PipOn(S, name), on => { CharacterFields.SetPip(S, name, on); pipOpacity.IsEnabled = on; Save(); }));
            pip.Children.Add(WithUnit(pipOpacity, T("L.S.Unit.Percent", "%")));
            Cell(pip, 4);
            row++;
        }

        var known = CharacterDirectory.Known(S, _host.RunningCharacters());
        if (known.Count == 0) host.Children.Insert(0, Ui.Note(T("L.Linux.NoCharacters", "No characters yet. They appear here once an EVE client is running.")));
        else
        {
            Header();
            foreach (var name in known) Add(name);
        }

        var newName = new TextBox { Width = 220, PlaceholderText = T("L.Common.Character", "Character") };
        var add = new Button { Content = Ui.Plain(T("L.Common.AddCharacter", "Add Character")) };
        add.Click += (_, _) =>
        {
            var name = newName.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            if (!S.ThumbnailVisibility.ContainsKey(name)) S.ThumbnailVisibility[name] = 0;   // 0 = shown; it makes the name known
            Save();
            _refresh();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        addRow.Children.Add(newName);
        addRow.Children.Add(add);
        page.Children.Add(addRow);
        return page;
    }
}
