using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Views;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.Services;

/// <summary>
/// Lifecycle manager for CropWindow popups. Mirrors ThumbnailManager:
///   - Listens to WindowDiscoveryService for EVE clients appearing/disappearing
///   - Spawns a CropWindow for each <see cref="CropDefinition"/> defined for that character
///     when <see cref="AppSettings.CropEnabled"/> is true
///   - Destroys popups when the client disappears or the master toggle is turned off
///   - Persists popup bounds back into the settings profile on drag/resize
/// </summary>
public sealed class CropManager : IDisposable
{
    private readonly WindowDiscoveryService _discovery;
    private readonly SettingsService _settings;
    private ThumbnailManager? _thumbnailManager;

    // character name -> (crop id -> popup)
    private readonly ConcurrentDictionary<string, Dictionary<string, CropWindow>> _windows
        = new(StringComparer.OrdinalIgnoreCase);

    // character name -> live EVE HWND
    private readonly ConcurrentDictionary<string, IntPtr> _liveHwnds
        = new(StringComparer.OrdinalIgnoreCase);

    // Periodic self-heal for crops whose DWM thumbnail goes stale with no window
    // event to trigger a rebind (issue #64 — crops vanish at random).
    private readonly System.Windows.Threading.DispatcherTimer _healthTimer;

    // Trailing debounce for the crop z-order re-raise. The foreground WinEvent fires
    // once PER client activation, so holding the cycle hotkey (a switch every
    // CycleDelayMs) used to post one ApplyZOrder per step at Normal dispatcher
    // priority — flooding the UI queue and STARVING the cycle's own Background-priority
    // repeat timer, so the configured cycle delay was ignored and the lag grew the
    // longer you held (the cycle regression). Coalescing collapses a whole cycle-storm
    // into ONE re-raise once switching settles; crops are already topmost (raw
    // SetWindowPos in CropWindow) so they don't fall behind during the brief coalesce.
    private readonly System.Windows.Threading.DispatcherTimer _zorderDebounce;

    // Window-event hook used to force-rebind a crop when its source EVE window
    // restores from minimized (issue #65 — periodic health check missed this
    // case because the registration was still nominally valid).
    private WinEventHookService? _winEvents;

    public CropManager(WindowDiscoveryService discovery, SettingsService settings)
    {
        _discovery = discovery;
        _settings = settings;
        _discovery.WindowFound += OnWindowFound;
        _discovery.WindowLost += OnWindowLost;
        _discovery.WindowTitleChanged += OnWindowTitleChanged;

        _healthTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _healthTimer.Tick += (_, _) => HealthCheck();
        _healthTimer.Start();

        _zorderDebounce = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _zorderDebounce.Tick += (_, _) => { _zorderDebounce.Stop(); ApplyZOrder(); };
    }

    /// <summary>Re-validate every live crop's DWM thumbnail and rebuild any that
    /// went stale, so a randomly-blanked crop recovers without the user toggling
    /// crops off/on (issue #64). Also (re)creates crops that were never spawned
    /// for a live character (issue #80).</summary>
    private void HealthCheck()
    {
        // Skip while crops are hidden (issue #66) — healing would needlessly
        // re-register thumbnails for windows the user has toggled off.
        var s = _settings.Settings;
        if (!s.CropEnabled || _cropsHidden) return;

        foreach (var perChar in _windows.Values)
        {
            foreach (var win in perChar.Values)
            {
                try { win.EnsureThumbnailHealthy(); }
                catch (Exception ex) { Debug.WriteLine($"[CropManager] Health check failed: {ex.Message}"); }
            }
        }

        // Self-heal MISSING crops (issue #80 — "crops not showing on login").
        // The per-character reconcile is reactive: it fires once per discovery
        // event (OnWindowFound / OnWindowTitleChanged) and is gated on settings
        // state at that instant. If a character's login event raced settings
        // load — or was otherwise missed — its crops were never created and the
        // only recovery was the user manually toggling crops off/on (which runs
        // Refresh = reconcile-all). Reconcile here any live character whose live
        // popups have drifted from its crop definitions — missing, extra, or
        // swapped-but-same-count ids — so they correct on their own within one
        // health-check cycle. No-op (and cheap) once the popups match the defs.
        foreach (var (charName, hwnd) in _liveHwnds)
        {
            if (!s.Crops.TryGetValue(charName, out var defs) || defs.Count == 0) continue;
            _windows.TryGetValue(charName, out var existing);
            if (!CropsInSync(existing, defs))
            {
                try { ReconcileCharacter(charName, hwnd); }
                catch (Exception ex) { Debug.WriteLine($"[CropManager] Crop self-heal reconcile failed for '{charName}': {ex.Message}"); }
            }
        }
    }

