using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.SettingsUi;

/// <summary>Tools › Stats overlay › Numbers per character (old LvStatCharacters): every known character with a
/// summary of its own numbers and › to its page at Choose numbers. Follows running clients by itself, so the old
/// Refresh button has no job. Reads only.</summary>
public sealed class StatCharactersEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly Grid _table = new();
    private string _signature = "";

    public StatCharactersEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = EditorCard.Build("tools.stats.characterTable", null, _table);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); });
        Refresh();
    }

    /// <summary>The characters listed, in order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private IReadOnlyList<string> Current() => CharacterDirectory.Known(_ctx.S, CharacterDirectory.Running(_ctx));

    private string Signature() => string.Join("\n", Current().Select(n => n + "|" + Summary(n)));

    /// <summary>"Default", or how many numbers this character always and never shows.</summary>
    public string Summary(string name)
    {
        var (shown, hidden) = StatsFields.Counts(_ctx.S, name);
        return shown == 0 && hidden == 0
            ? SettingsContext.Str("L.S.StatsTable.Default")
            : string.Format(CultureInfo.CurrentCulture, SettingsContext.Str("L.S.StatsTable.Summary"), shown, hidden);
    }

    public void Refresh()
    {
        Names = Current();
        _signature = Signature();
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int header = AddRow();
        Place(EditorCard.Note("L.S.Characters.ColCharacter", strong: true), header, 0);
        Place(EditorCard.Note("L.S.StatsTable.ColNumbers", strong: true), header, 1);
        if (Names.Count == 0)
        {
            var empty = EditorCard.Note("L.S.StatsTable.Empty");
            Place(empty, AddRow(), 0);
            Grid.SetColumnSpan(empty, 3);
            return;
        }
        foreach (var name in Names)
        {
            int r = AddRow();
            Place(EditorCard.Value(name, strong: true), r, 0);
            Place(EditorCard.Value(Summary(name), secondary: true), r, 1);
            Place(EditorCard.Open(string.Format(SettingsContext.Str("L.S.Characters.Open"), name), () => Open(name)), r, 2);
        }
    }

    /// <summary>The character's page, at Choose numbers.</summary>
    public void Open(string name) => _ctx.Host.Navigate(CharacterPage.IdFor(name), "character.statsPicker");

    private int AddRow()
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _table.RowDefinitions.Count - 1;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _table.Children.Add(element);
    }
}
