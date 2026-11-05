using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S7: the stats overlay's editors, Avalonia ports of StatMetricsEditor (Tools › Stats overlay › Numbers for every
/// character), StatCharactersEditor (Numbers per character) and StatPickerEditor (a character's Choose numbers): keep in
/// sync. Each write goes through the shared StatsFields and asks for a re-apply, which the app follows within a second
/// (Previews.SyncStats opens or closes the character's stats window).</summary>
internal sealed partial class SettingsEditors
{
    /// <summary>One switch per number, in its group's column. Only the number's own bit changes.</summary>
    internal Control StatMetricsCard(SettingsContext ctx)
    {
        var panel = Titled("tools.stats.metrics");
        var groups = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (group, labelKey, _) in StatsFields.Groups)
        {
            var column = new StackPanel { MinWidth = 260, Margin = new Thickness(0, 0, 32, 12), Spacing = 2 };
            column.Children.Add(LinuxLooks.Text(S3(labelKey), "Label"));
            foreach (var m in StatsFields.Metrics.Where(x => x.Group == group))
            {
                var bit = m.Bit;
                column.Children.Add(Ui.Check(S3(m.LabelKey), StatsFields.IsOn(ctx.S.GlobalStatMetrics, bit), on =>
                {
                    if (StatsFields.IsOn(ctx.S.GlobalStatMetrics, bit) == on) return;
                    StatsFields.SetGlobal(ctx.S, bit, on);
                    ctx.Applier.Request(ApplyScope.Thumbnails);
                }));
            }
            groups.Children.Add(column);
        }
        panel.Children.Add(groups);
        return panel;
    }

    /// <summary>"Default", or how many numbers the character always and never shows (StatCharactersEditor.Summary).</summary>
    internal static string StatsSummary(AppSettings s, string name)
    {
        var (shown, hidden) = StatsFields.Counts(s, name);
        return shown == 0 && hidden == 0
            ? S3("L.S.StatsTable.Default")
            : string.Format(CultureInfo.CurrentCulture, S3("L.S.StatsTable.Summary"), shown, hidden);
    }

    /// <summary>Every known character with its summary and a button to its page at Choose numbers. Reads only; follows running
    /// clients and the pickers by itself (looked at every second while shown, drawn again only when something changed).</summary>
    internal Control StatCharactersCard(SettingsContext ctx)
    {
        var panel = Titled("tools.stats.characterTable");
        var host = new StackPanel();
        panel.Children.Add(host);
        string signature = "\0";
        IReadOnlyList<string> Names() => CharacterDirectory.Known(ctx.S, CharacterDirectory.Running(ctx));
        string Signature(IReadOnlyList<string> names) => string.Join("\n", names.Select(n => n + "|" + StatsSummary(ctx.S, n)));

        void Refresh()
        {
            var names = Names();
            var now = Signature(names);
            if (now == signature) return;
            signature = now;
            host.Children.Clear();
            if (names.Count == 0)
            {
                host.Children.Add(Note("L.S.StatsTable.Empty"));
                return;
            }
            var table = Table("Auto,*,Auto");
            int header = Row(table);
            Put(table, Head("L.S.Characters.ColCharacter"), header, 0);
            Put(table, Head("L.S.StatsTable.ColNumbers"), header, 1);
            foreach (var name in names)
            {
                int r = Row(table);
                Put(table, new TextBlock { Text = name, FontWeight = Avalonia.Media.FontWeight.SemiBold }, r, 0);
                Put(table, new TextBlock { Text = StatsSummary(ctx.S, name), Opacity = 0.7 }, r, 1);
                var open = new Button { Content = new TextBlock { Text = "›" }, Padding = new Thickness(10, 2) };
                ToolTip.SetTip(open, string.Format(S3("L.S.Characters.Open"), name));
                open.Click += (_, _) => ctx.Host.Navigate(CharacterPage.IdFor(name), "character.statsPicker");
                Put(table, open, r, 2);
            }
            host.Children.Add(table);
        }

        WhileShown(panel, TimeSpan.FromSeconds(1), Refresh);
        Refresh();
        return panel;
    }

    /// <summary>A character's Choose numbers: every number Default, Always show or Never show for this character, behind a
    /// button that opens and closes the list, and the reset. Each choice writes only its own bit (StatsFields.SetOverride).</summary>
    internal Control StatPickerCard(SettingsContext ctx, string name)
    {
        var panel = Titled("character.statsPicker");
        var toggleText = new TextBlock { Text = S3("L.S.StatsPicker.Open") };
        var toggle = new Button { Content = toggleText, Padding = new Thickness(12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        var list = new StackPanel { IsVisible = false };
        toggle.Click += (_, _) =>
        {
            list.IsVisible = !list.IsVisible;
            toggleText.Text = S3(list.IsVisible ? "L.S.StatsPicker.Close" : "L.S.StatsPicker.Open");
        };
        panel.Children.Add(toggle);
        panel.Children.Add(list);
        string signature = "";
        string Signature() => $"{(uint)ctx.S.GlobalStatMetrics}|"
            + (ctx.S.PerCharacterStats.TryGetValue(name, out var c) ? $"{(uint)c.ForcedOn}/{(uint)c.ForcedOff}" : "-");

        void Changed()
        {
            ctx.Applier.Request(ApplyScope.Thumbnails);   // the app opens or closes the character's stats window
            signature = Signature();   // our own write: no redraw under the player's mouse
            ctx.Host.Reload();          // the group switches above show the new state
        }

        void Rebuild()
        {
            signature = Signature();
            list.Children.Clear();
            foreach (var (group, labelKey, _) in StatsFields.Groups)
            {
                var title = LinuxLooks.Text(S3(labelKey), "Label");
                title.Margin = new Thickness(0, 8, 0, 4);
                list.Children.Add(title);
                foreach (var m in StatsFields.Metrics.Where(x => x.Group == group))
                {
                    var bit = m.Bit;
                    var combo = new ComboBox { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
                    var defaultKey = StatsFields.IsOn(ctx.S.GlobalStatMetrics, bit) ? "L.S.StatsPicker.DefaultShown" : "L.S.StatsPicker.DefaultHidden";
                    foreach (var key in new[] { defaultKey, "L.S.StatsPicker.Always", "L.S.StatsPicker.Never" })
                        combo.Items.Add(new TextBlock { Text = S3(key) });
                    combo.SelectedIndex = StatsFields.Override(ctx.S, name, bit) switch { true => 1, false => 2, null => 0 };   // before the handler
                    combo.SelectionChanged += (_, _) =>
                    {
                        bool? state = combo.SelectedIndex switch { 1 => true, 2 => false, _ => null };
                        if (StatsFields.Override(ctx.S, name, bit) == state) return;
                        StatsFields.SetOverride(ctx.S, name, bit, state);
                        Changed();
                    };
                    var row = new DockPanel { Margin = new Thickness(0, 2) };
                    DockPanel.SetDock(combo, Dock.Right);
                    row.Children.Add(combo);
                    row.Children.Add(new TextBlock { Text = S3(m.LabelKey), VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
                    list.Children.Add(row);
                }
            }
            var reset = Action("L.S.StatsPicker.Reset", () =>
            {
                if (!ctx.S.PerCharacterStats.ContainsKey(name)) return;
                if (!ctx.Host.Confirm(AccessKeys.Spoken(S3("L.S.StatsPicker.Reset")), S3("L.S.StatsPicker.ResetConfirm"))) return;
                StatsFields.ClearOverrides(ctx.S, name);
                Changed();
                Rebuild();
            });
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 12, 0, 0);
            list.Children.Add(reset);
        }

        // The group switches above and Tools › Stats overlay change the same numbers: followed while shown.
        WhileShown(panel, TimeSpan.FromSeconds(1), () => { if (Signature() != signature) Rebuild(); });
        Rebuild();
        return panel;
    }
}
