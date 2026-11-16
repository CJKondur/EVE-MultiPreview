using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The Discord and ntfy columns of the event table: per event, whether that way sends it (PushEvents). Kept here,
/// apart from the table's own columns, and placed last, so the table file holds only the hooks. Each column is shown
/// only while its way is switched on (Alerts › Alert hub), looked at every two seconds as the groups line is.</summary>
public sealed partial class EventTableEditor
{
    private static readonly int DiscordColumn = Columns.Length - 2;
    private static readonly int NtfyColumn = Columns.Length - 1;

    // The headers and switches of the two columns, built anew by every Refresh: (element, true for ntfy).
    private readonly List<(FrameworkElement Element, bool Ntfy)> _pushCells = new();
    private bool _pushWatched;

    /// <summary>A header as Refresh places it. The first of the two columns' headers starts a new table: what was kept
    /// of the last one goes.</summary>
    private FrameworkElement PushColumn(FrameworkElement head, int column)
    {
        if (column == DiscordColumn)
        {
            _pushCells.Clear();
            if (!_pushWatched)
            {
                _pushWatched = true;
                LiveRefresh.Attach(this, ShowPushColumns);    // Refresh runs first in the constructor, before Loaded
            }
        }
        if (column == DiscordColumn || column == NtfyColumn)
        {
            head.ToolTip = Str("L.S.Events.PushColumnTip");
            Track(head, column == NtfyColumn);
        }
        return head;
    }

    /// <summary>The row's Discord and ntfy switches.</summary>
    private void AddPushSwitches(string key, string name, int row)
    {
        var discord = EditorCard.Toggle(PushEvents.Sends(_ctx.S.PushAlertTypes, key),
            on => Store(() => PushEvents.Set(_ctx.S.PushAlertTypes, key, on)));
        AutomationProperties.SetName(discord, string.Format(Str("L.S.Events.DiscordFor"), name));
        Place(discord, row, DiscordColumn);
        Track(discord, ntfy: false);

        var ntfy = EditorCard.Toggle(PushEvents.Sends(_ctx.S.NtfyAlertTypes, key),
            on => Store(() => PushEvents.Set(_ctx.S.NtfyAlertTypes, key, on)));
        AutomationProperties.SetName(ntfy, string.Format(Str("L.S.Events.NtfyFor"), name));
        Place(ntfy, row, NtfyColumn);
        Track(ntfy, ntfy: true);
    }

    private void Track(FrameworkElement element, bool ntfy)
    {
        _pushCells.Add((element, ntfy));
        element.Visibility = Shown(ntfy);
    }

    /// <summary>A column whose way is off is collapsed: its width is then nothing.</summary>
    private void ShowPushColumns()
    {
        foreach (var (element, ntfy) in _pushCells) element.Visibility = Shown(ntfy);
    }

    private Visibility Shown(bool ntfy) =>
        (ntfy ? _ctx.S.NtfyAlerts : _ctx.S.PushAlerts) ? Visibility.Visible : Visibility.Collapsed;
}
