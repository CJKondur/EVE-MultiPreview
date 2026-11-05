using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S6: Thumbnails › Layout &amp; screens on Linux: the preferred monitor (Home and Layout), saved layouts, align /
/// distribute / nudge, and the layout history. Avalonia ports of MonitorPickerEditor, LayoutPresetsEditor, LayoutAlignEditor
/// and LayoutHistoryEditor (keep in sync) over the shared LayoutPresetService, ShareCode, LayoutAlign and LayoutImage plan;
/// the wall's work is Previews' (Linux/PreviewsLayout.cs).</summary>
internal sealed partial class SettingsEditors
{
    /// <summary>MonitorPickerEditor: PreferredMonitor, 1-based, "Monitor 1: 2560×1440 (primary)".</summary>
    internal Control MonitorPicker(SettingsContext ctx, string id)
    {
        var panel = Titled(id);
        var list = new ComboBox { MinWidth = 260 };
        var screens = _window.Screens?.All ?? Array.Empty<Avalonia.Platform.Screen>();
        for (int i = 0; i < screens.Count; i++)
        {
            var text = string.Format(S3("L.S.Monitor.Item"), i + 1, screens[i].Bounds.Width, screens[i].Bounds.Height);
            if (screens[i].IsPrimary) text += " " + S3("L.S.Monitor.Primary");
            list.Items.Add(new TextBlock { Text = text });
        }
        int chosen = ctx.S.PreferredMonitor;
        list.SelectedIndex = chosen >= 1 && chosen <= screens.Count ? chosen - 1 : -1;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex + 1 == ctx.S.PreferredMonitor) return;
            ctx.S.PreferredMonitor = list.SelectedIndex + 1;
            ctx.Applier.Request(ApplyScope.Thumbnails);   // where new thumbnails and unsaved ones go
        };
        panel.Children.Add(list);
        return panel;
    }

    /// <summary>LayoutPresetsEditor: the layouts file every profile shares; Apply, Remap and apply, Delete, Save current, Export,
    /// Import, Copy code, Paste code, the layout and the wall as pictures (names hidden on request, always in streamer mode).</summary>
    internal Control LayoutPresets(SettingsContext ctx)
    {
        var library = new LayoutPresetService(ctx.Service.ConfigDirectory);
        var panel = Titled("thumbs.layout.presets");
        var list = new ComboBox { MinWidth = 220 };
        var note = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var arrangement = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var needsOne = new List<Button>();
        var hideNames = Ui.Check(S3("L.S.Presets.HideNames"), StreamerMask.IsOn, _ => { });
        string? selected = null;
        IReadOnlyList<string> names = Array.Empty<string>();
        bool loading = false;
        string Applied() => CharacterDirectory.Current(ctx.S).AppliedLayoutPreset;
        string? Find(string? name) => string.IsNullOrWhiteSpace(name) ? null : names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        const string saveFilterTail = " (*.emplayout)|*.emplayout|JSON (*.json)|*.json";
        string OpenFilter() => S3("L.S.Filter.Layouts") + " (*.emplayout, *.json)|*.emplayout;*.json|*.*|*.*";

        Button NeedsOne(string key, Action run, bool destructive = false)
        {
            var b = Action(key, run, destructive);
            needsOne.Add(b);
            return b;
        }

        void UpdateButtons()
        {
            foreach (var b in needsOne) b.IsEnabled = selected != null;
            var preset = selected == null ? null : library.Get(selected);
            int count = MonitorFingerprint.Count(preset?.MonitorFingerprint);
            arrangement.Text = preset == null ? ""
                : count < 0 ? S3("L.S.Presets.ArrangementNone")
                : string.Format(S3(preset.MonitorFingerprint == MonitorFingerprint.Of(_host.CurrentMonitorBounds()) ? "L.S.Presets.ArrangementNow" : "L.S.Presets.Arrangement"), count);
            arrangement.IsVisible = arrangement.Text.Length > 0;
        }

        void Refresh()
        {
            if (StreamerMask.IsOn) hideNames.IsChecked = true;
            library.Load();
            names = library.Names;
            selected = Find(selected) ?? Find(Applied());
            loading = true;
            list.Items.Clear();
            foreach (var n in names) list.Items.Add(n);
            list.SelectedItem = selected;
            loading = false;
            note.Text = names.Count == 0 ? S3("L.S.Presets.Empty")
                : string.IsNullOrWhiteSpace(Applied()) ? S3("L.S.Presets.NoneUsed")
                : string.Format(S3("L.S.Presets.Current"), Applied());
            UpdateButtons();
        }

        LayoutPreset? Chosen()
        {
            var preset = selected == null ? null : library.Get(selected);
            if (preset == null) ctx.Host.Toast(S3("L.S.Presets.PickFirst"));
            return preset;
        }

        bool Remap()
        {
            if (Chosen() is not { } preset) return false;
            var running = CharacterDirectory.Running(ctx);
            if (running.Count == 0) { ctx.Host.Toast(S3("L.S.Presets.NoClients")); return false; }
            if (ctx.Host is not SharedSettingsWindow window) return false;
            if (window.AskMapping(preset.Slots.Select(s => s.Character).ToList(), running, profileCode: false) is not { } mapping) return false;
            _host.ApplyLayoutPresetMapped(preset, mapping);
            if (preset.IncludesVisibility) ctx.Applier.Request(ApplyScope.Crops);
            ctx.Host.Toast(string.Format(S3("L.S.Presets.Applied"), preset.Name));
            return true;
        }

        void SaveImage(LayoutPreset preset, string name)
        {
            var monitors = _host.CurrentMonitors();
            bool hide = hideNames.IsChecked == true || StreamerMask.IsOn;
            var plan = LayoutImage.Plan(LayoutImage.Resolve(preset, monitors), monitors, hide);
            if (plan.Boxes.Count == 0) { ctx.Host.Toast(S3("L.S.Presets.ImageEmpty")); return; }
            var file = ctx.Host.PickSaveFile((hide ? "layout" : LayoutImage.FileStem(name)) + ".png", "PNG (*.png)|*.png");
            if (file == null) return;
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, LinuxLayoutImage.Png(plan));
                File.Move(temp, file, overwrite: true);
                ctx.Host.Toast(string.Format(S3("L.S.Presets.ImageSaved"), Path.GetFileName(file)));
            }
            catch (Exception)
            {
                try { File.Delete(temp); } catch (Exception) { }
                ctx.Host.Toast(S3("L.S.Presets.ImageFailed"));
            }
        }

        bool AddOrAsk(LayoutPreset preset)
        {
            if (library.Get(preset.Name) != null
                && !ctx.Host.Confirm(S3("L.S.Presets.ReplaceTitle"), string.Format(S3("L.S.Presets.ReplaceConfirm"), preset.Name))) return false;
            library.AddOrReplace(preset);
            selected = preset.Name;
            Refresh();
            return true;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            selected = list.SelectedItem as string;
            UpdateButtons();
        };
        var first = new WrapPanel { Orientation = Orientation.Horizontal };
        first.Children.Add(Spaced(list));
        first.Children.Add(NeedsOne("L.S.Presets.Apply", () =>
        {
            if (Chosen() is not { } preset) return;
            _host.ApplyLayoutPreset(preset);
            CharacterDirectory.Current(ctx.S).AppliedLayoutPreset = preset.Name;   // #94: named when switching profiles
            ctx.Applier.Request(preset.IncludesVisibility ? ApplyScope.Save | ApplyScope.Crops : ApplyScope.Save);
            ctx.Host.Toast(string.Format(S3("L.S.Presets.Applied"), preset.Name));
            Refresh();
        }));
        first.Children.Add(NeedsOne("L.S.Presets.Remap", () => Remap()));
        first.Children.Add(NeedsOne("L.S.Presets.Delete", () =>
        {
            if (selected == null || library.Get(selected) == null) return;
            if (!ctx.Host.Confirm(S3("L.S.Presets.DeleteTitle"), string.Format(S3("L.S.Presets.DeleteConfirm"), selected))) return;
            library.Delete(selected);
            selected = null;
            Refresh();
        }, destructive: true));
        panel.Children.Add(first);
        panel.Children.Add(Buttons(
            Action("L.S.Presets.Save", () =>
            {
                if (CharacterDirectory.Running(ctx).Count == 0) { ctx.Host.Toast(S3("L.S.Presets.NoThumbnails")); return; }
                var name = ctx.Host.AskText(S3("L.S.Presets.SaveTitle"), S3("L.S.Presets.NamePrompt"), selected ?? "")?.Trim();
                if (string.IsNullOrEmpty(name)) return;
                var preset = _host.CaptureLayout(name);
                if (preset == null || preset.Slots.Count == 0) { ctx.Host.Toast(S3("L.S.Presets.NoThumbnails")); return; }
                if (AddOrAsk(preset)) ctx.Host.Toast(string.Format(S3("L.S.Presets.Saved"), name));
            }),
            NeedsOne("L.S.Presets.Export", () =>
            {
                if (Chosen() is not { } preset) return;
                var file = ctx.Host.PickSaveFile(preset.Name + ".emplayout", S3("L.S.Filter.Layouts") + saveFilterTail);
                if (file == null) return;
                try
                {
                    library.Export(preset, file);
                    ctx.Host.Toast(string.Format(S3("L.S.Presets.Exported"), Path.GetFileName(file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    ctx.Host.Toast(S3("L.S.Presets.ExportFailed"));
                }
            }),
            Action("L.S.Presets.Import", () =>
            {
                var file = ctx.Host.PickFile(null, OpenFilter());
                if (file == null) return;
                var preset = library.ReadFile(file);
                if (preset == null) { ctx.Host.Toast(S3("L.S.Presets.ImportFailed")); return; }
                if (AddOrAsk(preset)) ctx.Host.Toast(string.Format(S3("L.S.Presets.Imported"), preset.Name));
            })));
        panel.Children.Add(Buttons(
            NeedsOne("L.S.Presets.CopyCode", () =>
            {
                if (Chosen() is not { } preset) return;
                var code = ShareCode.Layout(preset);
                if (code == null) { ctx.Host.Toast(S3("L.S.Presets.CodeTooBig")); return; }
                bool ok = ctx.Host is SharedSettingsWindow w && w.CopyText(code);
                var key = !ok ? "L.S.Presets.CodeCopyFailed" : code.Length > ShareCode.DiscordSafeLength ? "L.S.Presets.CodeLong" : "L.S.Presets.CodeCopied";
                ctx.Host.Toast(ok ? string.Format(S3(key), preset.Name, code.Length) : S3(key));
            }),
            Action("L.S.Presets.PasteCode", () =>
            {
                var text = ctx.Host.AskText(S3("L.S.Presets.PasteTitle"), S3("L.S.Presets.PastePrompt"), "");
                if (text == null) return;
                var read = ShareCode.ReadLayout(text);
                if (read.Value is not { } preset) { ctx.Host.Toast(S3(read.ErrorKey)); return; }
                if (!AddOrAsk(preset)) return;
                // With clients running, Remap and apply opens at once: the code's characters are someone else's.
                if (CharacterDirectory.Running(ctx).Count == 0 || !Remap()) ctx.Host.Toast(string.Format(S3("L.S.Presets.CodeAdded"), preset.Name));
            })));
        var images = Buttons(
            NeedsOne("L.S.Presets.ExportImage", () => { if (Chosen() is { } preset) SaveImage(preset, preset.Name); }),
            Action("L.S.Presets.WallImage", () =>
            {
                if (_host.CaptureLayout("") is not { } preset) { ctx.Host.Toast(S3("L.S.Presets.NoThumbnails")); return; }
                SaveImage(preset, "layout");
            }));
        images.Children.Add(hideNames);
        panel.Children.Add(images);
        panel.Children.Add(note);
        panel.Children.Add(arrangement);
        Refresh();
        return panel;
    }

    /// <summary>LayoutAlignEditor (IDEAS #36): tick the logged-in characters, then align an edge, space evenly, make the same
    /// size or nudge 1 or 10 px. Each button is one Previews.ApplyAlign (one undo step). The first ticked is the one Match copies.
    /// The list follows characters logging in and out every 2 s while it shows.</summary>
    internal Control AlignCard(SettingsContext ctx)
    {
        (AlignOp Op, string Key)[] operations =
        {
            (AlignOp.Left, "L.S.Act.thumbs.layout.alignLeft"), (AlignOp.Right, "L.S.Act.thumbs.layout.alignRight"),
            (AlignOp.Top, "L.S.Act.thumbs.layout.alignTop"), (AlignOp.Bottom, "L.S.Act.thumbs.layout.alignBottom"),
            (AlignOp.DistributeH, "L.S.Act.thumbs.layout.distributeH"), (AlignOp.DistributeV, "L.S.Act.thumbs.layout.distributeV"),
            (AlignOp.MatchWidth, "L.S.Act.thumbs.layout.matchWidth"), (AlignOp.MatchHeight, "L.S.Act.thumbs.layout.matchHeight"),
        };
        (string Glyph, string Key, int Dx, int Dy)[] arrows = { ("←", "L.S.Align.NudgeLeft", -1, 0), ("→", "L.S.Align.NudgeRight", 1, 0), ("↑", "L.S.Align.NudgeUp", 0, -1), ("↓", "L.S.Align.NudgeDown", 0, 1) };
        var ticked = new List<string>();
        var listed = new List<string>();
        string signature = "\0";
        var panel = Titled("thumbs.layout.align");
        panel.Children.Add(LinuxLooks.Text(S3("L.S.Align.Pick"), "Label"));
        var none = new TextBlock { Text = S3("L.S.Align.None"), Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
        var boxes = new WrapPanel { Orientation = Orientation.Horizontal };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var buttons = new Dictionary<AlignOp, Button>();
        var nudges = new List<Button>();
        bool syncing = false;
        Button all = null!, clear = null!;
        bool Has(IEnumerable<string> names, string name) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        void UpdateButtons()
        {
            foreach (var (op, b) in buttons) b.IsEnabled = ticked.Count >= EveMultiPreview.Services.LayoutAlign.MinItems(op);
            foreach (var b in nudges) b.IsEnabled = ticked.Count >= EveMultiPreview.Services.LayoutAlign.MinItems(AlignOp.Nudge);
            all.IsEnabled = listed.Count > 0;
            clear.IsEnabled = ticked.Count > 0;
            count.Text = string.Format(CultureInfo.CurrentCulture, S3("L.S.Align.Ticked"), ticked.Count);
        }
        void SyncBoxes()
        {
            syncing = true;
            foreach (var box in boxes.Children.OfType<CheckBox>()) box.IsChecked = Has(ticked, (string)box.Tag!);
            syncing = false;
        }
        void Refresh()
        {
            var names = CharacterDirectory.Running(ctx).ToList();
            var now = string.Join("\n", names);
            if (now != signature)
            {
                signature = now;
                listed.Clear();
                listed.AddRange(names);
                ticked.RemoveAll(t => !Has(listed, t));
                boxes.Children.Clear();
                foreach (var name in listed)
                {
                    var who = name;
                    var box = Ui.Check(who, Has(ticked, who), on =>
                    {
                        if (syncing) return;
                        int at = ticked.FindIndex(n => string.Equals(n, who, StringComparison.OrdinalIgnoreCase));
                        if (on && at < 0) ticked.Add(who);
                        else if (!on && at >= 0) ticked.RemoveAt(at);
                        UpdateButtons();
                    });
                    box.Tag = who;
                    box.Margin = new Thickness(0, 2, 16, 2);
                    boxes.Children.Add(box);
                }
                none.IsVisible = listed.Count == 0;
            }
            UpdateButtons();
        }
        void Run(AlignOp op, int dx = 0, int dy = 0)
        {
            if (ticked.Count < EveMultiPreview.Services.LayoutAlign.MinItems(op)) return;
            _host.ApplyAlign(op, ticked.ToList(), dx, dy);
            Refresh();
        }

        all = Action("L.S.Align.All", () => { foreach (var n in listed) if (!Has(ticked, n)) ticked.Add(n); SyncBoxes(); UpdateButtons(); });
        clear = Action("L.S.Align.Clear", () => { ticked.Clear(); SyncBoxes(); UpdateButtons(); });
        panel.Children.Add(none);
        panel.Children.Add(boxes);
        var row = Buttons(all, clear);
        row.Children.Add(count);
        panel.Children.Add(row);
        var ops = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (op, key) in operations)
        {
            var captured = op;
            var b = Action(key, () => Run(captured));
            buttons[op] = b;
            ops.Children.Add(b);
        }
        panel.Children.Add(ops);
        foreach (var step in new[] { 1, 10 })
        {
            var strip = new WrapPanel { Orientation = Orientation.Horizontal };
            strip.Children.Add(new TextBlock { Text = S3(step == 1 ? "L.S.Align.Nudge1" : "L.S.Align.Nudge10"), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            foreach (var (glyph, key, dx, dy) in arrows)
            {
                var (sx, sy) = (dx * step, dy * step);
                var b = new Button { Content = glyph, MinWidth = 36, Margin = new Thickness(0, 2, 6, 2) };
                AutomationName(b, string.Format(CultureInfo.CurrentCulture, S3(key), step));
                b.Click += (_, _) => Run(AlignOp.Nudge, sx, sy);
                nudges.Add(b);
                strip.Children.Add(b);
            }
            panel.Children.Add(strip);
        }
        panel.Children.Add(new TextBlock { Text = S3("L.S.Align.Hint"), Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        WhileShown(panel, TimeSpan.FromSeconds(2), Refresh);
        Refresh();
        return panel;
    }

    /// <summary>LayoutHistoryEditor: Undo, Redo (greyed out while the history has nothing) and Reset all positions (asks first).
    /// Followed every second while it shows: the undo keys and Size for all change the history meanwhile.</summary>
    internal Control LayoutHistory(SettingsContext ctx)
    {
        var panel = Titled("thumbs.layout.history");
        Button undo = null!, redo = null!;
        void Refresh()
        {
            undo.IsEnabled = _host.CanUndoLayout;
            redo.IsEnabled = _host.CanRedoLayout;
        }
        undo = Action("L.S.Act.thumbs.layout.undo", () => { _host.UndoLayout(); Refresh(); });
        redo = Action("L.S.Act.thumbs.layout.redo", () => { _host.RedoLayout(); Refresh(); });
        var reset = Action("L.S.Act.thumbs.layout.reset", () =>
        {
            if (!ctx.Host.Confirm(S3("L.S.Layout.ResetTitle"), S3("L.S.Layout.ResetConfirm"))) return;
            _host.ResetThumbnailPositions();
            Refresh();
        }, destructive: true);
        panel.Children.Add(Buttons(undo, redo, reset));
        WhileShown(panel, TimeSpan.FromSeconds(1), Refresh);
        Refresh();
        return panel;
    }
}

/// <summary>LayoutImage.Render / Png drawn with Avalonia (the plan is the shared LayoutImage.Plan): the background, the monitors
/// with their numbers, the boxes with their text. Drawn text only; no client is read. Copied colours and sizes: keep in sync.</summary>
internal static class LinuxLayoutImage
{
    private static IBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    public static byte[] Png(LayoutImagePlan plan)
    {
        if (plan.Width < 1 || plan.Height < 1) throw new InvalidOperationException("Nothing to draw.");
        IBrush background = Brush(0x1B, 0x1F, 0x27), monitorFill = Brush(0x27, 0x2D, 0x38), monitorText = Brush(0x6C, 0x77, 0x89),
            boxFill = Brush(0x2F, 0x5E, 0x9E), boxText = Brush(0xF2, 0xF6, 0xFC);
        var monitorPen = new Pen(Brush(0x5A, 0x66, 0x7A), 2);
        var boxPen = new Pen(Brush(0x8C, 0xB8, 0xF0), 1.5);
        using var bitmap = new RenderTargetBitmap(new PixelSize(plan.Width, plan.Height), new Vector(96, 96));
        using (var dc = bitmap.CreateDrawingContext())
        {
            dc.FillRectangle(background, new Rect(0, 0, plan.Width, plan.Height));
            foreach (var monitor in plan.Monitors)
            {
                var r = new Rect(monitor.Rect.X, monitor.Rect.Y, monitor.Rect.Width, monitor.Rect.Height);
                dc.DrawRectangle(monitorFill, monitorPen, r);
                dc.DrawText(Text(monitor.Label, 16, monitorText, r.Width - 12, r.Height - 8, TextAlignment.Left), new Point(r.X + 8, r.Y + 6));
            }
            foreach (var box in plan.Boxes)
            {
                var r = new Rect(box.Rect.X, box.Rect.Y, box.Rect.Width, box.Rect.Height);
                dc.DrawRectangle(boxFill, boxPen, r);
                var text = Text(box.Label, Math.Max(9, Math.Min(22, r.Height * 0.28)), boxText, Math.Max(1, r.Width - 6), Math.Max(1, r.Height - 4), TextAlignment.Center);
                using (dc.PushClip(r)) dc.DrawText(text, new Point(r.X + 3, r.Y + Math.Max(2, (r.Height - text.Height) / 2)));
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Player text goes in as the text to draw, never as a format.</summary>
    private static FormattedText Text(string text, double size, IBrush brush, double maxWidth, double maxHeight, TextAlignment align) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = Math.Max(1, maxHeight),
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = align,
        };
}