    /// <summary>True when a character's live crop popups exactly match its current
    /// definitions (same set of crop ids). Any drift — missing, extra, or
    /// swapped-but-same-count ids — returns false so the health check reconciles
    /// it (issue #80 self-heal; the id check catches a defs swap a count compare
    /// would miss).</summary>
    private static bool CropsInSync(Dictionary<string, CropWindow>? existing, List<CropDefinition> defs)
    {
        if ((existing?.Count ?? 0) != defs.Count) return false;
        if (existing == null) return false; // unreachable when defs.Count > 0; satisfies null-flow
        foreach (var def in defs)
            if (!existing.ContainsKey(def.Id)) return false;
        return true;
    }

    // True while crops are hidden by the Hide/Show All keybind or the dedicated
    // Hide/Show Crops keybind (issue #66). Newly spawned crops respect this.
    private bool _cropsHidden;

    /// <summary>Optional: let crop popups snap against live primary thumbnails, and
    /// follow the Hide/Show All keybind + the dedicated Hide/Show Crops keybind
    /// (issue #66). Must be called before any CropWindow is created.</summary>
    public void AttachThumbnailManager(ThumbnailManager thumbnailManager)
    {
        _thumbnailManager = thumbnailManager;
        _thumbnailManager.AllVisibilityChanged += SetCropsHidden;
        _thumbnailManager.CropsVisibilityToggleRequested += ToggleCropsVisibility;
        _thumbnailManager.CropZOrderReassertRequested += OnThumbnailZOrderReassert;
    }

    /// <summary>Re-raise crops in lockstep with the thumbnails' own z-order sweep.
    /// Driven by <see cref="ThumbnailManager.CropZOrderReassertRequested"/>, which fires
    /// from the 250ms focus poll on every client switch — so crops get the identical,
    /// reliable treatment that keeps thumbnails above the clients, instead of a separate
    /// edge-triggered re-raise that lost the race and let crops drop behind (#80/#87).</summary>
    private void OnThumbnailZOrderReassert(bool desiredTopmost)
    {
        var s = _settings.Settings;
        if (!s.CropEnabled || _cropsHidden) return;

        foreach (var perChar in _windows.Values)
            foreach (var win in perChar.Values)
            {
                try
                {
                    if (win.IsTopmostState != desiredTopmost) win.SetTopmost(desiredTopmost);
                    win.BringToFront();
                }
                catch (Exception ex) { Debug.WriteLine($"[CropManager] Crop sweep re-raise failed: {ex.Message}"); }
            }
    }

