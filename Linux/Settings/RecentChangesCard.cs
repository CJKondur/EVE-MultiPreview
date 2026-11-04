using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux side of the Windows RecentChangesEditor (App settings › General › Recently changed, IDEAS P17): the last
/// simple settings changed in this run, newest first, what each was and is, when, and Undo. Undo goes through the shared
/// SettingChanges.Undo, which refuses a setting that has changed since; nothing is written to disk.</summary>
internal sealed class RecentChangesCard : UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _none;

    public RecentChangesCard(SettingsContext ctx)
    {
        _ctx = ctx;
        _none = Note("L.S.Changes.None");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = SettingsContext.Str("L.S." + EditorIds.RecentChangesEditor), TextWrapping = TextWrapping.Wrap });
        if (LocalizationService.Str("L.S." + EditorIds.RecentChangesEditor + ".Desc", "") is { Length: > 0 } desc)
            panel.Children.Add(new TextBlock { Text = desc, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_rows);
        panel.Children.Add(_none);
        var notListed = Note("L.S.Changes.NotListed");
        notListed.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(notListed);
        Content = panel;
        Refresh();
    }

    public void Refresh()
    {
        var shown = _ctx.Service.Changes.Items.ToList();
        _rows.Children.Clear();
        foreach (var change in shown) _rows.Children.Add(Row(change));
        _none.IsVisible = shown.Count == 0;
    }

    /// <summary>Undo runs the setting's own side effect (OnChanged); one a Linux stand-in refuses, or any other failure, is
    /// said as a toast and the list is drawn again — it never takes the app with it.</summary>
    private void Undo(SettingChanges.Change change)
    {
        try
        {
            if (_ctx.Service.Changes.Undo(change, _ctx)) _ctx.Host.Reload();
            else _ctx.Host.Toast(SettingsContext.Str(SettingChanges.BelongsToAnotherProfile(change, _ctx.S) ? "L.S.Changes.OtherProfile" : "L.S.Changes.Refused"));
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine("[Settings] undo: " + ex.Message);
            _ctx.Host.Toast(string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), SettingsContext.Str(change.LabelKey)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[Settings] undo {change.Id}: {ex.GetType().Name}");
            _ctx.Host.Toast(SettingsContext.Str("L.S.Changes.Refused"));
        }
        Refresh();
    }

    private Control Row(SettingChanges.Change change)
    {
        var undo = new Button { Content = Ui.NoAccess(SettingsContext.Str("L.S.Changes.Undo")), VerticalAlignment = VerticalAlignment.Center };
        undo.Click += (_, _) => Undo(change);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = SettingsContext.Str(change.LabelKey), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = change.OldText + " → " + change.NewText, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = change.Time.ToString("g", CultureInfo.CurrentCulture), Opacity = 0.6, FontSize = 12 });
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        DockPanel.SetDock(undo, Dock.Right);
        row.Children.Add(undo);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock Note(string key) => new() { Text = SettingsContext.Str(key), Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
}
