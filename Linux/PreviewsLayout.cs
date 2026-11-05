using Avalonia;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Rectangle = System.Drawing.Rectangle;

namespace EveMultiPreview.Linux;

/// <summary>S6: the thumbnail wall's layout work of ThumbnailManager, on Linux (copied, keep in sync): the preferred monitor,
/// Reset positions, the layout history (undo / redo), saved layouts (capture, apply, apply mapped, pre-placed characters not
/// logged in), align / distribute / nudge (the shared LayoutAlign does the arithmetic) and the layout applied by itself when
/// the monitors change (the shared MonitorLayoutFollower). Monitors are Avalonia's screens, in their own order; a layout's
/// slot fractions are of a monitor's work area, as on Windows.</summary>
public sealed partial class Previews
{
    private const int MaxLayoutHistory = 25;
    private readonly List<Dictionary<string, ThumbnailRect>> _layoutUndo = new(), _layoutRedo = new();

    private IReadOnlyList<Avalonia.Platform.Screen> AllScreens => AnyScreens?.All ?? Array.Empty<Avalonia.Platform.Screen>();

    private static Rectangle R(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The bounds of every monitor now, as MonitorFingerprint takes them (ThumbnailManager.CurrentMonitorBounds).</summary>
    public IReadOnlyList<Rectangle> CurrentMonitorBounds() => AllScreens.Select(s => R(s.Bounds)).ToList();

    /// <summary>The monitors as the layout image and its plan see them.</summary>
    public IReadOnlyList<LayoutMonitor> CurrentMonitors() => AllScreens.Select(s => new LayoutMonitor(R(s.Bounds), R(s.WorkingArea), s.IsPrimary)).ToList();

    /// <summary>ThumbnailManager.GetTargetMonitorWorkArea: the preferred monitor's work area (1-based), else the primary's.</summary>
    private Rectangle TargetWorkArea(int preferredMonitor)
    {
        var screens = AllScreens;
        int idx = preferredMonitor - 1;
        if (idx >= 0 && idx < screens.Count) return R(screens[idx].WorkingArea);
        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        return primary != null ? R(primary.WorkingArea) : new Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>ThumbnailManager.EnsureOnScreen: a place on no monitor at all is anchored onto the primary work area.</summary>
    private (int X, int Y) EnsureOnScreen(int x, int y, int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        var rect = new Rectangle(x, y, w, h);
        var screens = AllScreens;
        if (screens.Count == 0 || screens.Any(s => R(s.WorkingArea).IntersectsWith(rect))) return (x, y);
        var wa = TargetWorkArea(0);
        int nx = w >= wa.Width ? wa.Left : Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        int ny = h >= wa.Height ? wa.Top : Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        return (nx, ny);
    }

    LayoutPreset? ISettingsHost.CaptureLayout(string name) => _thumbs.Count == 0 ? null : CaptureCurrentLayoutPreset(name);

    // ── Reset positions ──

    /// <summary>ThumbnailManager.ResetThumbnailPositions: every thumbnail back to the column a fresh start lays out (one undo
    /// step); the profile's saved places are dropped first so they can't win.</summary>
    public void ResetThumbnailPositions()
    {
        PushLayoutSnapshot();
        _settings.ClearThumbnailPositions();
        int width = (int)S.ThumbnailStartLocation.Width, height = (int)S.ThumbnailStartLocation.Height;
        var wa = TargetWorkArea(S.PreferredMonitor);
        int index = 0;
        foreach (var thumb in _thumbs.Values)
        {
            var (x, y) = FlowSlot(index, wa.Left + (int)S.ThumbnailStartLocation.X, wa.Top + (int)S.ThumbnailStartLocation.Y, width, height, S.ThumbnailGutter, wa.Bottom);
            (x, y) = EnsureOnScreen(x, y, width, height);
            thumb.PlaceAt(x, y, Math.Max(1, width), Math.Max(1, height));
            index++;
        }
        _settings.Save();
    }

    /// <summary>Copy of ThumbnailManager.FlowSlot (a WPF file) — keep in sync.</summary>
    public static (int X, int Y) FlowSlot(int index, int startX, int startY, int width, int height, int gutter, int bottom)
    {
        int gap = Math.Clamp(gutter, 0, 200);
        int perColumn = Math.Max(1, (bottom - startY) / Math.Max(1, height + gap));
        return (startX + index / perColumn * (width + gap), startY + index % perColumn * (height + gap));
    }

    // ── Layout undo / redo ──

    /// <summary>The wall as it is (saved places, overlaid with the live thumbnails) onto the undo list, before a change of many
    /// thumbnails at once. Clears redo.</summary>
    public void PushLayoutSnapshot()
    {
        _layoutUndo.Add(CaptureLiveLayout());
        if (_layoutUndo.Count > MaxLayoutHistory) _layoutUndo.RemoveAt(0);
        _layoutRedo.Clear();
    }

    public bool CanUndoLayout => _layoutUndo.Count > 0;
    public bool CanRedoLayout => _layoutRedo.Count > 0;

    public bool UndoLayout()
    {
        if (_layoutUndo.Count == 0) { Alerts.Say(LocalizationService.Str("L.Msg.NothingToUndo", "Nothing to undo")); return false; }
        _layoutRedo.Add(CaptureLiveLayout());
        var snap = _layoutUndo[^1];
        _layoutUndo.RemoveAt(_layoutUndo.Count - 1);
        RestoreLayout(snap);
        Alerts.Say(LocalizationService.Str("L.Msg.LayoutUndone", "Layout: undone"));
        return true;
    }

    public bool RedoLayout()
    {
        if (_layoutRedo.Count == 0) { Alerts.Say(LocalizationService.Str("L.Msg.NothingToRedo", "Nothing to redo")); return false; }
        _layoutUndo.Add(CaptureLiveLayout());
        var snap = _layoutRedo[^1];
        _layoutRedo.RemoveAt(_layoutRedo.Count - 1);
        RestoreLayout(snap);
        Alerts.Say(LocalizationService.Str("L.Msg.LayoutRedone", "Layout: redone"));
        return true;
    }

    private Dictionary<string, ThumbnailRect> CaptureLiveLayout()
    {
        var snap = _settings.GetThumbnailPositionsSnapshot();
        foreach (var thumb in _thumbs.Values)
            if (!string.IsNullOrEmpty(thumb.Client.Character))
                snap[thumb.Client.Character] = new ThumbnailRect { X = thumb.OwnPosition.X, Y = thumb.OwnPosition.Y, Width = thumb.OwnWidth, Height = thumb.OwnHeight };
        return snap;
    }

    private void RestoreLayout(Dictionary<string, ThumbnailRect> snap)
    {
        _settings.RestoreThumbnailPositions(snap);
        foreach (var thumb in _thumbs.Values)
            if (snap.TryGetValue(thumb.Client.Character, out var r))
                thumb.PlaceAt((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
    }

    // ── Saved layouts (monitor-relative, shareable) ──

    /// <summary>ThumbnailManager.CaptureCurrentLayoutPreset: each live character's place and size as fractions of the work area of
    /// the monitor it is on, its visibility, the crop switch and the monitors' arrangement.</summary>
    public LayoutPreset CaptureCurrentLayoutPreset(string name)
    {
        var preset = new LayoutPreset { Name = name, IncludesVisibility = true, CropsEnabled = S.CropEnabled };
        var screens = AllScreens;
        preset.MonitorFingerprint = MonitorFingerprint.Of(screens.Select(s => R(s.Bounds)));
        foreach (var thumb in _thumbs.Values)
        {
            var character = thumb.Client.Character;
            if (string.IsNullOrEmpty(character)) continue;
            var at = thumb.OwnPosition;
            int w = Math.Max(1, thumb.OwnWidth), h = Math.Max(1, thumb.OwnHeight);
            var screen = AnyScreens?.ScreenFromBounds(new PixelRect(at.X, at.Y, w, h));
            int mon = screen == null ? 0 : Math.Max(0, screens.ToList().FindIndex(s => s.Equals(screen)));
            var wa = screen != null ? R(screen.WorkingArea) : TargetWorkArea(0);
            double ww = Math.Max(1, wa.Width), wh = Math.Max(1, wa.Height);
            preset.Slots.Add(new LayoutSlot
            {
                Character = character, Monitor = mon,
                Fx = (at.X - wa.Left) / ww, Fy = (at.Y - wa.Top) / wh, Fw = w / ww, Fh = h / wh,
                Hidden = Hidden(character),
            });
        }
        return preset;
    }

    /// <summary>ThumbnailManager.ApplyLayoutPreset: slots go to the characters of their name, the slots left to the other live
    /// thumbnails in name order, and slots left after that are saved for their characters (#94), so a character not logged in
    /// yet appears there. One undo step.</summary>
    public void ApplyLayoutPreset(LayoutPreset preset, bool forMonitors = false)
    {
        PushLayoutSnapshot();
        var live = _thumbs.Values.Where(t => !string.IsNullOrEmpty(t.Client.Character)).ToList();
        var used = new HashSet<ThumbnailWindow>();
        bool applyVis = preset.IncludesVisibility;
        var leftover = new List<LayoutSlot>();
        foreach (var slot in preset.Slots)
        {
            var thumb = live.FirstOrDefault(t => !used.Contains(t) && string.Equals(t.Client.Character, slot.Character, StringComparison.OrdinalIgnoreCase));
            if (thumb != null) { ApplyPresetSlot(thumb, slot, applyVis); used.Add(thumb); }
            else leftover.Add(slot);
        }
        var remaining = live.Where(t => !used.Contains(t)).OrderBy(t => t.Client.Character, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < leftover.Count && i < remaining.Count; i++) ApplyPresetSlot(remaining[i], leftover[i], applyVis);
        int preStaged = 0;
        for (int i = remaining.Count; i < leftover.Count; i++)
        {
            var slot = leftover[i];
            if (string.IsNullOrWhiteSpace(slot.Character)) continue;
            var (sx, sy, sw, sh) = ResolveSlotRect(slot);
            _settings.SaveThumbnailPosition(slot.Character, sx, sy, sw, sh);
            if (applyVis) S.ThumbnailVisibility[slot.Character] = slot.Hidden ? 1 : 0;
            preStaged++;
        }
        if (applyVis) S.CropEnabled = preset.CropsEnabled;
        _settings.Save();
        string shown = StreamerMask.Name(StreamerKind.Layout, preset.Name);
        Alerts.Say(forMonitors
            ? string.Format(LocalizationService.Str("L.Msg.LayoutAppliedForMonitors", "Monitors changed: applied layout '{0}'"), shown)
            : preStaged > 0
            ? string.Format(LocalizationService.Str("L.Msg.LayoutAppliedSaved", "Layout: applied '{0}' ({1} saved for characters not logged in)"), shown, preStaged)
            : string.Format(LocalizationService.Str("L.Msg.LayoutApplied", "Layout: applied '{0}'"), shown));
    }

    /// <summary>ThumbnailManager.ApplyLayoutPresetMapped: slot index → character, from the remap dialog. One undo step.</summary>
    public void ApplyLayoutPresetMapped(LayoutPreset preset, Dictionary<int, string> mapping)
    {
        PushLayoutSnapshot();
        bool applyVis = preset.IncludesVisibility;
        for (int i = 0; i < preset.Slots.Count; i++)
        {
            if (!mapping.TryGetValue(i, out var target) || string.IsNullOrEmpty(target)) continue;
            var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, target, StringComparison.OrdinalIgnoreCase));
            if (thumb != null) ApplyPresetSlot(thumb, preset.Slots[i], applyVis);
        }
        if (applyVis) S.CropEnabled = preset.CropsEnabled;
        _settings.Save();
        Alerts.Say(string.Format(LocalizationService.Str("L.Msg.LayoutAppliedRemapped", "Layout: applied '{0}' (remapped)"), StreamerMask.Name(StreamerKind.Layout, preset.Name)));
    }

    /// <summary>ThumbnailManager.ResolveSlotRect: a slot's fractions of its monitor's work area on this PC (a monitor it does not
    /// have is the primary one), at least 40 × 30, kept on a screen.</summary>
    private (int X, int Y, int W, int H) ResolveSlotRect(LayoutSlot slot)
    {
        var screens = AllScreens;
        var wa = slot.Monitor >= 0 && slot.Monitor < screens.Count ? R(screens[slot.Monitor].WorkingArea) : TargetWorkArea(0);
        int w = Math.Max(40, (int)(slot.Fw * wa.Width)), h = Math.Max(30, (int)(slot.Fh * wa.Height));
        int x = wa.Left + (int)(slot.Fx * wa.Width), y = wa.Top + (int)(slot.Fy * wa.Height);
        (x, y) = EnsureOnScreen(x, y, w, h);
        return (x, y, w, h);
    }

    private void ApplyPresetSlot(ThumbnailWindow thumb, LayoutSlot slot, bool applyVis)
    {
        var (x, y, w, h) = ResolveSlotRect(slot);
        thumb.PlaceAt(x, y, w, h);
        var character = thumb.Client.Character;
        if (string.IsNullOrEmpty(character)) return;
        _settings.SaveThumbnailPosition(character, x, y, w, h);
        if (applyVis) S.ThumbnailVisibility[character] = slot.Hidden ? 1 : 0;   // the tick shows or hides it (Hidden)
    }

    // ── Align, distribute, nudge (IDEAS #36) ──

    /// <summary>ThumbnailManager.ApplyAlign: one undo step, each moved thumbnail's place saved; false when nothing moved.</summary>
    public bool ApplyAlign(AlignOp op, IReadOnlyList<string> names, int dx = 0, int dy = 0)
    {
        var picked = new List<ThumbnailWindow>();
        foreach (var name in names)
        {
            var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, name, StringComparison.OrdinalIgnoreCase));
            if (thumb != null && !picked.Contains(thumb)) picked.Add(thumb);
        }
        if (picked.Count < LayoutAlign.MinItems(op)) return false;
        var before = picked.Select(t => (Name: t.Client.Character, Rect: new Rectangle(t.OwnPosition.X, t.OwnPosition.Y, t.OwnWidth, t.OwnHeight))).ToList();
        var areas = AllScreens.Select(s => R(s.WorkingArea)).ToList();
        var (floorW, floorH) = ThumbnailWindow.FloorOf(S);
        var after = LayoutAlign.Apply(op, before, dx, dy, S.LockResizeAspect, areas, new System.Drawing.Size(floorW, floorH));
        if (after.Select(a => a.Rect).SequenceEqual(before.Select(b => b.Rect))) return false;
        PushLayoutSnapshot();
        for (int i = 0; i < picked.Count; i++)
        {
            var now = after[i].Rect;
            if (now == before[i].Rect) continue;
            picked[i].PlaceAt(now.X, now.Y, now.Width, now.Height);
            _settings.SaveThumbnailPosition(picked[i].Client.Character, now.X, now.Y, now.Width, now.Height);
        }
        _settings.Save();
        return true;
    }

