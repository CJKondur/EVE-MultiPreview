using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Label = System.Windows.Controls.Label;
using MessageBox = System.Windows.MessageBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Views;

/// <summary>
/// Settings → Client → "Fixed slots" editor (ClientPositionMode = 3). Rows are
/// built in code from the current profile's ClientSlots. Every edit writes straight
/// into the profile (ThumbnailManager reads it live) and saves.
/// Slot coordinates are the visible window rect, relative to the
/// monitor's full bounds — the same coordinate space Screen.Bounds and SetWindowPos use.
/// </summary>
public partial class SettingsWindow
{
    private const string SlotDefaultGroup = "ClientSlotDefault";

    private static string SlotStr(string key, string en) => LocalizationService.Str(key, en);

    private void LoadClientSlots()
    {
        if (PanelSlotRows == null) return;
        _loadingDepth++;
        try
        {
            PanelSlotRows.Children.Clear();
            var profile = _svc.CurrentProfile;

            // "No default slot" — unlisted characters are then left alone.
            var none = new RadioButton
            {
                GroupName = SlotDefaultGroup,
                Content = SlotStr("L.Client.SlotNoDefault", "No default slot (only listed characters are moved)"),
                IsChecked = string.IsNullOrEmpty(profile.DefaultClientSlotId)
                            || profile.ClientSlots.All(s => s.Id != profile.DefaultClientSlotId),
                Margin = new Thickness(0, 0, 0, 6),
            };
            none.Checked += (_, _) =>
            {
                if (_loading) return;
                _svc.CurrentProfile.DefaultClientSlotId = "";
                SaveDelayed();
            };
            PanelSlotRows.Children.Add(none);

            foreach (var slot in profile.ClientSlots)
                PanelSlotRows.Children.Add(BuildSlotRow(slot));

            PanelSlotRows.Children.Add(BuildExcludedRow());
        }
        finally { _loadingDepth--; }
    }

    private UIElement BuildSlotRow(ClientSlot slot)
    {
        var secondary = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var root = new StackPanel();
        var border = new Border
        {
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = root,
        };

        // ── Line 1: name, default, monitor, delete ──
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        var txtName = new TextBox { Text = slot.Name, Width = 130, VerticalContentAlignment = VerticalAlignment.Center };
        txtName.TextChanged += (_, _) => { if (_loading) return; slot.Name = txtName.Text; SaveDelayed(); };
        line1.Children.Add(txtName);

        var rbDefault = new RadioButton
        {
            GroupName = SlotDefaultGroup,
            Content = SlotStr("L.Client.SlotDefault", "Default"),
            IsChecked = _svc.CurrentProfile.DefaultClientSlotId == slot.Id,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = SlotStr("L.Client.SlotDefaultTip", "Clients whose character isn't listed in any slot go here (including clients at character select)."),
        };
        rbDefault.Checked += (_, _) =>
        {
            if (_loading) return;
            _svc.CurrentProfile.DefaultClientSlotId = slot.Id;
            SaveDelayed();
        };
        line1.Children.Add(rbDefault);

        var cmbMonitor = new ComboBox { Width = 250, Margin = new Thickness(10, 0, 0, 0) };
        var screens = WinForms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            cmbMonitor.Items.Add(new ComboBoxItem { Content = MonitorLabel(sc, i), Tag = sc.DeviceName });
            if (sc.DeviceName == slot.MonitorDeviceName) cmbMonitor.SelectedIndex = i;
        }
        if (cmbMonitor.SelectedIndex < 0 && !string.IsNullOrEmpty(slot.MonitorDeviceName))
        {
            cmbMonitor.Items.Add(new ComboBoxItem
            {
                Content = $"{SlotStr("L.Client.SlotMonitorMissing", "(not connected)")} {slot.MonitorDeviceName} {slot.MonitorWidth}×{slot.MonitorHeight}",
                Tag = slot.MonitorDeviceName,
            });
            cmbMonitor.SelectedIndex = cmbMonitor.Items.Count - 1;
        }
        line1.Children.Add(cmbMonitor);

        var btnDelete = SlotButton(SlotStr("L.Client.SlotDelete", "Delete"));
        btnDelete.Margin = new Thickness(10, 0, 0, 0);
        line1.Children.Add(btnDelete);
        root.Children.Add(line1);

        // ── Line 2: window rect + capture / fill ──
        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        TextBox Num(string label, int value)
        {
            line2.Children.Add(new Label { Content = label, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 2, 0) });
            var tb = new TextBox { Text = value.ToString(), Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
            line2.Children.Add(tb);
            return tb;
        }
        var txtX = Num("X", slot.X);
        var txtY = Num("Y", slot.Y);
        var txtW = Num("W", slot.Width);
        var txtH = Num("H", slot.Height);

