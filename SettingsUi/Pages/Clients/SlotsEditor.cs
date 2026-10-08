using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Windows &amp; positions › Fixed slots, ported from SettingsWindow.Slots.cs. Every
/// edit writes the current profile and only saves: ThumbnailManager reads slots on each switch.
/// Rect edits update the slot's status line in place; structural edits rebuild the cards.</summary>
public sealed class SlotsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly StackPanel _root = new();
    // WPF groups RadioButtons by name across a window: one group per editor.
    private readonly string _radioGroup = "Slots" + Guid.NewGuid().ToString("N");

    public SlotsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = _root;
        Refresh();
    }

    /// <summary>Slot cards shown.</summary>
    public int SlotCount { get; private set; }

    private Profile P => CharacterDirectory.Current(_ctx.S);
    private static string Str(string key) => SettingsContext.Str(key);
    private void Saved() => _ctx.Applier.Request(ApplyScope.Save);

    public void Refresh()
    {
        _root.Children.Clear();
        var none = new RadioButton
        {
            GroupName = _radioGroup,
            IsChecked = string.IsNullOrEmpty(P.DefaultClientSlotId) || P.ClientSlots.All(s => s.Id != P.DefaultClientSlotId),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Loc.SetKey(none, "L.S.Slot.NoDefault");
        none.Checked += (_, _) => { P.DefaultClientSlotId = ""; Saved(); Refresh(); };
        _root.Children.Add(EditorCard.Build("clients.windows.slots", EditorCard.Row(
            EditorCard.Button("L.S.Act.clients.windows.addSlot", AddSlot),
            EditorCard.Button("L.S.Act.clients.windows.snapNow", () => _ctx.Services.Thumbnails?.ApplyAllClientSlots())), none));
        var slots = P.ClientSlots.ToList();
        SlotCount = slots.Count;
        foreach (var slot in slots) _root.Children.Add(SlotCard(slot));
        _root.Children.Add(EditorCard.Build("clients.windows.neverMove", null, Chips(P.ClientSlotExcluded,
            remove: n => { SlotEditing.Include(P, n); Saved(); Refresh(); },
            add: () =>
            {
                var picked = Pick(Str("L.S.Slot.NeverMoveTitle"));
                if (picked == null) return;
                SlotEditing.Exclude(P, picked);
                Saved();
                Refresh();
            })));
    }

    private UIElement SlotCard(ClientSlot slot)
    {
        var stack = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        void UpdateStatus()
        {
            var (text, warn) = Status(slot);
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Fx.WarningBrush" : "Fx.TextSecondaryBrush");
        }

        // Name, default, monitor, delete.
        var name = new TextBox { Text = slot.Name, MinWidth = 160, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        Loc.SetNameKey(name, "L.S.Slot.NameBox");
        name.LostFocus += (_, _) => { if (name.Text != slot.Name) { slot.Name = name.Text; Saved(); } };
        var isDefault = new RadioButton
        {
            GroupName = _radioGroup, IsChecked = P.DefaultClientSlotId == slot.Id,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8),
        };
        Loc.SetKey(isDefault, "L.S.Slot.Default");
        isDefault.Checked += (_, _) => { P.DefaultClientSlotId = slot.Id; Saved(); Refresh(); };
        var monitor = new ComboBox { MinWidth = 240 };
        var screens = WinForms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            monitor.Items.Add(new ComboBoxItem { Content = MonitorLabel(screens[i], i), Tag = screens[i].DeviceName });
            if (screens[i].DeviceName == slot.MonitorDeviceName) monitor.SelectedIndex = i;
        }
        if (monitor.SelectedIndex < 0 && !string.IsNullOrEmpty(slot.MonitorDeviceName))
        {
            monitor.Items.Add(new ComboBoxItem
            {
                Content = string.Format(Str("L.S.Slot.MonitorMissing"), slot.MonitorDeviceName, slot.MonitorWidth, slot.MonitorHeight),
                Tag = slot.MonitorDeviceName,
            });
            monitor.SelectedIndex = monitor.Items.Count - 1;
        }
        monitor.SelectionChanged += (_, _) =>
        {
            if (monitor.SelectedItem is not ComboBoxItem { Tag: string device }) return;
            var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
            if (sc == null) return;   // a disconnected monitor can stay selected but not be chosen
            slot.MonitorDeviceName = sc.DeviceName;
            slot.MonitorWidth = sc.Bounds.Width;
            slot.MonitorHeight = sc.Bounds.Height;
            UpdateStatus();
            Saved();
        };
        var line1 = new WrapPanel();
        line1.Children.Add(name);
        line1.Children.Add(isDefault);
        line1.Children.Add(EditorCard.Field("L.S.Slot.Monitor", monitor));
        line1.Children.Add(EditorCard.Button("L.S.Slot.Delete", () => DeleteSlot(slot), destructive: true));
        stack.Children.Add(line1);

        // The visible window rect, copy from a client, fill the screen.
        var line2 = new WrapPanel();
        line2.Children.Add(EditorCard.Field("L.S.Slot.X", NumberBox(slot.X, v => { slot.X = v; UpdateStatus(); Saved(); })));
        line2.Children.Add(EditorCard.Field("L.S.Slot.Y", NumberBox(slot.Y, v => { slot.Y = v; UpdateStatus(); Saved(); })));
        line2.Children.Add(EditorCard.Field("L.S.Slot.W", NumberBox(slot.Width, v => { slot.Width = v; UpdateStatus(); Saved(); })));
        line2.Children.Add(EditorCard.Field("L.S.Slot.H", NumberBox(slot.Height, v => { slot.Height = v; UpdateStatus(); Saved(); })));
        Button? capture = null;
        capture = EditorCard.Button("L.S.Slot.Capture", () => ShowCaptureMenu(capture!, slot));
        capture.Margin = new Thickness(0, 0, 8, 8);
        line2.Children.Add(capture);
        line2.Children.Add(EditorCard.Button("L.S.Slot.Fill", () => Fill(slot)));
        stack.Children.Add(line2);

        // Characters sent to this slot.
        stack.Children.Add(Chips(slot.Characters,
            remove: n => { SlotEditing.RemoveCharacter(slot, n); Saved(); Refresh(); },
            add: () =>
            {
                var picked = Pick(string.Format(Str("L.S.Slot.AddCharacterTitle"), slot.Name));
                if (picked == null) return;
                SlotEditing.Assign(P, slot, picked);
                Saved();
                Refresh();
            }));
        stack.Children.Add(status);
        UpdateStatus();
        return EditorCard.Frame(stack);
    }

    /// <summary>A whole-number box: stores on Enter or focus loss; bad input puts the old value back.</summary>
    private static TextBox NumberBox(int value, Action<int> store)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = 64, VerticalContentAlignment = VerticalAlignment.Center };
        void Commit()
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v))
            {
                box.Text = value.ToString(CultureInfo.CurrentCulture);
                return;
            }
            if (v == value) return;
            value = v;
            store(v);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return box;
    }

    private UIElement Chips(IEnumerable<string> names, Action<string> remove, Action add)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var label = EditorCard.Note("L.S.Slot.Characters");
        label.Margin = new Thickness(0, 0, 8, 6);
        wrap.Children.Add(label);
        foreach (var n in names.ToList())
        {
            var name = n;
            wrap.Children.Add(EditorCard.Chip(name, () => remove(name)));
        }
        var addButton = EditorCard.Button("L.S.Slot.AddCharacter", add);
        addButton.Margin = new Thickness(0, 0, 0, 6);
        wrap.Children.Add(addButton);
        return wrap;
    }

    private string? Pick(string title) =>
        _ctx.Host.PickCharacter(title, CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));

    private void AddSlot()
    {
        var sc = WinForms.Screen.PrimaryScreen;
        if (sc == null) return;
        SlotEditing.Add(P, sc.DeviceName, sc.Bounds.Width, sc.Bounds.Height);
        Saved();
        Refresh();
    }

    private void DeleteSlot(ClientSlot slot)
    {
        if (!_ctx.Host.Confirm(Str("L.S.Slot.DeleteTitle"), string.Format(Str("L.S.Slot.DeleteConfirm"), slot.Name))) return;
        SlotEditing.Delete(P, slot);
        Saved();
        Refresh();
    }

    private void Fill(ClientSlot slot)
    {
        var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == slot.MonitorDeviceName) ?? WinForms.Screen.PrimaryScreen;
        if (sc == null) return;
        SlotEditing.SetRect(slot, sc.DeviceName, sc.Bounds.Width, sc.Bounds.Height, 0, 0, sc.Bounds.Width, sc.Bounds.Height);
        Saved();
        Refresh();
    }

    private void ShowCaptureMenu(Button anchor, ClientSlot slot)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var names = CharacterDirectory.Running(_ctx);
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = Str("L.S.Slot.NoClients"), IsEnabled = false });
        foreach (var n in names)
        {
            var name = n;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => CaptureFromClient(slot, name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Old CaptureSlotFromClient: the client's visible window on the monitor holding its
    /// centre, clipped to that monitor so the slot always fits.</summary>
    private void CaptureFromClient(ClientSlot slot, string character)
    {
        var hwnd = _ctx.Services.Thumbnails?.GetHwndForCharacter(character) ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || User32.IsIconic(hwnd) || User32.IsZoomed(hwnd)
            || !DwmApi.TryGetVisibleFrame(hwnd, out var vis, out _, out _))
        {
            _ctx.Host.Toast(Str("L.S.Slot.CaptureFail"));
            return;
        }
        int w = vis.Right - vis.Left, h = vis.Bottom - vis.Top;
        var sc = WinForms.Screen.FromPoint(new System.Drawing.Point(vis.Left + w / 2, vis.Top + h / 2));
        int x = Math.Max(0, vis.Left - sc.Bounds.Left), y = Math.Max(0, vis.Top - sc.Bounds.Top);
        w = Math.Min(w, sc.Bounds.Width - x);
        h = Math.Min(h, sc.Bounds.Height - y);
        if (string.IsNullOrWhiteSpace(slot.Name) || slot.Name.StartsWith("Slot ", StringComparison.Ordinal)) slot.Name = character;
        SlotEditing.SetRect(slot, sc.DeviceName, sc.Bounds.Width, sc.Bounds.Height, x, y, w, h);
        Saved();
        Refresh();
    }

    /// <summary>The fit problem (warning colour), else who goes here plus a mixed-scaling note.</summary>
    private (string Text, bool Warn) Status(ClientSlot slot)
    {
        var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == slot.MonitorDeviceName);
        var problem = SlotEditing.Problem(slot, sc == null ? null : (sc.Bounds.Width, sc.Bounds.Height));
        if (problem != null) return (string.Format(Str(problem), sc?.Bounds.Width ?? 0, sc?.Bounds.Height ?? 0), true);
        var who = new List<string>();
        if (P.DefaultClientSlotId == slot.Id) who.Add(Str("L.S.Slot.WhoDefault"));
        who.AddRange(slot.Characters);
        var text = string.Join(", ", who);
        if (sc != null && MonitorInfo.ScalingDiffersFromPrimary(sc))
        {
            var (pw, ph) = MonitorInfo.PhysicalResolution(sc);
            text = (text.Length > 0 ? text + Environment.NewLine : "")
                   + string.Format(Str("L.S.Slot.Scaled"), sc.Bounds.Width, sc.Bounds.Height, pw, ph);
            return (text, true);
        }
        return (text, false);
    }

    private static string MonitorLabel(WinForms.Screen sc, int index)
    {
        var (pw, ph) = MonitorInfo.PhysicalResolution(sc);
        var label = $"{index + 1}: {pw}×{ph}";
        if (sc.Primary) label += " — " + Str("L.S.Slot.Primary");
        if (MonitorInfo.ScalingDiffersFromPrimary(sc)) label += " — " + Str("L.S.Slot.ScaledShort");
        return label;
    }
}
