using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

internal sealed partial class SettingsEditors
{
    // The Discord and ntfy columns of the event table (columns 2 and 3): per event, whether that way sends it
    // (PushEvents), as on Windows. Each is there only while its way is switched on when the page is built.
    private const int DiscordColumn = 2, NtfyColumn = 3;

    private void AddPushHeadings(Grid table)
    {
        if (S.PushAlerts) table.Children.Add(PushHeading(T("L.S.Events.ColDiscord", "Discord"), DiscordColumn));
        if (S.NtfyAlerts) table.Children.Add(PushHeading(T("L.S.Events.ColNtfy", "ntfy"), NtfyColumn));
    }

    private void AddPushTicks(Grid table, string key, string name, int row)
    {
        if (S.PushAlerts)
            table.Children.Add(PushTick(PushEvents.Sends(S.PushAlertTypes, key), on => PushEvents.Set(S.PushAlertTypes, key, on),
                string.Format(T("L.S.Events.DiscordFor", "Send {0} to Discord"), name), row, DiscordColumn));
        if (S.NtfyAlerts)
            table.Children.Add(PushTick(PushEvents.Sends(S.NtfyAlertTypes, key), on => PushEvents.Set(S.NtfyAlertTypes, key, on),
                string.Format(T("L.S.Events.NtfyFor", "Send {0} to ntfy"), name), row, NtfyColumn));
    }

    private static TextBlock PushHeading(string text, int column)
    {
        var heading = new TextBlock { Text = text, Opacity = 0.6, Margin = new Avalonia.Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(heading, T("L.S.Events.PushColumnTip", "Shown while that service is switched on in Alerts › Alert hub. Its severity and \"only while away\" settings still apply."));
        Grid.SetColumn(heading, column);
        return heading;
    }

    private CheckBox PushTick(bool on, Action<bool> set, string name, int row, int column)
    {
        var tick = new CheckBox { IsChecked = on, Margin = new Avalonia.Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(tick, name);
        ToolTip.SetTip(tick, name);
        tick.IsCheckedChanged += (_, _) =>
        {
            set(tick.IsChecked == true);
            Save();
            _host.AlertsChanged();
        };
        Grid.SetRow(tick, row);
        Grid.SetColumn(tick, column);
        return tick;
    }
}