        var btnCapture = SlotButton(SlotStr("L.Client.SlotCapture", "Capture from client…"));
        btnCapture.Margin = new Thickness(10, 0, 0, 0);
        btnCapture.ToolTip = SlotStr("L.Client.SlotCaptureTip", "Use a running client's current window (position, size and monitor) for this slot.");
        line2.Children.Add(btnCapture);
        var btnFill = SlotButton(SlotStr("L.Client.SlotFill", "Fill screen"));
        btnFill.Margin = new Thickness(4, 0, 0, 0);
        line2.Children.Add(btnFill);
        root.Children.Add(line2);

        // ── Line 3: "these specific characters" ──
        root.Children.Add(BuildCharacterChips(
            SlotStr("L.Client.SlotCharacters", "Characters:"),
            slot.Characters,
            add: name => AssignCharacterToSlot(name, slot),
            remove: name => { slot.Characters.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)); LoadClientSlots(); SaveDelayed(); },
            addTitle: string.Format(SlotStr("L.Client.SlotAddCharTitle", "Send a character to '{0}'"), slot.Name)));

        // ── Line 4: status (fit / monitor problems) ──
        var status = new TextBlock { FontSize = 10, Margin = new Thickness(4, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = secondary };
        root.Children.Add(status);

        void UpdateStatus()
        {
            var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == slot.MonitorDeviceName);
            string? problem = null;
            if (sc == null)
                problem = SlotStr("L.Client.SlotNoMonitor", "Monitor not connected — this slot is skipped.");
            else if (slot.Width < 640 || slot.Height < 360)
                problem = SlotStr("L.Client.SlotTooSmall", "Too small — minimum 640×360. This slot is skipped.");
            else if (slot.X < 0 || slot.Y < 0 || slot.X + slot.Width > sc.Bounds.Width || slot.Y + slot.Height > sc.Bounds.Height)
                problem = string.Format(SlotStr("L.Client.SlotNoFit", "Doesn't fit the {0}×{1} monitor — this slot is skipped."),
                                        sc.Bounds.Width, sc.Bounds.Height);

            int count = slot.Characters.Count;
            string who = _svc.CurrentProfile.DefaultClientSlotId == slot.Id
                ? SlotStr("L.Client.SlotWhoDefault", "Default slot")
                : "";
            if (count > 0)
                who += (who.Length > 0 ? " + " : "") + string.Join(", ", slot.Characters);

            string? warning = null;
            if (problem == null && sc != null && ScalingDiffersFromPrimary(sc))
            {
                var (pw, ph) = PhysicalResolution(sc);
                warning = string.Format(SlotStr("L.Client.SlotScaledWarn",
                    "This monitor's Windows scaling differs from your primary's. EVE doesn't support that, so its image is rescaled here. " +
                    "Position and size are still exact; values are in the app's {0}×{1} units for this {2}×{3} monitor. " +
                    "For a pixel-exact client, give this monitor the same scaling as your primary."),
                    sc.Bounds.Width, sc.Bounds.Height, pw, ph);
            }

            if (problem != null) status.Text = problem;
            else if (warning != null) status.Text = (who.Length > 0 ? who + Environment.NewLine : "") + "⚠ " + warning;
            else status.Text = who;
            status.Foreground = problem != null || warning != null ? new SolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x0D)) : secondary;
        }

        // ── Wiring ──
        void RectChanged()
        {
            if (_loading) return;
            if (int.TryParse(txtX.Text, out var x)) slot.X = x;
            if (int.TryParse(txtY.Text, out var y)) slot.Y = y;
            if (int.TryParse(txtW.Text, out var w)) slot.Width = w;
            if (int.TryParse(txtH.Text, out var h)) slot.Height = h;
            UpdateStatus();
            SaveDelayed();
        }
        txtX.TextChanged += (_, _) => RectChanged();
        txtY.TextChanged += (_, _) => RectChanged();
        txtW.TextChanged += (_, _) => RectChanged();
        txtH.TextChanged += (_, _) => RectChanged();

        cmbMonitor.SelectionChanged += (_, _) =>
        {
            if (_loading || cmbMonitor.SelectedItem is not ComboBoxItem item) return;
            var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == (string)item.Tag);
            if (sc == null) return;
            slot.MonitorDeviceName = sc.DeviceName;
            slot.MonitorWidth = sc.Bounds.Width;
            slot.MonitorHeight = sc.Bounds.Height;
            UpdateStatus();
            SaveDelayed();
        };

        btnFill.Click += (_, _) =>
        {
            var sc = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == slot.MonitorDeviceName)
                     ?? WinForms.Screen.PrimaryScreen;
            if (sc == null) return;
            SetSlotRect(slot, sc, 0, 0, sc.Bounds.Width, sc.Bounds.Height);
        };

        btnCapture.Click += (_, _) => ShowCaptureMenu(btnCapture, slot);

        btnDelete.Click += (_, _) =>
        {
            var profile = _svc.CurrentProfile;
            profile.ClientSlots.Remove(slot);
            if (profile.DefaultClientSlotId == slot.Id) profile.DefaultClientSlotId = "";
            LoadClientSlots();
            SaveDelayed();
        };

        UpdateStatus();
        return border;
    }

    /// <summary>A labelled row of removable character "chips" plus an Add… button.</summary>
    private UIElement BuildCharacterChips(string label, System.Collections.Generic.List<string> names,
        Action<string> add, Action<string> remove, string addTitle)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(new Label { Content = label, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 6, 0) });
        foreach (var name in names.ToList())
        {
            var chip = SlotButton(name + "  ✕");
            chip.Margin = new Thickness(0, 0, 4, 4);
            chip.ToolTip = SlotStr("L.Client.SlotRemoveChar", "Remove");
            var captured = name;
            chip.Click += (_, _) => remove(captured);
            wrap.Children.Add(chip);
        }
        var btnAdd = SlotButton(SlotStr("L.Client.SlotAddChar", "+ Add…"));
        btnAdd.Margin = new Thickness(0, 0, 4, 4);
        btnAdd.Click += (_, _) =>
        {
            var picked = ShowCharacterSearch(addTitle);
            if (!string.IsNullOrWhiteSpace(picked)) add(picked.Trim());
        };
        wrap.Children.Add(btnAdd);
        return wrap;
    }

    /// <summary>A character belongs to at most one place: its slot, or the excluded
    /// list. Assigning it here removes it everywhere else.</summary>
    private void AssignCharacterToSlot(string name, ClientSlot target)
    {
        var profile = _svc.CurrentProfile;
        foreach (var sl in profile.ClientSlots)
            sl.Characters.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        profile.ClientSlotExcluded.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        target.Characters.Add(name);
        LoadClientSlots();
        SaveDelayed();
    }

    private UIElement BuildExcludedRow()
    {
        var profile = _svc.CurrentProfile;
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        box.Children.Add(BuildCharacterChips(
            SlotStr("L.Client.SlotExcluded", "Never move:"),
            profile.ClientSlotExcluded,
            add: name =>
            {
                foreach (var sl in profile.ClientSlots)
                    sl.Characters.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                if (!profile.ClientSlotExcluded.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                    profile.ClientSlotExcluded.Add(name);
                LoadClientSlots();
                SaveDelayed();
            },
            remove: name =>
            {
                profile.ClientSlotExcluded.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                LoadClientSlots();
                SaveDelayed();
            },
            addTitle: SlotStr("L.Client.SlotExcludeTitle", "Never move this character")));
        box.Children.Add(new TextBlock
        {
            Text = SlotStr("L.Client.SlotExcludedNote", "These characters' windows are never moved or resized, even when there's a Default slot."),
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
        });
        return box;
    }

    private static Button SlotButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(8, 2, 8, 2),
        Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
        Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
    };

    private static string MonitorLabel(WinForms.Screen sc, int index)
    {
        var (pw, ph) = PhysicalResolution(sc);
        string primary = sc.Primary ? $" — {SlotStr("L.Client.SlotPrimary", "primary")}" : "";
        string scaled = ScalingDiffersFromPrimary(sc) ? $" — {SlotStr("L.Client.SlotScaledShort", "different scaling")}" : "";
        return $"{index + 1}: {pw}×{ph}{primary}{scaled}";
    }

    // ── Mixed-DPI detection ─────────────────────────────────────────
    // EVE is system-DPI-aware (spike, 2026-09-24): on a monitor whose Windows scaling
    // differs from the primary's, Windows rescales its image, and EVE believes its
    // window is larger than it is. This app is system-aware too, so per-monitor DPI
    // queries all return the primary's DPI. The reliable signal is the monitor's real
    // display-mode resolution (EnumDisplaySettings is never virtualized) versus the
    // bounds this process sees: they differ exactly when the scaling differs.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>The monitor's real resolution; falls back to the bounds this process sees.</summary>
    private static (int W, int H) PhysicalResolution(WinForms.Screen sc)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(sc.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            return (dm.dmPelsWidth, dm.dmPelsHeight);
        return (sc.Bounds.Width, sc.Bounds.Height);
    }

    /// <summary>True when this monitor's Windows scaling differs from the primary's.</summary>
    private static bool ScalingDiffersFromPrimary(WinForms.Screen sc)
    {
        if (sc.Primary) return false;
        var (pw, _) = PhysicalResolution(sc);
        if (pw != sc.Bounds.Width) return true;   // bounds virtualized: system-aware process
        // Per-monitor-aware process: bounds are physical, so compare effective DPIs.
        var prim = WinForms.Screen.PrimaryScreen;
        if (prim == null) return false;
        double a = Interop.DpiHelper.GetScaleFactorForPoint(sc.Bounds.Left + sc.Bounds.Width / 2, sc.Bounds.Top + sc.Bounds.Height / 2);
        double b = Interop.DpiHelper.GetScaleFactorForPoint(prim.Bounds.Left + prim.Bounds.Width / 2, prim.Bounds.Top + prim.Bounds.Height / 2);
        return Math.Abs(a - b) > 0.01;
    }

    /// <summary>Store a rect on a slot (relative to <paramref name="sc"/>) and refresh the editor.</summary>
    private void SetSlotRect(ClientSlot slot, WinForms.Screen sc, int x, int y, int w, int h)
    {
        slot.MonitorDeviceName = sc.DeviceName;
        slot.MonitorWidth = sc.Bounds.Width;
        slot.MonitorHeight = sc.Bounds.Height;
        slot.X = x; slot.Y = y; slot.Width = w; slot.Height = h;
        LoadClientSlots();
        SaveDelayed();
    }

    /// <summary>List running logged-in clients; picking one copies its current visible
    /// window rect and monitor into the slot.</summary>
    private void ShowCaptureMenu(Button anchor, ClientSlot slot)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var names = _thumbnailManager?.GetActiveCharacterNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        if (names.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = SlotStr("L.Client.SlotNoClients", "No logged-in EVE clients running"), IsEnabled = false });
        }
        foreach (var name in names)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => CaptureSlotFromClient(slot, name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void CaptureSlotFromClient(ClientSlot slot, string character)
    {
        var hwnd = _thumbnailManager?.GetHwndForCharacter(character) ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || Interop.User32.IsIconic(hwnd) || Interop.User32.IsZoomed(hwnd)
            || !Interop.DwmApi.TryGetVisibleFrame(hwnd, out var vis, out _, out _))
        {
            MessageBox.Show(SlotStr("L.Client.SlotCaptureFail", "That client isn't in a normal window (it may be minimized or maximized). Restore it and try again."),
                SlotStr("L.Client.SpawnSlots", "Fixed slots"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // The slot is the VISIBLE window (title bar and borders included for a windowed
        // client, without Windows' invisible resize borders), so a window placed flush in
        // a corner captures as exactly 0,0.
        int w = vis.Right - vis.Left, h = vis.Bottom - vis.Top;

        // The monitor holding the centre of the window.
        var sc = WinForms.Screen.FromPoint(new System.Drawing.Point(vis.Left + w / 2, vis.Top + h / 2));

        // Clip to that monitor so the captured slot always passes the fit check.
        int x = Math.Max(0, vis.Left - sc.Bounds.Left), y = Math.Max(0, vis.Top - sc.Bounds.Top);
        w = Math.Min(w, sc.Bounds.Width - x);
        h = Math.Min(h, sc.Bounds.Height - y);

        if (string.IsNullOrWhiteSpace(slot.Name) || slot.Name.StartsWith("Slot ", StringComparison.Ordinal))
            slot.Name = character;
        SetSlotRect(slot, sc, x, y, w, h);
    }

    private void OnSlotAdd(object sender, RoutedEventArgs e)
    {
        var profile = _svc.CurrentProfile;
        var sc = WinForms.Screen.PrimaryScreen!;
        var slot = new ClientSlot
        {
            Name = $"Slot {profile.ClientSlots.Count + 1}",
            MonitorDeviceName = sc.DeviceName,
            MonitorWidth = sc.Bounds.Width,
            MonitorHeight = sc.Bounds.Height,
            X = 0, Y = 0, Width = sc.Bounds.Width, Height = sc.Bounds.Height,
        };
        profile.ClientSlots.Add(slot);
        // First slot becomes the default, so "all mains on one monitor" works right away.
        if (string.IsNullOrEmpty(profile.DefaultClientSlotId))
            profile.DefaultClientSlotId = slot.Id;
        LoadClientSlots();
        SaveDelayed();
    }

    private void OnSlotApplyNow(object sender, RoutedEventArgs e) => _thumbnailManager?.ApplyAllClientSlots();
}