    /// <summary>Show or hide every live crop. Driven by the Hide/Show All keybind
    /// (crops follow the thumbnails' hidden state) — issue #66.</summary>
    public void SetCropsHidden(bool hidden)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_cropsHidden == hidden) return;
            _cropsHidden = hidden;
            ApplyHiddenToAll();
        });
    }

    /// <summary>Flip crop visibility. Driven by the dedicated Hide/Show Crops
    /// keybind (issue #66).</summary>
    public void ToggleCropsVisibility()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _cropsHidden = !_cropsHidden;
            ApplyHiddenToAll();
            _thumbnailManager?.ShowTooltipFeedback(_cropsHidden ? "Crops: Hidden" : "Crops: Visible");
        });
    }

    /// <summary>Recovery net (issue #81): force crops back to visible, clearing any
    /// hidden state. The dedicated Hide/Show Crops keybind is a silent sticky toggle,
    /// so a single stray press (e.g. a hotkey collision during heavy client cycling)
    /// could leave crops stuck hidden with no automatic way back. Called from safe
    /// anchors — opening Settings and switching profile — so the user always has a
    /// reliable, no-guesswork way to get them back without the disable/enable dance.</summary>
    public void ShowCrops()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_cropsHidden) return;
            _cropsHidden = false;
            ApplyHiddenToAll();
        });
    }

    private void ApplyHiddenToAll()
    {
        foreach (var perChar in _windows.Values)
            foreach (var win in perChar.Values)
            {
                try { win.SetHidden(_cropsHidden); }
                catch (Exception ex) { Debug.WriteLine($"[CropManager] SetHidden failed: {ex.Message}"); }
            }
    }

    /// <summary>Wire the WinEvent hook so we can force-rebind a crop's DWM
    /// thumbnail the moment its source EVE window restores from minimized —
    /// the disappearance trigger reported in issue #65 that the 4s periodic
    /// health check misses because the stale registration still passes
    /// DwmUpdateThumbnailProperties.</summary>
    public void AttachWinEvents(WinEventHookService winEvents)
    {
        if (_winEvents != null) return;
        _winEvents = winEvents;
        _winEvents.WindowMinimizeEnd += OnSourceMinimizeEnd;
        _winEvents.ForegroundChanged += OnForegroundChanged;
    }

    /// <summary>Re-assert crop z-order when the foreground window changes. WPF Topmost
    /// alone doesn't keep a crop above a client the user tabs into — the activated
    /// client raises over the crop and stays there until the user toggles crops off/on
    /// (issue #80: "crops hide under the main client the moment you tab in").
    ///
    /// This mirrors ThumbnailManager's focus-aware topmost model exactly, because a
    /// bare non-topmost <see cref="CropWindow.BringToFront"/> (HWND_TOP) LOSES the
    /// activation race — the re-activating EVE client gets stuck back on top (the same
    /// failure the thumbnails hit, "confirmed by LittlePhish"). The winning move is to
    /// put the crop into the TOPMOST band while an EVE client (or this app) is focused,
    /// then BringToFront within that band. Under KeepThumbnailsAboveClients we DROP the
    /// crop back to non-topmost the instant a non-EVE app is focused, so crops never
    /// sit over Discord/your browser once you tab there; Always-On-Top keeps them
    /// topmost permanently.</summary>
    private void OnForegroundChanged(IntPtr fgHwnd)
    {
        // Coalesce: restart the debounce instead of re-raising immediately. A burst of
        // switches (held cycle key) collapses to a single ApplyZOrder once it settles,
        // so we never flood the dispatcher and stall the cycle timer.
        _zorderDebounce.Stop();
        _zorderDebounce.Start();
    }

    /// <summary>Converge crop z-order to the current focus state: topmost while an
    /// EVE client (or this app) is foreground — or always, under Always-On-Top — then
    /// re-raise within that band; drop to non-topmost when the user tabs to another app
    /// (KeepThumbnailsAboveClients only). Also driven by the thumbnail sweep hook.</summary>
    private void ApplyZOrder()
    {
        var s = _settings.Settings;
        if (!s.CropEnabled || _cropsHidden) return;

        IntPtr fg = Interop.User32.GetForegroundWindow();
        string? fgProc = null;
        try { fgProc = Interop.User32.GetProcessName(fg); } catch { }
        bool eveFocused = Interop.User32.IsEveOrAppProcess(fgProc);

        // Re-raise crops above the EVE client whenever a client (or this app) is
        // foreground — NOT gated on Always-On-Top. The thumbnails' client-switch
        // re-raise is deliberately un-gated too (ThumbnailManager), so a user who
        // keeps previews non-topmost still gets them lifted over the client on a
        // switch; crops used to early-return here when neither on-top mode was set,
        // so for those users crops never re-raised at all while thumbnails did — the
        // "thumbnails fine, crops still hide" report (#80/#87). Topmost while EVE/app
        // is foreground (or always, under Always-On-Top); non-topmost otherwise, set
        // BEFORE BringToFront so it uses the right z-order band.
        bool desiredTopmost = s.ShowThumbnailsAlwaysOnTop || (s.KeepThumbnailsAboveClients && eveFocused);

        foreach (var perChar in _windows.Values)
            foreach (var win in perChar.Values)
            {
                try
                {
                    if (win.IsTopmostState != desiredTopmost) win.SetTopmost(desiredTopmost);
                    // Only re-raise when an EVE/app window is up — raising on a
                    // tab-away would lift the crop over the app the user just chose.
                    if (eveFocused) win.BringToFront();
                }
                catch (Exception ex) { Debug.WriteLine($"[CropManager] Crop re-raise failed: {ex.Message}"); }
            }

        // Diagnostic ONLY when DWM debug logging is enabled (issue #80/#87). It reads
        // WS_EX_TOPMOST off the foreground window and every crop and appends to
        // debug_dwm.log — a GetWindowLong-per-crop plus a synchronous disk write that
        // fired on EVERY foreground change and made client-switching feel laggy. Behind
        // the flag it costs nothing in normal use; testers who enable it still get the
        // fgTopmost / cropsTopmost readout that pinned this bug down.
        if (DiagnosticsService.GlobalSettings?.EnableDebugLogging_DWM == true)
            LogCropZOrder(fg, fgProc, eveFocused, desiredTopmost);
    }

    private void LogCropZOrder(IntPtr fg, string? fgProc, bool eveFocused, bool desiredTopmost)
    {
        bool fgTop = fg != IntPtr.Zero && (Interop.User32.GetWindowLong(fg, Interop.User32.GWL_EXSTYLE) & Interop.User32.WS_EX_TOPMOST) != 0;
        int cropCount = 0, cropTop = 0;
        foreach (var perChar in _windows.Values)
            foreach (var win in perChar.Values)
            {
                cropCount++;
                var h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (h != IntPtr.Zero && (Interop.User32.GetWindowLong(h, Interop.User32.GWL_EXSTYLE) & Interop.User32.WS_EX_TOPMOST) != 0)
                    cropTop++;
            }
        if (cropCount > 0)
            DiagnosticsService.LogDwm($"[Crop:ZOrder] fg=0x{fg.ToInt64():X} proc={fgProc ?? "?"} eveFocused={eveFocused} fgTopmost={fgTop} desiredTopmost={desiredTopmost} crops={cropCount} cropsTopmost={cropTop}");
    }

    private void OnSourceMinimizeEnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.Settings.CropEnabled || _cropsHidden) return;
            foreach (var perChar in _windows.Values)
            {
                foreach (var win in perChar.Values)
                {
                    if (win.EveHwnd != hwnd) continue;
                    try { win.ForceRebind(); }
                    catch (Exception ex) { Debug.WriteLine($"[CropManager] ForceRebind on minimize-end failed: {ex.Message}"); }
                }
            }
        });
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>Re-sync popups against current settings. Call after the user edits crops.</summary>
    public void Refresh()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var s = _settings.Settings;

            if (!s.CropEnabled)
            {
                CloseAllInternal();
                return;
            }

            // For every live character, reconcile its crop popups with its definitions.
            foreach (var (charName, hwnd) in _liveHwnds)
                ReconcileCharacter(charName, hwnd);

            // Close popups for characters that no longer have definitions.
            foreach (var charName in _windows.Keys.ToList())
            {
                if (!s.Crops.TryGetValue(charName, out var defs) || defs.Count == 0)
                    CloseCharacter(charName);
            }
        });
    }

    /// <summary>Push updated <see cref="CropDefinition"/> values to any live popup for that crop.</summary>
    public void ApplyDefinitionEdits(string characterName, string cropId)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_windows.TryGetValue(characterName, out var perChar)) return;
            if (!perChar.TryGetValue(cropId, out var win)) return;

            var def = FindDefinition(characterName, cropId);
            if (def == null)
            {
                win.Close();
                perChar.Remove(cropId);
                return;
            }

            // Pull latest bounds from the definition onto the live window (in case the
            // user changed numeric fields), then refresh DWM source rect + label.
            win.Left = def.PopupX;
            win.Top = def.PopupY;
            win.Width = Math.Max(40, def.PopupWidth);
            win.Height = Math.Max(30, def.PopupHeight);
            win.ApplyLabel();
            win.UpdateThumbnailDestination();
            win.ApplyClickThrough();
        });
    }

    public void Dispose()
    {
        _healthTimer.Stop();
        _zorderDebounce.Stop();
        _discovery.WindowFound -= OnWindowFound;
        _discovery.WindowLost -= OnWindowLost;
        _discovery.WindowTitleChanged -= OnWindowTitleChanged;
        if (_thumbnailManager != null)
        {
            _thumbnailManager.AllVisibilityChanged -= SetCropsHidden;
            _thumbnailManager.CropsVisibilityToggleRequested -= ToggleCropsVisibility;
            _thumbnailManager.CropZOrderReassertRequested -= OnThumbnailZOrderReassert;
        }
        if (_winEvents != null)
        {
            _winEvents.WindowMinimizeEnd -= OnSourceMinimizeEnd;
            _winEvents.ForegroundChanged -= OnForegroundChanged;
            _winEvents = null;
        }
        CloseAllInternal();
    }

    // ── Discovery events ────────────────────────────────────────────

    private void OnWindowFound(EveWindow window)
    {
        if (string.IsNullOrEmpty(window.CharacterName)) return;
        _liveHwnds[window.CharacterName] = window.Hwnd;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.Settings.CropEnabled) return;
            ReconcileCharacter(window.CharacterName, window.Hwnd);
        });
    }

    private void OnWindowLost(EveWindow window)
    {
        if (string.IsNullOrEmpty(window.CharacterName)) return;
        _liveHwnds.TryRemove(window.CharacterName, out _);

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CloseCharacter(window.CharacterName);
        });
    }

    /// <summary>
    /// Title changes happen when an EVE client transitions from the character-
    /// select screen ("EVE") to a logged-in character ("EVE - CharName"), or
    /// when the player switches characters within the same client window.
    ///
    /// Without this handler, crop popups never spawned on app relaunch when
    /// the user happened to launch the EVE clients while MultiPreview was
    /// already running: WindowFound fires at the char-select screen with an
    /// empty CharacterName so the early-return at the top of OnWindowFound
    /// skips the binding, and the subsequent rename never reached us.
    /// </summary>
    private void OnWindowTitleChanged(EveWindow window, string oldTitle)
    {
        // Same EVE window may have shown a different character before. Evict
        // any stale entries that pointed to this hwnd under another name —
        // their crops no longer apply because the window now hosts a
        // different (or no) character.
        foreach (var kv in _liveHwnds.ToArray())
        {
            if (kv.Value == window.Hwnd
                && !string.Equals(kv.Key, window.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                _liveHwnds.TryRemove(kv.Key, out _);
                var staleChar = kv.Key;
                Application.Current?.Dispatcher.BeginInvoke(() => CloseCharacter(staleChar));
            }
        }

        if (string.IsNullOrEmpty(window.CharacterName)) return;
        _liveHwnds[window.CharacterName] = window.Hwnd;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.Settings.CropEnabled) return;
            ReconcileCharacter(window.CharacterName, window.Hwnd);
        });
    }

    // ── Reconciliation ──────────────────────────────────────────────

    private void ReconcileCharacter(string charName, IntPtr hwnd)
    {
        var s = _settings.Settings;
        if (!s.Crops.TryGetValue(charName, out var defs) || defs.Count == 0)
        {
            CloseCharacter(charName);
            return;
        }

        if (!_windows.TryGetValue(charName, out var perChar))
        {
            perChar = new Dictionary<string, CropWindow>(StringComparer.Ordinal);
            _windows[charName] = perChar;
        }

        // Ensure one popup per definition
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in defs)
        {
            seen.Add(def.Id);
            if (perChar.TryGetValue(def.Id, out var existing))
            {
                existing.Rebind(hwnd);
                existing.ApplyLabel();
                existing.UpdateThumbnailDestination();
                existing.ApplyClickThrough();
                ApplyVisualSettings(existing);
                existing.SetHidden(_cropsHidden);
                continue;
            }

            var win = new CropWindow();
            win.Initialize(hwnd, charName, def);
            win.BoundsChanged += OnBoundsChanged;
            win.SnapPosition = (x, y, w, h) => ResolveSnap(win, x, y, w, h);
            try { win.Show(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CropManager] Show failed for '{charName}' / '{def.Name}': {ex.Message}");
                continue;
            }
            ApplyVisualSettings(win);
            // A crop spawned while crops are toggled off must start hidden (issue #66).
            if (_cropsHidden) win.SetHidden(true);
            perChar[def.Id] = win;
        }

        // Remove popups whose definition was deleted
        foreach (var id in perChar.Keys.ToList())
        {
            if (!seen.Contains(id))
            {
                var win = perChar[id];
                perChar.Remove(id);
                win.BoundsChanged -= OnBoundsChanged;
                try { win.Close(); } catch { }
            }
        }
    }

    private void CloseCharacter(string charName)
    {
        if (!_windows.TryRemove(charName, out var perChar)) return;
        foreach (var win in perChar.Values)
        {
            win.BoundsChanged -= OnBoundsChanged;
            try { win.Close(); } catch { }
        }
    }

    private void CloseAllInternal()
    {
        foreach (var charName in _windows.Keys.ToList())
            CloseCharacter(charName);
    }

    private void OnBoundsChanged(CropWindow win)
    {
        // The window already mutated Definition in-place. Persist.
        _settings.Save();
    }

    /// <summary>
    /// Corner-to-corner snap resolver. Mirrors ThumbnailManager's stat-window
    /// snap: tries every pair of corners between the dragged window and each
    /// target, picks the closest pair within ThumbnailSnapDistance pixels.
    /// Targets = every other crop popup + every live primary thumbnail.
    /// </summary>
    private (double x, double y) ResolveSnap(CropWindow self, double x, double y, double w, double h)
    {
        var s = _settings.Settings;
        if (!s.ThumbnailSnap) return (x, y);
        int snapRange = Math.Max(1, s.ThumbnailSnapDistance);

        var myCorners = new[] { (x, y), (x + w, y), (x, y + h), (x + w, y + h) };
        bool[] isRight  = { false, true,  false, true  };
        bool[] isBottom = { false, false, true,  true  };

        double bestDist = snapRange + 1;
        double destX = x, destY = y;
        bool shouldMove = false;

        var targets = new List<(double L, double T, double W, double H)>();
        foreach (var perChar in _windows.Values)
        {
            foreach (var other in perChar.Values)
            {
                if (ReferenceEquals(other, self)) continue;
                targets.Add((other.Left, other.Top, other.Width, other.Height));
            }
        }
        if (_thumbnailManager != null)
            targets.AddRange(_thumbnailManager.GetThumbnailBounds());

        foreach (var (oL, oT, oW, oH) in targets)
        {
            var targetCorners = new[] { (oL, oT), (oL + oW, oT), (oL, oT + oH), (oL + oW, oT + oH) };
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double dx = myCorners[i].Item1 - targetCorners[j].Item1;
                    double dy = myCorners[i].Item2 - targetCorners[j].Item2;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= snapRange && dist < bestDist)
                    {
                        bestDist = dist;
                        shouldMove = true;
                        destX = targetCorners[j].Item1 - (isRight[i] ? w : 0);
                        destY = targetCorners[j].Item2 - (isBottom[i] ? h : 0);
                    }
                }
            }
        }

        return shouldMove ? (destX, destY) : (x, y);
    }

    /// <summary>
    /// Propagate the user's always-on-top preference and label text style
    /// (font / size / color) from AppSettings onto a CropWindow.
    /// </summary>
    private void ApplyVisualSettings(CropWindow win)
    {
        var s = _settings.Settings;

        // Always-on-top parity with ThumbnailWindow (also propagates to label overlay).
        // Be focus-aware at creation: under KeepThumbnailsAboveClients a crop created
        // while an EVE client is already foreground gets NO subsequent ForegroundChanged
        // event, so a bare SetTopmost(false) would leave it buried behind the client
        // until the user toggled crops off/on (issue #80's startup case). Start it in
        // the topmost band when EVE/app is the current foreground.
        bool eveFocusedNow = false;
        try { eveFocusedNow = Interop.User32.IsEveOrAppProcess(Interop.User32.GetProcessName(Interop.User32.GetForegroundWindow())); }
        catch { }
        win.SetTopmost(s.ShowThumbnailsAlwaysOnTop || (s.KeepThumbnailsAboveClients && eveFocusedNow));

        // Raise it above the client NOW if EVE/app is already foreground. A crop
        // reconciled after you've tabbed to EVE — e.g. the slow ones still loading
        // during a profile switch — gets no ForegroundChanged event of its own, so
        // without this it stays flagged topmost yet sitting behind the client until
        // the next focus change (issue #87: "crops fail to load if you tab to EVE too
        // quick"). Same root cause as #80, just hit mid-load rather than on tab-in.
        if (eveFocusedNow) win.BringToFront();

        // Parse font size from string setting (matches ThumbnailTextSize being string)
        double fontSize = 11;
        if (!string.IsNullOrWhiteSpace(s.ThumbnailTextSize) &&
            double.TryParse(s.ThumbnailTextSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            fontSize = parsed;
        }

        var color = AppSettings.ParseColor(s.ThumbnailTextColor, Color.FromRgb(0xFA, 0xC5, 0x7A));
        win.SetLabelStyle(s.ThumbnailTextFont ?? "Segoe UI", fontSize, color);
    }

    private CropDefinition? FindDefinition(string charName, string cropId)
    {
        if (!_settings.Settings.Crops.TryGetValue(charName, out var list)) return null;
        return list.FirstOrDefault(c => string.Equals(c.Id, cropId, StringComparison.Ordinal));
    }
}
