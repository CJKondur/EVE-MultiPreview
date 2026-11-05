using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S6: Clients › Windows &amp; positions › Fixed slots, the Avalonia port of the Windows SlotsEditor (keep in sync) over
/// the shared SlotEditing rules. Every edit writes the current profile and only saves: the app reads the slots at each switch
/// (Previews.ApplyClientSlot). Monitors are Avalonia's screens: a slot keeps the output's name and resolution, and a slot
/// made on Windows (\\.\DISPLAYn) finds the first monitor of its resolution, as ThumbnailManager.FindSlotScreen does.</summary>
internal sealed partial class SettingsEditors
{
    /// <summary>The slots card: Add slot / Snap clients now, "no default slot", one card per slot, then Never move.</summary>
    internal Control SlotsCard(SettingsContext ctx)
    {
        var root = new StackPanel { Spacing = 6 };
        string group = "Slots" + Guid.NewGuid().ToString("N");
        Profile P() => CharacterDirectory.Current(ctx.S);
        void Saved() => ctx.Applier.Request(ApplyScope.Save);
        IReadOnlyList<Avalonia.Platform.Screen> Screens() => _window.Screens?.All ?? Array.Empty<Avalonia.Platform.Screen>();
        string? Pick(string title) => ctx.Host.PickCharacter(title, CharacterDirectory.AllKnown(ctx.S, CharacterDirectory.Running(ctx)));

        void Show()
        {
            root.Children.Clear();
            var head = Titled("clients.windows.slots");
            head.Children.Add(Buttons(
                Action("L.S.Act.clients.windows.addSlot", () =>
                {
                    var screens = Screens();
                    var sc = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
                    if (sc == null) return;
                    SlotEditing.Add(P(), sc.DisplayName ?? "", sc.Bounds.Width, sc.Bounds.Height);
                    Saved();
                    Show();
                }),
                Action("L.S.Act.clients.windows.snapNow", _host.ApplyAllClientSlots)));
            var none = new RadioButton
            {
                GroupName = group,
                Content = new TextBlock { Text = S3("L.S.Slot.NoDefault"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                IsChecked = string.IsNullOrEmpty(P().DefaultClientSlotId) || P().ClientSlots.All(s => s.Id != P().DefaultClientSlotId),
            };
            none.IsCheckedChanged += (_, _) => { if (none.IsChecked == true && P().DefaultClientSlotId != "") { P().DefaultClientSlotId = ""; Saved(); Show(); } };
            head.Children.Add(none);
            root.Children.Add(head);
            foreach (var slot in P().ClientSlots.ToList()) root.Children.Add(Ui.Card(SlotCard(slot)));

            var never = Titled("clients.windows.neverMove");
            never.Children.Add(Names(P().ClientSlotExcluded,
                n => { SlotEditing.Include(P(), n); Saved(); Show(); },
                () =>
                {
                    if (Pick(S3("L.S.Slot.NeverMoveTitle")) is not { } picked) return;
                    SlotEditing.Exclude(P(), picked);
                    Saved();
                    Show();
                }));
            root.Children.Add(never);
        }

        Control SlotCard(ClientSlot slot)
        {
            var stack = new StackPanel { Spacing = 6 };
            var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
            void UpdateStatus()
            {
                var (text, warn) = Status(slot);
                status.Text = text;
                LinuxLooks.Paint(status, TextBlock.ForegroundProperty, warn ? "Fx.WarningBrush" : "Fx.TextSecondaryBrush");
            }

            // Name, default, monitor, delete.
            var name = new TextBox { Text = slot.Name, MinWidth = 160 };
            AutomationName(name, S3("L.S.Slot.NameBox"));
            name.LostFocus += (_, _) => { if (name.Text != slot.Name) { slot.Name = name.Text ?? ""; Saved(); } };
            var isDefault = new RadioButton { GroupName = group, Content = new TextBlock { Text = S3("L.S.Slot.Default") }, IsChecked = P().DefaultClientSlotId == slot.Id };
            isDefault.IsCheckedChanged += (_, _) => { if (isDefault.IsChecked == true && P().DefaultClientSlotId != slot.Id) { P().DefaultClientSlotId = slot.Id; Saved(); Show(); } };
            var monitor = new ComboBox { MinWidth = 220 };
            var screens = Screens();
            var names = new List<string>();
            for (int i = 0; i < screens.Count; i++)
            {
                monitor.Items.Add(new TextBlock { Text = MonitorLabel(screens[i], i) });
                names.Add(screens[i].DisplayName ?? "");
            }
            int at = Previews.FindSlotMonitor(slot, screens.Select(s => (s.DisplayName ?? "", s.Bounds.Width, s.Bounds.Height)).ToList());
            if (at >= 0 && names[at] == slot.MonitorDeviceName) monitor.SelectedIndex = at;
            else if (!string.IsNullOrEmpty(slot.MonitorDeviceName))
            {
                // A monitor that isn't connected (or a slot made on Windows) stays shown; the slot goes where FindSlotMonitor finds it.
                monitor.Items.Add(new TextBlock { Text = string.Format(S3("L.S.Slot.MonitorMissing"), slot.MonitorDeviceName, slot.MonitorWidth, slot.MonitorHeight) });
                monitor.SelectedIndex = monitor.Items.Count - 1;
            }
            monitor.SelectionChanged += (_, _) =>
            {
                int i = monitor.SelectedIndex;
                if (i < 0 || i >= screens.Count) return;   // a disconnected monitor can stay selected but not be chosen
                slot.MonitorDeviceName = names[i];
                slot.MonitorWidth = screens[i].Bounds.Width;
                slot.MonitorHeight = screens[i].Bounds.Height;
                UpdateStatus();
                Saved();
            };
            var line1 = new WrapPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(Spaced(name));
            line1.Children.Add(Spaced(isDefault));
            line1.Children.Add(Spaced(Field(S3("L.S.Slot.Monitor"), monitor)));
            line1.Children.Add(Action("L.S.Slot.Delete", () =>
            {
                if (!ctx.Host.Confirm(S3("L.S.Slot.DeleteTitle"), string.Format(S3("L.S.Slot.DeleteConfirm"), slot.Name))) return;
                SlotEditing.Delete(P(), slot);
                Saved();
                Show();
            }, destructive: true));
            stack.Children.Add(line1);

            // The visible window rect, copy from a client, fill the screen.
            var line2 = new WrapPanel { Orientation = Orientation.Horizontal };
            line2.Children.Add(Spaced(Field(S3("L.S.Slot.X"), Ui.Number(-20000, 20000, slot.X, v => { slot.X = v; UpdateStatus(); Saved(); }))));
            line2.Children.Add(Spaced(Field(S3("L.S.Slot.Y"), Ui.Number(-20000, 20000, slot.Y, v => { slot.Y = v; UpdateStatus(); Saved(); }))));
            line2.Children.Add(Spaced(Field(S3("L.S.Slot.W"), Ui.Number(0, 20000, slot.Width, v => { slot.Width = v; UpdateStatus(); Saved(); }))));
            line2.Children.Add(Spaced(Field(S3("L.S.Slot.H"), Ui.Number(0, 20000, slot.Height, v => { slot.Height = v; UpdateStatus(); Saved(); }))));
            var capture = Action("L.S.Slot.Capture", () => { });
            capture.Click += (_, _) => CaptureMenu(slot).Open(capture);
            line2.Children.Add(capture);
            line2.Children.Add(Action("L.S.Slot.Fill", () =>
            {
                var all = Screens();
                int i = Previews.FindSlotMonitor(slot, all.Select(s => (s.DisplayName ?? "", s.Bounds.Width, s.Bounds.Height)).ToList());
                var sc = i >= 0 ? all[i] : all.FirstOrDefault(s => s.IsPrimary) ?? all.FirstOrDefault();
                if (sc == null) return;
                SlotEditing.SetRect(slot, sc.DisplayName ?? "", sc.Bounds.Width, sc.Bounds.Height, 0, 0, sc.Bounds.Width, sc.Bounds.Height);
                Saved();
                Show();
            }));
            stack.Children.Add(line2);

            // Characters sent to this slot.
            stack.Children.Add(Names(slot.Characters,
                n => { SlotEditing.RemoveCharacter(slot, n); Saved(); Show(); },
                () =>
                {
                    if (Pick(string.Format(S3("L.S.Slot.AddCharacterTitle"), slot.Name)) is not { } picked) return;
                    SlotEditing.Assign(P(), slot, picked);
                    Saved();
                    Show();
                }));
            stack.Children.Add(status);
            UpdateStatus();
            return stack;
        }

        // "Copy from a client…": the running clients; the picked one's window on the monitor holding its centre, clipped to it.
        ContextMenu CaptureMenu(ClientSlot slot)
        {
            var menu = new ContextMenu();
            var running = CharacterDirectory.Running(ctx);
            if (running.Count == 0) menu.Items.Add(new MenuItem { Header = new TextBlock { Text = S3("L.S.Slot.NoClients") }, IsEnabled = false });
            foreach (var character in running)
            {
                var item = new MenuItem { Header = new TextBlock { Text = character } };
                item.Click += (_, _) => Capture(slot, character);
                menu.Items.Add(item);
            }
            return menu;
        }

        void Capture(ClientSlot slot, string character)
        {
            var all = Screens();
            if (_host.ClientFrame(character) is not { } vis || all.Count == 0) { ctx.Host.Toast(S3("L.S.Slot.CaptureFail")); return; }
            var sc = _window.Screens?.ScreenFromPoint(new PixelPoint(vis.X + vis.Width / 2, vis.Y + vis.Height / 2)) ?? all[0];
            var b = sc.Bounds;
            int x = Math.Max(0, vis.X - b.X), y = Math.Max(0, vis.Y - b.Y);
            int w = Math.Min(vis.Width, b.Width - x), h = Math.Min(vis.Height, b.Height - y);
            if (string.IsNullOrWhiteSpace(slot.Name) || slot.Name.StartsWith("Slot ", StringComparison.Ordinal)) slot.Name = character;
            SlotEditing.SetRect(slot, sc.DisplayName ?? "", b.Width, b.Height, x, y, w, h);
            Saved();
            Show();
        }

        // The fit problem (warning colour), else who goes here. (Windows adds a note for a monitor whose Windows scaling differs
        // from the primary's; X11 positions are the screen's own pixels, so Linux has no such note.)
        (string Text, bool Warn) Status(ClientSlot slot)
        {
            var all = Screens();
            int i = Previews.FindSlotMonitor(slot, all.Select(s => (s.DisplayName ?? "", s.Bounds.Width, s.Bounds.Height)).ToList());
            (int, int)? size = i >= 0 ? (all[i].Bounds.Width, all[i].Bounds.Height) : null;
            if (SlotEditing.Problem(slot, size) is { } problem)
                return (string.Format(S3(problem), size?.Item1 ?? 0, size?.Item2 ?? 0), true);
            var who = new List<string>();
            if (P().DefaultClientSlotId == slot.Id) who.Add(S3("L.S.Slot.WhoDefault"));
            who.AddRange(slot.Characters);
            return (string.Join(", ", who), false);
        }

        Show();
        return root;
    }

    /// <summary>"Characters" with each name as a chip and an Add… button (the Windows SlotsEditor.Chips).</summary>
    private static Control Names(IEnumerable<string> names, Action<string> remove, Action add)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        wrap.Children.Add(new TextBlock { Text = S3("L.S.Slot.Characters"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        foreach (var n in names.ToList())
        {
            var name = n;
            wrap.Children.Add(Chip(name, () => remove(name)));
        }
        wrap.Children.Add(Action("L.S.Slot.AddCharacter", add));
        return wrap;
    }

    /// <summary>"1: 2560×1440 — primary" (the Windows SlotsEditor.MonitorLabel without its Windows scaling note).</summary>
    private static string MonitorLabel(Avalonia.Platform.Screen sc, int index)
    {
        var label = $"{index + 1}: {sc.Bounds.Width}×{sc.Bounds.Height}";
        if (sc.IsPrimary) label += " — " + S3("L.S.Slot.Primary");
        return label;
    }

    /// <summary>A label above its control (EditorCard.Field).</summary>
    private static Control Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Opacity = 0.75, FontSize = 12 });
        panel.Children.Add(input);
        return panel;
    }

    private static Control Spaced(Control control)
    {
        control.Margin = new Thickness(0, 0, 12, 8);
        control.VerticalAlignment = VerticalAlignment.Bottom;
        return control;
    }

    private static void AutomationName(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);
}
