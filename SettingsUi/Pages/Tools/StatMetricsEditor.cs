using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Tools › Stats overlay › Numbers for every character (the old ChkGm* grid): one switch per number in
/// four groups. A character shows these unless its page says otherwise.</summary>
public sealed class StatMetricsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly WrapPanel _groups = new();

    public StatMetricsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = EditorCard.Build("tools.stats.metrics", null, _groups);
        Refresh();
    }

    public void Refresh()
    {
        _groups.Children.Clear();
        foreach (var (group, labelKey, _) in StatsFields.Groups)
        {
            var column = new StackPanel { MinWidth = 260, Margin = new Thickness(0, 0, 32, 12) };
            column.Children.Add(EditorCard.Note(labelKey, strong: true));
            foreach (var m in StatsFields.Metrics.Where(x => x.Group == group))
            {
                var bit = m.Bit;
                var toggle = EditorCard.Toggle(StatsFields.IsOn(_ctx.S.GlobalStatMetrics, bit), on => Set(bit, on));
                Loc.SetNameKey(toggle, m.LabelKey);
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
                label.SetResourceReference(StyleProperty, "Fx.Label");
                Loc.SetKey(label, m.LabelKey);
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);
                row.Children.Add(label);
                column.Children.Add(row);
            }
            _groups.Children.Add(column);
        }
    }

    /// <summary>Only this number's bit changes. A re-apply opens a stats window for a character that now has numbers.</summary>
    public void Set(StatMetrics bit, bool on)
    {
        if (StatsFields.IsOn(_ctx.S.GlobalStatMetrics, bit) == on) return;
        StatsFields.SetGlobal(_ctx.S, bit, on);
        _ctx.Applier.Request(ApplyScope.Thumbnails);
    }
}
