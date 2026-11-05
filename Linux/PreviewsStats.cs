using Avalonia;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>S7: the stats overlay (ThumbnailManager.CreateStatWindowsForCharacter / DestroyStatWindowsForCharacter /
/// UpdateStatWindows / SaveStatWindowPositions and the stats part of its focus sweep: keep in sync). One StatsWindow per
/// running character with numbers to show, while Tools › Stats overlay is on. Made, closed, styled and given its text
/// once a second (Windows' 1 s stat timer, here every tenth tick); shown or hidden at every tick by booleans only.</summary>
public sealed partial class Previews
{
    private readonly Dictionary<string, StatsWindow> _statWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App's SetCsvLogging and CountNpcDamage, at start and after every change (the same values again do nothing,
    /// and only the first call deletes old files).</summary>
    private void FollowStatsLogging()
    {
        if (_alerts?.Stats is not { } tracker) return;
        tracker.SetCsvLogging(S.StatLoggingEnabled, S.StatLogDirectory, S.StatLogRetentionDays);
        tracker.CountNpcDamage = !S.StatsExcludeNpcDamage;   // IDEAS P32
    }

    /// <summary>The numbers a character's window shows (CharacterStatSettings.Resolve over the numbers for every character).</summary>
    private StatMetrics StatsOf(string character)
    {
        S.PerCharacterStats.TryGetValue(character, out var own);
        return CharacterStatSettings.Resolve(S.GlobalStatMetrics, own);
    }

    /// <summary>Once a second: a window for each running character with numbers to show (none while the overlay is off),
    /// closed for one that left or has no numbers any more (its place kept), and the look and lines of every window.</summary>
    private void SyncStats()
    {
        FollowStatsLogging();
        if (_alerts?.Stats is not { } tracker) return;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (S.StatOverlayEnabled)
            foreach (var thumb in _thumbs.Values)
                if (thumb.Client.Character.Length > 0 && (StatsOf(thumb.Client.Character) & StatMetrics.AllMetrics) != 0) wanted.Add(thumb.Client.Character);
        foreach (var gone in _statWindows.Keys.Where(k => !wanted.Contains(k)).ToList()) CloseStatWindow(gone);
        foreach (var character in wanted.Where(c => !_statWindows.ContainsKey(c))) OpenStatWindow(character);
        string waiting = LocalizationService.Str("L.Stats.Waiting", "Waiting for data...");
        foreach (var (character, window) in _statWindows)
        {
            window.SetLook(S.StatOverlayFontSize, S.StatOverlayOpacity, S.StatOverlayBgColor, S.StatOverlayTextColor);
            window.ShowName();
            string text;
            try { text = tracker.GetOverlayText(character, StatsOf(character)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { text = ""; }   // Windows' catch: this window keeps its old lines
            window.SetText(string.IsNullOrEmpty(text) ? waiting : text);
        }
    }

    /// <summary>At every tick: a stats window shows while some client runs, the player is in EVE or MultiPreview (or the
    /// settings window is open) when "Hide when you switch to another app" is on, and nothing hides all or the main
    /// thumbnails or holds its character for focus mode. Topmost and locked as the thumbnails are.</summary>
    private void FollowStatWindows(bool eveFocused)
    {
        if (_statWindows.Count == 0) return;
        bool visibleByFocus = !S.HideStatsOnLostFocus || eveFocused || _settingsWindow != null;
        bool any = _thumbs.Count > 0 && visibleByFocus && !_allHidden && !_primaryHidden;
        foreach (var (character, window) in _statWindows)
        {
            bool shown = any && !FocusMode.Holds(_focusHeld, character);
            if (shown != window.IsVisible) { if (shown) window.Show(); else window.Hide(); }
            if (window.Topmost != ThumbnailsTopmost) window.Topmost = ThumbnailsTopmost;
            window.CanDrag = !S.LockPositions;
        }
    }

    private void OpenStatWindow(string character)
    {
        // Saved under the plain name (AHK); older C# files used "<name>_Stats".
        var positions = S.StatWindowPositions;
        bool known = positions.TryGetValue(character, out var saved) || positions.TryGetValue(character + "_Stats", out saved);
        int x = known ? (int)saved!.X : StatsWindow.DefaultX, y = known ? (int)saved!.Y : StatsWindow.DefaultY;
        int w = known && saved!.Width > 0 && saved.Height > 0 ? (int)saved.Width : StatsWindow.DefaultWidth;
        int h = known && saved!.Width > 0 && saved.Height > 0 ? (int)saved.Height : StatsWindow.DefaultHeight;
        var window = new StatsWindow(character, new PixelRect(x, y, w, h))
        {
            Topmost = ThumbnailsTopmost,
            CanDrag = !S.LockPositions,
            PointerSource = _clients.Pointer,
        };
        window.SetLook(S.StatOverlayFontSize, S.StatOverlayOpacity, S.StatOverlayBgColor, S.StatOverlayTextColor);
        window.PositionAndSizeChanged += w =>
        {
            KeepStatPlace(w);
            _settings.SaveDelayed();
        };
        window.SnapPosition = (sx, sy, sw, sh) => S.ThumbnailSnap
            ? StatsWindow.SnapCorner(sx, sy, sw, sh,
                _statWindows.Values.Where(o => o != window).Select(o => o.Rect)
                    .Concat(_thumbs.Values.Select(t => new PixelRect(t.OwnPosition, new PixelSize(t.OwnWidth, t.OwnHeight)))),
                S.ThumbnailSnapDistance)
            : null;
        window.ResizeAll += (source, rw, rh) =>
        {
            foreach (var other in _statWindows.Values)
                if (other != source) other.SetSize(rw, rh);
        };
        window.Opened += (_, _) => { if (window.TryGetPlatformHandle() is { } handle) _clients.NoFocus((uint)(long)handle.Handle); };
        _statWindows[character] = window;
        // Shown by the next FollowStatWindows (not here, so a window that should be hidden never flashes up).
    }

    private void CloseStatWindow(string character)
    {
        if (!_statWindows.Remove(character, out var window)) return;
        KeepStatPlace(window);
        window.Close();
    }

    /// <summary>The window's place and size under the plain character name; the old "_Stats" entry goes.</summary>
    private void KeepStatPlace(StatsWindow window)
    {
        var r = window.Rect;
        S.StatWindowPositions[window.Character] = new ThumbnailRect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
        S.StatWindowPositions.Remove(window.Character + "_Stats");
    }

    /// <summary>On exit (ThumbnailManager.SaveStatWindowPositions): every window's place is kept and the window closed.</summary>
    private void CloseStatWindows()
    {
        if (_statWindows.Count == 0) return;
        foreach (var character in _statWindows.Keys.ToList()) CloseStatWindow(character);
        _settings.SaveDelayed();   // pending, so the exit's SettingsService.Dispose writes the places (a resize-all follower's too)
    }
}
