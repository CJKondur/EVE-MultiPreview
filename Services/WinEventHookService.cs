using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using EveMultiPreview.Interop;

using Application = System.Windows.Application;

namespace EveMultiPreview.Services;

/// <summary>
/// Single owner of SetWinEventHook subscriptions. Replaces per-tick polling in
/// WindowDiscoveryService / ThumbnailManager / FrozenFrameService with
/// event-driven wake-ups, so those services can run their heavy sweeps on a
/// much slower cadence and still respond instantly to focus changes, minimize
/// transitions, and EVE windows opening/closing.
///
/// Hooks must be installed on a thread with a message pump (the WPF UI thread)
/// because we use WINEVENT_OUTOFCONTEXT — the system delivers callbacks on the
/// installing thread. That means subscribers receive events on the UI thread.
/// Keep handlers cheap or marshal heavy work via Dispatcher.BeginInvoke.
/// </summary>
public sealed class WinEventHookService : IDisposable
{
    public event Action<IntPtr>? ForegroundChanged;
    public event Action<IntPtr>? WindowMinimizeStart;
    public event Action<IntPtr>? WindowMinimizeEnd;
    public event Action<IntPtr>? WindowCreated;
    public event Action<IntPtr>? WindowDestroyed;
    public event Action<IntPtr>? WindowNameChanged;

    // Hold the delegates as fields so the GC doesn't collect them while the
    // OS retains a callback pointer. Collection here = CallbackOnCollectedDelegate.
    private User32.WinEventDelegate? _systemCallback;
    private User32.WinEventDelegate? _objectCallback;
    // One hook per event (or per adjacent pair) we use. A range hook delivers every event inside it: the old
    // CREATE..NAMECHANGE range included EVENT_OBJECT_LOCATIONCHANGE, which Windows raises for the cursor on every
    // mouse move anywhere, so each move was posted to this (the UI) thread only to be dropped (3TEARS, 2026-11-06).
    private readonly List<IntPtr> _hooks = new();
    private bool _disposed;

    /// <summary>
    /// Install the hooks. MUST be called on the WPF UI thread — the callbacks
    /// are delivered on the thread that called this method, and that thread
    /// must have a message pump.
    /// </summary>
    public void Start()
    {
        if (_hooks.Count > 0) return;

        _systemCallback = OnSystemEvent;
        _objectCallback = OnObjectEvent;

        // System events: foreground change, and minimize start/end (adjacent, 0x0016..0x0017).
        // Object events: window create/destroy (adjacent, 0x8000..0x8001) and name (title) change. We filter
        // hard inside the callback because EVENT_OBJECT_NAMECHANGE in particular
        // fires VERY often for sub-controls across the entire desktop.
        foreach (var (first, last, callback) in new[]
        {
            (User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, _systemCallback!),
            (User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND, _systemCallback!),
            (User32.EVENT_OBJECT_CREATE, User32.EVENT_OBJECT_DESTROY, _objectCallback!),
            (User32.EVENT_OBJECT_NAMECHANGE, User32.EVENT_OBJECT_NAMECHANGE, _objectCallback!),
        })
        {
            var hook = User32.SetWinEventHook(first, last, IntPtr.Zero, callback, 0, 0,
                User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
            if (hook == IntPtr.Zero) Debug.WriteLine($"[WinEventHook:Start] ⚠ SetWinEventHook failed for 0x{first:X}..0x{last:X}");
            else _hooks.Add(hook);
        }
        Debug.WriteLine($"[WinEventHook:Start] 🔧 {_hooks.Count} hooks installed");
    }

    private void OnSystemEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // System events for sub-objects (menus, focus reticles) come through
        // with non-zero idObject — drop those. We only care about top-level
        // window transitions.
        if (idObject != User32.OBJID_WINDOW || hwnd == IntPtr.Zero) return;

        try
        {
            switch (eventType)
            {
                case User32.EVENT_SYSTEM_FOREGROUND:
                    ForegroundChanged?.Invoke(hwnd);
                    break;
                case User32.EVENT_SYSTEM_MINIMIZESTART:
                    WindowMinimizeStart?.Invoke(hwnd);
                    break;
                case User32.EVENT_SYSTEM_MINIMIZEEND:
                    WindowMinimizeEnd?.Invoke(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let a subscriber exception escape into the OS hook chain.
            Debug.WriteLine($"[WinEventHook:System] ❌ {eventType:X}: {ex.Message}");
        }
    }

    private void OnObjectEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // Object events fire for every accessibility-named control in the
        // system. We only want top-level windows (idObject == 0, idChild == 0).
        if (idObject != User32.OBJID_WINDOW || idChild != User32.CHILDID_SELF) return;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            switch (eventType)
            {
                case User32.EVENT_OBJECT_CREATE:
                    WindowCreated?.Invoke(hwnd);
                    break;
                case User32.EVENT_OBJECT_DESTROY:
                    WindowDestroyed?.Invoke(hwnd);
                    break;
                case User32.EVENT_OBJECT_NAMECHANGE:
                    WindowNameChanged?.Invoke(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinEventHook:Object] ❌ {eventType:X}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unhook MUST happen on the same thread that called SetWinEventHook.
        // If we're not on the UI thread (shutdown path), marshal back.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { dispatcher.Invoke(UnhookAll); }
            catch { UnhookAll(); }
        }
        else
        {
            UnhookAll();
        }
    }

    private void UnhookAll()
    {
        foreach (var hook in _hooks) User32.UnhookWinEvent(hook);
        _hooks.Clear();
        _systemCallback = null;
        _objectCallback = null;
        Debug.WriteLine("[WinEventHook:Dispose] 🛑 Hooks released");
    }
}