    // ── A layout for the monitors (IDEAS #10) ──

    private MonitorLayoutFollower? _monitorFollower;
    private DispatcherTimer? _monitorSettle;
    private string _lastMonitors = "";

    /// <summary>Asked at the 2 s client look (no timer of its own): the monitors' arrangement changed since the last look, and
    /// the follower (ThumbnailManager.StartMonitorFollowing's) is told, as DisplaySettingsChanged tells it on Windows.</summary>
    private void FollowMonitors()
    {
        if (AnyScreens == null) return;   // nothing to ask yet
        var now = MonitorFingerprint.Of(CurrentMonitorBounds());
        if (_monitorFollower == null)
        {
            _monitorFollower = new MonitorLayoutFollower(() => S.AutoLayoutOnMonitorChange, CurrentMonitorBounds, () => DateTime.UtcNow, ScheduleMonitorSettle,
                fingerprint => new LayoutPresetService(_settings.ConfigDirectory).FindForFingerprint(fingerprint),
                preset =>
                {
                    ApplyLayoutPreset(preset, forMonitors: true);
                    _settings.CurrentProfile.AppliedLayoutPreset = preset.Name;   // App's MonitorLayoutApplied
                    _settings.SaveDelayed();
                });
            _monitorFollower.Start();
            _lastMonitors = now;
            return;
        }
        if (now == _lastMonitors) return;
        _lastMonitors = now;
        _monitorFollower.Changed();
    }

    private void ScheduleMonitorSettle(TimeSpan after)
    {
        if (_monitorSettle == null)
        {
            _monitorSettle = new DispatcherTimer();
            _monitorSettle.Tick += (_, _) =>
            {
                _monitorSettle?.Stop();
                try { _monitorFollower?.Tick(); }
                catch (Exception ex) { ErrorLog.Shared.Record(ex, "Thumbnails (layout for the monitors)", DateTime.Now); }
            };
        }
        _monitorSettle.Stop();
        _monitorSettle.Interval = after;
        _monitorSettle.Start();
    }
}
