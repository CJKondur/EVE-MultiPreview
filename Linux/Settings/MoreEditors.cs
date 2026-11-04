using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S3b: Avalonia ports of the Windows editors whose settings the Linux app already reads (Home › Quick actions,
/// Clients › Audio's volumes, Thumbnails' resize-all and second thumbnails, a character's own size, Alerts' severity table,
/// colour-blind mode, sound table and do-not-disturb line). Each does what its Windows editor does, through the Linux app
/// (ISettingsHost) where the Windows one asks ThumbnailManager.</summary>
internal sealed partial class SettingsPages
{
    /// <summary>A key in its Linux wording (LinuxSupport.Key).</summary>
    private static string L(string key) => SettingsContext.Str(LinuxSupport.Key(key));

    private void Saved(SettingsContext ctx) => ctx.Applier.Request(ApplyScope.Save);

    /// <summary>Runs <paramref name="tick"/> every <paramref name="every"/> while <paramref name="control"/> is on the screen
    /// (the Windows LiveRefresh).</summary>
    private static void WhileShown(Control control, TimeSpan every, Action tick)
    {
        var timer = new DispatcherTimer { Interval = every };
        timer.Tick += (_, _) => tick();
        control.AttachedToVisualTree += (_, _) => timer.Start();
        control.DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    private static TextBlock Error() => new() { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private static void Say(TextBlock line, string? text)
    {
        line.Text = text ?? "";
        line.IsVisible = text != null;
    }

    private static Grid Table(string columns) => new() { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(0, 4) };

    private static int Row(Grid table)
    {
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        return table.RowDefinitions.Count - 1;
    }

    private static void Put(Grid table, Control c, int row, int column, int span = 1)
    {
        c.Margin = new Thickness(0, 3, 12, 3);
        c.VerticalAlignment = VerticalAlignment.Center;
        if (c.HorizontalAlignment == HorizontalAlignment.Stretch) c.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        Grid.SetColumnSpan(c, span);
        table.Children.Add(c);
    }

    private static TextBlock Head(string key) => new() { Text = L(key), FontWeight = FontWeight.SemiBold, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 160 };

    private static TextBlock Note(string key) => new() { Text = L(key), Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    /// <summary>A slider with its value and unit beside it; the value is set before <paramref name="changed"/> is attached.</summary>
    private static StackPanel SliderRow(double min, double max, double step, double value, Action<double> changed, string unit, double width = 200)
    {
        var shown = new TextBlock { MinWidth = 52, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Width = width, SmallChange = step, TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(value, min, max) };
        void Show(double v) => shown.Text = ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture) + (unit == "%" ? unit : " " + unit);
        Show(slider.Value);
        Ui.Watch(slider, Avalonia.Controls.Primitives.RangeBase.ValueProperty, v => { Show(v); changed(v); });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(shown);
        return row;
    }

    // ── Home › Quick actions ──

    /// <summary>Home › Quick actions (the Windows QuickActionsEditor): hide all, lock positions, pause hotkeys, click-through,
    /// each with the key that does the same. Click-through is greyed out: the Linux thumbnails have none (as the Windows switch
    /// is without its service).</summary>
    internal Control QuickActions(SettingsContext ctx)
    {
        var rows = new StackPanel { Spacing = 2 };
        void Add(string id, string key, bool? state, Action<bool> set)
        {
            var text = HotkeyText.ToDisplay(key);
            var keyText = new TextBlock { Text = text.Length > 0 ? text : L("L.S.Hotkey.None"), Opacity = 0.65, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center };
            var box = new CheckBox { IsChecked = state ?? false, IsEnabled = state != null };
            box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); _host.TrayChanged(); };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            right.Children.Add(keyText);
            right.Children.Add(box);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            grid.Children.Add(Titled(id));
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            rows.Children.Add(grid);
        }
        Add("home.quick.hideAll", ctx.S.HideShowThumbnailsHotkey, _host.ThumbnailsHidden, on => _host.ThumbnailsHidden = on);
        Add("home.quick.lock", ctx.S.LockPositionsHotkey, _host.PositionsLocked, on => _host.PositionsLocked = on);
        Add("home.quick.pause", ctx.S.SuspendHotkey, _host.HotkeysPaused, on => _host.HotkeysPaused = on);
        Add("home.quick.clickThrough", ctx.S.ClickThroughHotkey, null, _ => { });
        return rows;
    }

    // ── Clients › Audio › Running clients ──

    /// <summary>One volume slider per running client (the Windows VolumeEditor); drawn again when who runs changes.</summary>
    internal Control Volumes(SettingsContext ctx)
    {
        var panel = Titled("clients.audio.volume");
        var rows = new StackPanel();
        panel.Children.Add(rows);
        string shown = "";
        string Signature() => string.Join("\n", CharacterDirectory.Running(ctx).Select(n => n + "|" + CharacterFields.Volume(ctx.S, n)));
        void Draw()
        {
            rows.Children.Clear();
            var names = CharacterDirectory.Running(ctx);
            shown = Signature();
            if (names.Count == 0) { rows.Children.Add(Note("L.S.clients.audio.volume.Empty")); return; }
            var table = Table("*,Auto");
            foreach (var name in names)
            {
                int r = Row(table);
                Put(table, new TextBlock { Text = name }, r, 0);
                Put(table, SliderRow(0, 100, 1, CharacterFields.Volume(ctx.S, name), v =>
                {
                    _host.SetClientVolume(name, (int)Math.Round(v));   // stores, saves, sets the running client
                    shown = Signature();                               // our own write must not redraw mid-drag
                }, L("L.S.Unit.Percent"), 220), r, 1);
            }
            rows.Children.Add(table);
        }
        Draw();
        WhileShown(panel, TimeSpan.FromSeconds(2), () => { if (Signature() != shown) Draw(); });
        return panel;
    }

    // ── Sizes ──

    /// <summary>Width × height boxes, an Apply button and an error line under them.</summary>
    private static (Control Row, TextBox W, TextBox H, Button Apply, TextBlock Error) SizeRow((int W, int H) size, string applyKey)
    {
        var w = new TextBox { Width = 72, Text = size.W.ToString(CultureInfo.CurrentCulture) };
        var h = new TextBox { Width = 72, Text = size.H.ToString(CultureInfo.CurrentCulture) };
        ToolTip.SetTip(w, L("L.S.Slot.W"));
        ToolTip.SetTip(h, L("L.S.Slot.H"));
        var apply = new Button { Content = new TextBlock { Text = Ui.NoAccess(L(applyKey)) } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(w);
        row.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(h);
        row.Children.Add(new TextBlock { Text = L("L.S.Unit.Px"), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(apply);
        return (row, w, h, apply, Error());
    }

    /// <summary>The error key of a typed size (not a whole number, or outside 50–4000), or null with the size.</summary>
    private static string? CheckSize(string? wText, string? hText, out int w, out int h)
    {
        h = 0;
        if (!ThumbnailFields.TryWhole(wText, out w) || !ThumbnailFields.TryWhole(hText, out h)) return Setting.ErrNotANumber;
        return w < CharacterSize.Min || w > CharacterSize.Max || h < CharacterSize.Min || h > CharacterSize.Max ? Setting.ErrRange : null;
    }

    private static string SizeError(string key) => string.Format(CultureInfo.CurrentCulture, L(key), CharacterSize.Min, CharacterSize.Max);

    /// <summary>Thumbnails › Appearance › Resize every thumbnail (the Windows ResizeAllEditor): every running thumbnail gets the
    /// size where it is, and new ones start at it.</summary>
    internal Control ResizeAll(SettingsContext ctx)
    {
        var panel = Titled("thumbs.appearance.resizeAll");
        var start = ctx.S.ThumbnailStartLocation;
        var (row, w, h, apply, error) = SizeRow(((int)start.Width, (int)start.Height), "L.S.Act.thumbs.appearance.applySize");
        apply.Click += (_, _) =>
        {
            if (CheckSize(w.Text, h.Text, out var width, out var height) is { } bad) { Say(error, SizeError(bad)); return; }
            Say(error, null);
            _host.ResizeThumbnails(width, height);   // stores the starting size and saves
        };
        panel.Children.Add(row);
        panel.Children.Add(error);
        return panel;
    }

    /// <summary>A character's page › Own size (the Windows CharacterSizeEditor): the running thumbnail is resized where it is;
    /// one that is not running keeps its saved place with the new size; one never placed can't be given a size yet.</summary>
    internal Control CharacterSizeBox(SettingsContext ctx, string name)
    {
        var panel = Titled("character.size");
        var (row, w, h, apply, error) = SizeRow(CharacterSize.Current(ctx, name), "L.S.Act.character.applySize");
        bool running = _host.RunningCharacters().Contains(name, StringComparer.OrdinalIgnoreCase);
        bool can = running || CharacterSize.CanApply(ctx, name);
        w.IsEnabled = h.IsEnabled = apply.IsEnabled = can;
        if (!can) Say(error, L("L.S.character.size.NeedsLogin"));
        apply.Click += (_, _) =>
        {
            if (CheckSize(w.Text, h.Text, out var width, out var height) is { } bad) { Say(error, SizeError(bad)); return; }
            if (_host.ResizeThumbnail(name, width, height)) { Say(error, null); return; }
            // Not running: the saved place gets the size (CharacterSize.Apply without a ThumbnailManager).
            var result = CharacterSize.Apply(ctx, name, w.Text ?? "", h.Text ?? "");
            Say(error, result == null ? null : SizeError(result));
        };
        panel.Children.Add(row);
        panel.Children.Add(error);
        return panel;
    }

    /// <summary>A character's page › Include in cycling (the Windows CharacterCycleEditor): the session-only exclusion that
    /// Shift+click (by default) toggles too.</summary>
    internal Control CharacterCycle(string name)
    {
        var box = new CheckBox { IsChecked = !_host.IsExcludedFromCycle(name) };
        box.IsCheckedChanged += (_, _) => _host.SetInCycle(name, box.IsChecked == true);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Titled("character.cycle"));
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    // ── Thumbnails › Visibility › Second thumbnails ──

    // Copy of SettingsUi/Pages/Thumbnails/PipEditor.cs:SecondThumbnails (that file is WPF) — keep in sync.
    private static string? PipKeyOf(AppSettings s, string name) =>
        s.SecondaryThumbnails.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>One row per character with a second thumbnail (the Windows PipEditor): on, opacity, its page, remove; Add…
    /// below. The app makes or closes the second thumbnail within 2 s (Previews.SyncPips) and reads its opacity at every tick.</summary>
    internal Control PipTable(SettingsContext ctx)
    {
        var panel = Titled("thumbs.visibility.pipTable");
        var body = new StackPanel();
        panel.Children.Add(body);
        void Draw()
        {
            body.Children.Clear();
            var table = Table("*,Auto,Auto,Auto,Auto");
            int header = Row(table);
            Put(table, Head("L.S.Characters.ColCharacter"), header, 0);
            Put(table, Head("L.S.Pip.ColOn"), header, 1);
            Put(table, Head("L.S.Pip.ColOpacity"), header, 2);
            var names = ctx.S.SecondaryThumbnails.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) Put(table, Note("L.S.Pip.Empty"), Row(table), 0, span: 5);
            foreach (var name in names)
            {
                int r = Row(table);
                Put(table, new TextBlock { Text = name, FontWeight = FontWeight.SemiBold }, r, 0);
                var on = new CheckBox { IsChecked = CharacterFields.PipOn(ctx.S, name) };
                ToolTip.SetTip(on, string.Format(L("L.S.Pip.OnFor"), name));
                on.IsCheckedChanged += (_, _) => { CharacterFields.SetPip(ctx.S, name, on.IsChecked == true); Saved(ctx); };
                Put(table, on, r, 1);
                Put(table, SliderRow(8, 100, 1, CharacterFields.PipOpacity(ctx.S, name), v =>
                {
                    if (CharacterFields.PipOpacity(ctx.S, name) == (int)Math.Round(v)) return;
                    CharacterFields.SetPipOpacity(ctx.S, name, v);
                    Saved(ctx);
                }, L("L.S.Unit.Percent"), 160), r, 2);
                var open = new Button { Content = "›", Padding = new Thickness(10, 2) };
                ToolTip.SetTip(open, string.Format(L("L.S.Characters.Open"), name));
                open.Click += (_, _) => ctx.Host.Navigate(CharacterPage.IdFor(name));
                Put(table, open, r, 3);
                Put(table, Action("L.S.Pip.Remove", () =>
                {
                    if (!ctx.Host.Confirm(L("L.S.Pip.RemoveTitle"), string.Format(L("L.S.Pip.RemoveConfirm"), name))) return;
                    ctx.S.SecondaryThumbnails.Remove(name);   // with its place and size
                    Saved(ctx);
                    Draw();
                }, destructive: true), r, 4);
            }
            body.Children.Add(table);
            body.Children.Add(Action("L.S.Pip.Add", () =>
            {
                var picked = ctx.Host.PickCharacter(L("L.S.Pip.AddTitle"), CharacterDirectory.AllKnown(ctx.S, CharacterDirectory.Running(ctx)));
                if (picked == null) return;
                var name = picked.Trim();
                if (name.Length == 0) return;
                var key = PipKeyOf(ctx.S, name) ?? name;
                if (CharacterFields.PipOn(ctx.S, key)) { ctx.Host.Toast(string.Format(L("L.S.Pip.AddExists"), name)); return; }
                CharacterFields.SetPip(ctx.S, key, true);   // appears within 2 s if the client runs, else at its login
                Saved(ctx);
                Draw();
            }));
        }
        Draw();
        return panel;
    }

    // ── Alerts › Look ──

    /// <summary>Alerts › Look › Severity settings (the Windows SeverityTableEditor): colour, flash speed and the desktop
    /// notification for critical, warning and info. Read at the next alert or flash, so a change only saves. The colour is
    /// typed (no colour picker on Linux).</summary>
    internal Control SeverityTable(SettingsContext ctx)
    {
        var panel = Titled("alerts.look.severityTable");
        var table = Table("Auto,Auto,Auto,Auto");
        int header = Row(table);
        foreach (var (key, c) in new[] { ("L.S.Severity.ColSeverity", 0), ("L.S.Severity.ColColour", 1), ("L.S.Severity.ColFlash", 2), ("L.S.Severity.ColHub", 3) })
            Put(table, Head(key), header, c);
        foreach (var severity in AlertEvents.Severities)
        {
            int r = Row(table);
            var name = L(AlertEvents.SeverityKey(severity));
            Put(table, new TextBlock { Text = name }, r, 0);
            var colour = Ui.Colour(AlertFields.SeverityColour(ctx.S, severity), v => { AlertFields.SetSeverityColour(ctx.S, severity, v); Saved(ctx); });
            ToolTip.SetTip(colour, string.Format(L("L.S.Severity.PickColour"), name));
            Put(table, colour, r, 1);
            // Info alerts are painted steady, so their flash speed does nothing (ruling 16).
            Put(table, severity == AlertEvents.Info ? Note("L.S.Severity.InfoSteady")
                : SliderRow(AlertFields.FlashMin, AlertFields.FlashMax, AlertFields.FlashStep, AlertFields.FlashSlider(AlertFields.FlashRate(ctx.S, severity)), v =>
                {
                    var rate = (int)AlertFields.FlashSlider((int)Math.Round(v));
                    if (AlertFields.FlashRate(ctx.S, severity) == rate) return;
                    AlertFields.SetFlashRate(ctx.S, severity, rate);
                    Saved(ctx);
                }, L("L.S.Unit.Ms"), 160), r, 2);
            var hub = new CheckBox { IsChecked = AlertFields.HubToast(ctx.S, severity), HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(hub, string.Format(L("L.S.Severity.HubFor"), name));
            hub.IsCheckedChanged += (_, _) =>
            {
                if (AlertFields.HubToast(ctx.S, severity) == (hub.IsChecked == true)) return;
                AlertFields.SetHubToast(ctx.S, severity, hub.IsChecked == true);
                Saved(ctx);
            };
            Put(table, hub, r, 3);
        }
        panel.Children.Add(table);
        panel.Children.Add(Note("L.S.Severity.Note"));
        return panel;
    }

    /// <summary>Alerts › Look › Colour-blind mode (the Windows ColorBlindEditor): one switch; declining the question puts it back.</summary>
    internal Control ColorBlind(SettingsContext ctx)
    {
        var box = new CheckBox { IsChecked = ctx.S.ColorBlindMode };
        bool reading = false;
        box.IsCheckedChanged += (_, _) =>
        {
            if (reading) return;
            if (ColorBlindSwitch.Set(ctx, box.IsChecked == true)) return;   // it reloads the window
            reading = true;
            box.IsChecked = ctx.S.ColorBlindMode;
            reading = false;
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Titled("alerts.look.colorBlind"));
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    // ── Alerts › Sounds ──

    /// <summary>Alerts › Sounds › Do not disturb (the Windows DoNotDisturbStateEditor): a line while do-not-disturb is on
    /// right now, whatever switched it on; looked at again every second. Nothing here is saved.</summary>
    internal Control DoNotDisturbLine(SettingsContext ctx)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var bar = new Border
        {
            BorderBrush = Brushes.LightSkyBlue, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 6), Child = text,
        };
        void Look()
        {
            var key = _host.DoNotDisturb ? (ctx.S.DoNotDisturbCriticalPlays ? "L.S.DndState.OnCritical" : "L.S.DndState.On") : null;
            bar.IsVisible = key != null;
            if (key != null) text.Text = L(key);
        }
        Look();
        WhileShown(bar, TimeSpan.FromSeconds(1), Look);
        return bar;
    }

    // Copies of SettingsUi/Pages/Alerts/SoundTableEditor.cs:AlertSoundFields (that file is WPF) — keep in sync.
    private const int MaxSoundWait = 3600;
    private static string OwnSound(AppSettings s, string key) => s.AlertSounds.TryGetValue(key, out var f) ? f?.Trim() ?? "" : "";
    private static bool BlankOwnSound(AppSettings s, string key) =>
        s.AlertSounds.TryGetValue(key, out var f) && !string.IsNullOrEmpty(f) && string.IsNullOrWhiteSpace(f);
    private static int SoundWait(AppSettings s, string key) => s.SoundCooldowns.TryGetValue(key, out var w) && w > 0 ? w : 0;

    /// <summary>Alerts › Sounds › Sound for each event (the Windows SoundTableEditor): the default sound, then each event's own
    /// file with Change…, Built-in…, Play, Clear and its Wait. The alerts read all of it at the next alert: a change only saves.</summary>
    internal Control SoundTable(SettingsContext ctx)
    {
        var panel = Titled("alerts.sounds.table");
        var body = new StackPanel();
        var error = Error();
        panel.Children.Add(body);
        panel.Children.Add(error);
        panel.Children.Add(Note("L.S.Sounds.Note"));

        void Store(string? key, string path)
        {
            path = path.Trim();
            if (key == null) ctx.S.AlertSoundFile = path;
            else if (path.Length == 0) ctx.S.AlertSounds.Remove(key);
            else ctx.S.AlertSounds[key] = path;
            Saved(ctx);
            Draw();
        }
        void Take(string? key, string picked)
        {
            if (CustomRuleCheck.LocalSound(picked).Length == 0) ctx.Host.Toast(L(AlertsPages.SoundRefusedKey(picked)));
            else Store(key, picked);
        }
        void Draw()
        {
            body.Children.Clear();
            var table = Table("Auto,*,Auto,Auto");
            int header = Row(table);
            Put(table, Head("L.S.Sounds.ColEvent"), header, 0);
            Put(table, Head("L.S.Sounds.ColSound"), header, 1);
            Put(table, Head("L.S.Sounds.ColWait"), header, 3);
            AddRow(table, null, L("L.S.Sounds.Default"), ctx.S.AlertSoundFile?.Trim() ?? "", false);
            foreach (var e in AlertEvents.Listed)
                AddRow(table, e.Key, LocalizationService.Str(e.LabelKey, e.Key), OwnSound(ctx.S, e.Key), BlankOwnSound(ctx.S, e.Key));
            body.Children.Add(table);
        }
        void AddRow(Grid table, string? key, string name, string own, bool blank)
        {
            int r = Row(table);
            Put(table, new TextBlock { Text = name, FontWeight = key == null ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 }, r, 0);
            Put(table, own.Length > 0 ? new TextBlock { Text = Path.GetFileName(own), Opacity = 0.8, TextWrapping = TextWrapping.Wrap }
                : Note(key == null || blank ? "L.S.Sounds.None" : "L.S.Sounds.UsesDefault"), r, 1);
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            var change = Action("L.S.Sounds.Change", () =>
            {
                var current = key == null ? ctx.S.AlertSoundFile : OwnSound(ctx.S, key);
                if (ctx.Host.PickFile(current, AlertsPages.SoundFilter) is { } picked) Take(key, picked);
            });
            ToolTip.SetTip(change, string.Format(L("L.S.Sounds.ChangeFor"), name));
            buttons.Children.Add(change);
            buttons.Children.Add(ToneButton(name, tone =>
            {
                // ToneMenu.Write: the tone is written into the settings folder and stored as a picked file is.
                if (ToneSounds.Ensure(tone, ctx.Service.ConfigDirectory) is { } path) Take(key, path);
                else if (CustomRuleCheck.LocalSound(ToneSounds.PathOf(tone, ctx.Service.ConfigDirectory)).Length == 0) Take(key, ToneSounds.PathOf(tone, ctx.Service.ConfigDirectory));
                else ctx.Host.Toast(L("L.S.Sounds.ToneFailed"));
            }));
            var play = Action("L.S.Sounds.Play", () =>
            {
                var file = key == null ? ctx.S.AlertSoundFile
                    : BlankOwnSound(ctx.S, key) ? "" : OwnSound(ctx.S, key) is { Length: > 0 } mine ? mine : ctx.S.AlertSoundFile?.Trim() ?? "";
                // As the Windows Play: a file on a drive of this computer (CustomRuleCheck.LocalSound), at the alert volume.
                if (string.IsNullOrWhiteSpace(file) || CustomRuleCheck.LocalSound(file).Length == 0 || !EveMultiPreview.Linux.Alerts.PlayFile(file, ctx.S.AlertSoundVolume))
                    ctx.Host.Toast(L("L.S.Sounds.Missing"));
            });
            ToolTip.SetTip(play, string.Format(L("L.S.Sounds.PlayFor"), name));
            buttons.Children.Add(play);
            if (own.Length > 0 || blank)
            {
                var clear = Action("L.S.Sounds.Clear", () => Store(key, ""));
                ToolTip.SetTip(clear, string.Format(L("L.S.Sounds.ClearFor"), name));
                buttons.Children.Add(clear);
            }
            Put(table, buttons, r, 2);
            if (key == null) return;
            string given = SoundWait(ctx.S, key).ToString(CultureInfo.CurrentCulture);
            var wait = new TextBox { Text = given, Width = 64 };
            ToolTip.SetTip(wait, string.Format(L("L.S.Sounds.WaitFor"), name));
            void Commit()
            {
                if (wait.Text == given) return;
                var t = wait.Text?.Trim() ?? "";
                int seconds = 0;
                if (t.Length > 0 && !(ThumbnailFields.TryWhole(t, out seconds) && seconds >= 0 && seconds <= MaxSoundWait))
                {
                    wait.Text = given;
                    Say(error, string.Format(CultureInfo.CurrentCulture, L("L.S.Sounds.WaitError"), 0, MaxSoundWait));
                    return;
                }
                Say(error, null);
                if (SoundWait(ctx.S, key) != seconds)
                {
                    if (seconds <= 0) ctx.S.SoundCooldowns.Remove(key);
                    else ctx.S.SoundCooldowns[key] = Math.Min(seconds, MaxSoundWait);
                    Saved(ctx);
                }
                wait.Text = given = SoundWait(ctx.S, key).ToString(CultureInfo.CurrentCulture);
            }
            wait.LostFocus += (_, _) => Commit();
            wait.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            Put(table, wait, r, 3);
        }
        Draw();
        return panel;
    }

    /// <summary>"Built-in…" (the Windows ToneMenu): a menu of the generated tones, named from the catalog.</summary>
    private static Button ToneButton(string name, Action<Tone> picked)
    {
        var button = Action("L.S.Sounds.Tone", () => { });
        ToolTip.SetTip(button, string.Format(L("L.S.Sounds.ToneFor"), name));
        var menu = new ContextMenu();
        foreach (var tone in ToneSounds.All)
        {
            var chosen = tone;
            var item = new MenuItem { Header = new TextBlock { Text = L(ToneSounds.NameKey(chosen)) } };
            item.Click += (_, _) => picked(chosen);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.Open(button);
        return button;
    }
}
