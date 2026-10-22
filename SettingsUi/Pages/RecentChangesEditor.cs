using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › General › Recently changed (IDEAS P17): the last 20 simple settings changed in this run of the
/// app, newest first, each with what it was and is as its own card shows it, when, and an Undo button. It writes no
/// setting of its own: Undo goes through <see cref="SettingChanges.Undo"/>, which refuses a setting that has changed since.
/// A character's setting is listed by its label alone. Nothing is written to disk.</summary>
public sealed class RecentChangesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "app.general.recent";
    private readonly SettingsContext _ctx;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _none = EditorCard.Note("L.S.Changes.None");
    private readonly List<Button> _buttons = new();

    public RecentChangesEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var below = new StackPanel();
        below.Children.Add(_rows);
        below.Children.Add(_none);
        var note = EditorCard.Note("L.S.Changes.NotListed");
        note.Margin = new Thickness(0, 8, 0, 0);
        below.Children.Add(note);
        Content = EditorCard.Build(ItemId, null, below);
        Refresh();
    }

    /// <summary>The Undo button of each row shown, newest first.</summary>
    public IReadOnlyList<Button> UndoButtons => _buttons;

    /// <summary>The line that says nothing was changed yet shows.</summary>
    public bool ShowsEmptyState => _none.Visibility == Visibility.Visible;

    /// <summary>The changes the rows show, newest first.</summary>
    public IReadOnlyList<SettingChanges.Change> Shown { get; private set; } = new List<SettingChanges.Change>();

    /// <summary>Called when the page is shown or refreshed, and after every undo.</summary>
    public void Refresh()
    {
        Shown = _ctx.Service.Changes.Items.ToList();
        _rows.Children.Clear();
        _buttons.Clear();
        foreach (var change in Shown) _rows.Children.Add(Row(change));
        _none.Visibility = Shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts one change back; the cards re-read their settings. A setting that changed since is left alone and the
    /// player is told.</summary>
    public void Undo(SettingChanges.Change change)
    {
        if (_ctx.Service.Changes.Undo(change, _ctx)) _ctx.Host.Reload();
        else _ctx.Host.Toast(SettingsContext.Str("L.S.Changes.Refused"));
        Refresh();
    }

    private FrameworkElement Row(SettingChanges.Change change)
    {
        var undo = EditorCard.Button("L.S.Changes.Undo", () => Undo(change));
        AutomationProperties.SetName(undo, SettingsContext.Str("L.S.Changes.Undo") + ": " + SettingsContext.Str(change.LabelKey));
        undo.VerticalAlignment = VerticalAlignment.Center;
        _buttons.Add(undo);

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(EditorCard.Note(change.LabelKey, strong: true));
        text.Children.Add(EditorCard.Value(change.OldText + " → " + change.NewText));
        text.Children.Add(EditorCard.Value(change.Time.ToString("g", CultureInfo.CurrentCulture), secondary: true));

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(undo, Dock.Right);
        row.Children.Add(undo);
        row.Children.Add(text);
        return row;
    }
}
