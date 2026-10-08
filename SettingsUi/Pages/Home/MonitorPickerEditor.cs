using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.SettingsUi;

/// <summary>Preferred monitor (AppSettings.PreferredMonitor, 1-based; 0 = none), listed as the old
/// Layout page did: "Monitor 1: 2560×1440 (primary)". Re-read whenever the page is shown.</summary>
public sealed class MonitorPickerEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private bool _loading;

    public MonitorPickerEditor(SettingsContext ctx, string id)
    {
        _ctx = ctx;
        List.SelectionChanged += (_, _) =>
        {
            if (_loading || List.SelectedIndex < 0) return;
            _ctx.S.PreferredMonitor = List.SelectedIndex + 1;
            _ctx.Applier.Request(ApplyScope.Thumbnails);   // where new thumbnails and unsaved ones go
        };
        Content = EditorCard.Build(id, List);
        Refresh();
    }

    public ComboBox List { get; } = new() { MinWidth = 260 };

    public void Refresh()
    {
        _loading = true;
        List.Items.Clear();
        var screens = WinForms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var text = string.Format(SettingsContext.Str("L.S.Monitor.Item"), i + 1, screens[i].Bounds.Width, screens[i].Bounds.Height);
            if (screens[i].Primary) text += " " + SettingsContext.Str("L.S.Monitor.Primary");
            List.Items.Add(text);
        }
        var chosen = _ctx.S.PreferredMonitor;
        List.SelectedIndex = chosen >= 1 && chosen <= screens.Length ? chosen - 1 : -1;
        _loading = false;
    }
}
