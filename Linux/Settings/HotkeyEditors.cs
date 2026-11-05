using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S6: Clients › Hotkeys &amp; switching › Keys used twice, the Avalonia port of HotkeyConflictsEditor (keep in sync)
/// over the shared HotkeyUses / HotkeyConflicts, counting only the keys of the cards Linux shows (the keys Linux binds).
/// Worked out again every second while it shows, when a key or the wildcard switch changed; each use opens its card.</summary>
internal sealed partial class SettingsEditors
{
    internal Control HotkeyConflictsCard(SettingsContext ctx)
    {
        var panel = Titled("clients.hotkeys.conflicts");
        var summary = LinuxLooks.Text("", "Label");
        summary.TextWrapping = TextWrapping.Wrap;
        var rows = new StackPanel();
        panel.Children.Add(summary);
        panel.Children.Add(rows);
        string signature = "\0";
        IReadOnlyList<HotkeyUse> Uses() => HotkeyUses.All(ctx.S, LinuxSupport.Settings).ToList();
        string Signature(IReadOnlyList<HotkeyUse> uses) => ctx.S.HotkeyWildcardModifiers + "|" + string.Join("\n", uses.Select(u => u.Id + "=" + u.Hotkey));

        void Refresh()
        {
            var uses = Uses();
            var now = Signature(uses);
            if (now == signature) return;
            signature = now;
            var clashes = HotkeyConflicts.Find(uses, ctx.S.HotkeyWildcardModifiers);
            summary.Text = clashes.Count == 0 ? S3("L.S.Conflicts.None") : string.Format(S3("L.S.Conflicts.Count"), clashes.Count);
            rows.Children.Clear();
            foreach (var clash in clashes)
            {
                var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(new TextBlock { Text = clash.KeyText, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
                row.Children.Add(Link(clash.First));
                row.Children.Add(Link(clash.Second));
                rows.Children.Add(row);
            }
        }

        Control Link(HotkeyUse use)
        {
            var target = HotkeyConflicts.Locate(use.Id, LinuxSupport.Pages);
            var button = new Button { Content = new TextBlock { Text = use.Label }, Margin = new Thickness(0, 0, 8, 0), IsEnabled = target != null };
            button.Click += (_, _) => { if (target is { } t) ctx.Host.Navigate(t.PageId, t.ItemId); };
            return button;
        }

        WhileShown(panel, TimeSpan.FromSeconds(1), Refresh);
        Refresh();
        return panel;
    }
}
