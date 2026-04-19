using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Interop;

namespace EveMultiPreview.Services;

/// <summary>
/// Represents a discovered EVE Online client window.
/// </summary>
public record EveWindow(IntPtr Hwnd, string Title, string CharacterName);

/// <summary>
/// Polls for EVE Online windows and fires events when clients appear or disappear.
/// Uses a background task with proper cancellation — no timer race conditions.
/// Reduced poll interval to 150ms for faster window detection (AHK uses 50ms).
/// Debug logging with [Discovery:*] tags.
/// </summary>
public sealed class WindowDiscoveryService : IDisposable
{
    private const int PollIntervalMs = 100; // Fast detection — EnumWindows is cheap at this rate

    private readonly ConcurrentDictionary<IntPtr, EveWindow> _windows = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>Fires when a new EVE window is discovered.</summary>
    public event Action<EveWindow>? WindowFound;

    /// <summary>Fires when an EVE window is closed or lost.</summary>
    public event Action<EveWindow>? WindowLost;

    /// <summary>Fires when an EVE window title changes (character login/logout).</summary>
    public event Action<EveWindow, string>? WindowTitleChanged;

    /// <summary>All currently tracked EVE windows.</summary>
    public IReadOnlyDictionary<IntPtr, EveWindow> Windows => _windows;

    /// <summary>
    /// Start polling for EVE windows on a background thread.
    /// </summary>
    public void Start()
    {
        if (_pollTask != null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
        Debug.WriteLine($"[Discovery:Poll] 🔧 Started window discovery (interval={PollIntervalMs}ms)");
    }

    /// <summary>
    /// Stop polling and clean up.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _pollTask?.Wait(TimeSpan.FromSeconds(2));
        _pollTask = null;
        _cts?.Dispose();
        _cts = null;
        Debug.WriteLine("[Discovery:Poll] 🛑 Window discovery stopped");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery:Poll] ❌ Poll error: {ex.Message}");
            }

            try
            {
                await Task.Delay(PollIntervalMs, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        // Find all current EVE windows (including those temporarily hidden if we already track them)
        var knownHwnds = new HashSet<IntPtr>(_windows.Keys);
        var currentWindows = Interop.User32.FindEveWindows(knownHwnds);
        var currentHwnds = new HashSet<IntPtr>(currentWindows.Select(w => w.Hwnd));

        // Check for new windows or title changes
        foreach (var (hwnd, title) in currentWindows)
        {
            string className = User32.GetWindowClassName(hwnd);
            // Filter out non-game windows spawned by exefile (tooltips, context menus, CEF panels).
            // The PID dedup in ThumbnailManager is the primary guard against duplicates —
            // this filter catches the obvious non-game windows.
            if (className.Contains("tooltip", StringComparison.OrdinalIgnoreCase) ||
                className == "#32768")
                continue;

            string charName = ExtractCharacterName(title);

            // Double guard against "Close", "Minimize", etc tooltips leaking through if class name was missed
            if (string.Equals(title, "Close", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(title, "Minimize", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(title, "Maximize", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_windows.TryGetValue(hwnd, out var existing))
            {
                // Window exists — check if title changed
                if (existing.Title != title)
                {
                    var updated = new EveWindow(hwnd, title, charName);
                    _windows[hwnd] = updated;
                    Debug.WriteLine($"[Discovery:TitleChanged] 🔄 '{existing.CharacterName}' → '{charName}' (hwnd=0x{hwnd:X})");
                    WindowTitleChanged?.Invoke(updated, existing.Title);
                }
            }
            else
            {
                // New window
                var eveWindow = new EveWindow(hwnd, title, charName);
                _windows[hwnd] = eveWindow;
                Debug.WriteLine($"[Discovery:Found] ✅ New EVE window: '{charName}' (hwnd=0x{hwnd:X}, title='{title}')");
                WindowFound?.Invoke(eveWindow);
            }
        }

        // Check for removed windows
        foreach (var (hwnd, window) in _windows)
        {
            if (!currentHwnds.Contains(hwnd))
            {
                if (_windows.TryRemove(hwnd, out var removed))
                {
                    Debug.WriteLine($"[Discovery:Lost] ❌ EVE window lost: '{removed.CharacterName}' (hwnd=0x{hwnd:X})");
                    WindowLost?.Invoke(removed);
                }
            }
        }
    }

    /// <summary>
    /// Extract character name from EVE window title.
    /// EVE titles are: "EVE - CharacterName" or just "EVE" (character select).
    /// </summary>
    private static string ExtractCharacterName(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title == "EVE")
            return string.Empty;

        // "EVE - CharacterName" → "CharacterName"
        const string prefix = "EVE - ";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? title[prefix.Length..].Trim()
            : title;
    }

    public void Dispose()
    {
        Stop();
    }
}
