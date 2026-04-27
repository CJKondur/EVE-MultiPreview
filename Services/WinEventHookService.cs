using System;
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
    private IntPtr _systemHook;
    private IntPtr _objectHook;
    private bool _disposed;

    /// <summary>
    /// Install the hooks. MUST be called on the WPF UI thread — the callbacks
    /// are delivered on the thread that called this method, and that thread
    /// must have a message pump.
    /// </summary>
    public void Start()
    {
        if (_systemHook != IntPtr.Zero || _objectHook != IntPtr.Zero) return;

        _systemCallback = OnSystemEvent;
        _objectCallback = OnObjectEvent;

        // System events: foreground change + minimize start/end. Range covers
        // 0x0003..0x0017 in one hook so the OS does the dispatching for us.
        _systemHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND,
            User32.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _systemCallback, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        // Object events: window create/destroy + name (title) change. We filter
        // hard inside the callback because EVENT_OBJECT_NAMECHANGE in particular
        // fires VERY often for sub-controls across the entire desktop.
        _objectHook = User32.SetWinEventHook(
            User32.EVENT_OBJECT_CREATE,
            User32.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _objectCallback, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        if (_systemHook == IntPtr.Zero || _objectHook == IntPtr.Zero)
            Debug.WriteLine($"[WinEventHook:Start] ⚠ SetWinEventHook failed (system=0x{_systemHook:X}, object=0x{_objectHook:X})");
        else
            Debug.WriteLine("[WinEventHook:Start] 🔧 Hooks installed (system + object)");
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
        if (_systemHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_systemHook);
            _systemHook = IntPtr.Zero;
        }
        if (_objectHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_objectHook);
            _objectHook = IntPtr.Zero;
        }
        _systemCallback = null;
        _objectCallback = null;
        Debug.WriteLine("[WinEventHook:Dispose] 🛑 Hooks released");
    }
}
