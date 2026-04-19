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

    public CropManager(WindowDiscoveryService discovery, SettingsService settings)
    {
        _discovery = discovery;
        _settings = settings;
        _discovery.WindowFound += OnWindowFound;
        _discovery.WindowLost += OnWindowLost;
    }

    /// <summary>Optional: let crop popups snap against live primary thumbnails.
    /// Must be called before any CropWindow is created (i.e. before Refresh()).</summary>
    public void AttachThumbnailManager(ThumbnailManager thumbnailManager)
        => _thumbnailManager = thumbnailManager;

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
        });
    }

    public void Dispose()
    {
        _discovery.WindowFound -= OnWindowFound;
        _discovery.WindowLost -= OnWindowLost;
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
                ApplyVisualSettings(existing);
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

        // Always-on-top parity with ThumbnailWindow (also propagates to label overlay)
        win.SetTopmost(s.ShowThumbnailsAlwaysOnTop);

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
