using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EveMultiPreview.Interop;

namespace EveMultiPreview.Services;

/// <summary>
/// Captures a periodic snapshot of each tracked EVE window via PrintWindow
/// (PW_RENDERFULLCONTENT, which routes through DWM and works for DirectX
/// swap-chain sources). The cached bitmap is handed back to ThumbnailManager
/// when the source window becomes iconic so the thumbnail can keep showing
/// the last-rendered frame instead of going blank.
///
/// One Bitmap per HWND, replaced in-place. Captured at source dimensions
/// (typically 1920×1080 → ~8 MB per client) — ThumbnailWindow paints scaled
/// to its own size, so callers should dispose old frames when replacing.
/// </summary>
public sealed class FrozenFrameService : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, Bitmap> _lastFrames = new();
    private readonly HashSet<IntPtr> _inFlight = new();
    private readonly object _inFlightLock = new();
    private readonly DispatcherTimer _captureTimer;
    private Func<IntPtr[]>? _hwndProvider;
    private WinEventHookService? _winEvents;
    private bool _disposed;

    public FrozenFrameService()
    {
        // Periodic capture is the safety net — the eager MINIMIZESTART hook is
        // the primary trigger now, so we only need to refresh occasionally for
        // windows that vanish without a minimize event (alt-tab to a covering
        // fullscreen app, virtual-desktop switch, etc.). 5s keeps a recent-ish
        // frame ready while keeping PrintWindow load low.
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _captureTimer.Tick += (_, _) => ScheduleCaptures();
    }

    /// <summary>Start polling. The provider returns the set of currently tracked
    /// EVE HWNDs to snapshot — caller decides which windows are live.
    /// If <paramref name="winEvents"/> is provided, an eager pre-minimize capture
    /// fires on EVENT_SYSTEM_MINIMIZESTART so the cached frame is up-to-the-instant
    /// when the window goes iconic instead of up-to-5-seconds stale.</summary>
    public void Start(Func<IntPtr[]> hwndProvider, WinEventHookService? winEvents = null)
    {
        _hwndProvider = hwndProvider;
        _winEvents = winEvents;
        if (_winEvents != null)
            _winEvents.WindowMinimizeStart += OnMinimizeStart;
        _captureTimer.Start();
    }

    /// <summary>
    /// Capture immediately for an HWND that is about to go iconic. Called
    /// synchronously on the UI thread (the hook delivers there) — PrintWindow
    /// is sync and takes ~50–100ms, which is invisible during a user-initiated
    /// minimize click. Without this, dropping the periodic timer to 5s would
    /// leave us showing a frame up to 5s old when the window minimizes.
    /// </summary>
    private void OnMinimizeStart(IntPtr hwnd)
    {
        if (_disposed || _hwndProvider == null) return;

        // Only act on tracked EVE HWNDs.
        bool tracked = false;
        try
        {
            foreach (var h in _hwndProvider())
            {
                if (h == hwnd) { tracked = true; break; }
            }
        }
        catch { return; }
        if (!tracked) return;

        // The system fires MINIMIZESTART before the window is actually iconic,
        // so PrintWindow still returns the live D3D content here. Bail if a
        // periodic capture is already in flight — avoids a double-capture.
        lock (_inFlightLock)
        {
            if (!_inFlight.Add(hwnd)) return;
        }

        try
        {
            if (User32.IsIconic(hwnd)) return;
            var bmp = CaptureWindow(hwnd);
            if (bmp == null) return;
            // Window may have completed minimization between PrintWindow start
            // and now — discard a black frame in that case.
            if (User32.IsIconic(hwnd)) { bmp.Dispose(); return; }
            if (_lastFrames.TryGetValue(hwnd, out var old)) old.Dispose();
            _lastFrames[hwnd] = bmp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrozenFrame:MinimizeStart] ⚠ Failed for hwnd=0x{hwnd.ToInt64():X}: {ex.Message}");
        }
        finally
        {
            lock (_inFlightLock) _inFlight.Remove(hwnd);
        }
    }

    /// <summary>Fetch the most recent cached frame for an HWND, or null if none
    /// was ever successfully captured. Returned Bitmap is owned by the service —
    /// do not dispose it.</summary>
    public Bitmap? GetLastFrame(IntPtr hwnd)
    {
        return _lastFrames.TryGetValue(hwnd, out var bmp) ? bmp : null;
    }

    /// <summary>Drop the cached frame for an HWND (e.g. when its window closes).</summary>
    public void Forget(IntPtr hwnd)
    {
        if (_lastFrames.TryRemove(hwnd, out var bmp))
            bmp.Dispose();
    }

    private void ScheduleCaptures()
    {
        if (_disposed || _hwndProvider == null) return;

        IntPtr[] hwnds;
        try { hwnds = _hwndProvider(); }
        catch { return; }

        // Drop cached frames for HWNDs no longer tracked.
        var live = new System.Collections.Generic.HashSet<IntPtr>(hwnds);
        foreach (var key in _lastFrames.Keys)
        {
            if (!live.Contains(key)) Forget(key);
        }

        foreach (var hwnd in hwnds)
        {
            // Only capture while the window is NOT iconic — once minimized the
            // D3D swap-chain is torn down and PrintWindow returns black. The
            // frame we want is the last one taken while it was still visible.
            if (User32.IsIconic(hwnd)) continue;

            // De-dup: skip if a capture for this HWND is still in flight. Avoids
            // piling up background work if PrintWindow is slow.
            lock (_inFlightLock)
            {
                if (!_inFlight.Add(hwnd)) continue;
            }

            var targetHwnd = hwnd;
            Task.Run(() =>
            {
                try
                {
                    if (_disposed) return;
                    var bmp = CaptureWindow(targetHwnd);
                    if (_disposed) { bmp?.Dispose(); return; }
                    if (bmp == null) return;

                    // If the window minimized between scheduling and capture completing,
                    // the frame we got is likely black (D3D already torn down) AND the
                    // UI may now be referencing the cached bitmap for its frozen paint.
                    // Drop the new frame to avoid both a bad cache update and a
                    // dispose-while-painting race on the previous bitmap.
                    if (User32.IsIconic(targetHwnd)) { bmp.Dispose(); return; }

                    // Safe to swap + dispose old: while non-iconic the UI is showing
                    // the live DWM surface and does not reference the cached bitmap.
                    if (_lastFrames.TryGetValue(targetHwnd, out var old))
                        old.Dispose();
                    _lastFrames[targetHwnd] = bmp;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FrozenFrame:Capture] ⚠ Failed for hwnd=0x{targetHwnd.ToInt64():X}: {ex.Message}");
                }
                finally
                {
                    lock (_inFlightLock) _inFlight.Remove(targetHwnd);
                }
            });
        }
    }

    private static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out var rect)) return null;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        // Cap capture dimensions to avoid VRAM bloat across many clients.
        // 1280×720 keeps a 4-client fleet under ~15 MB total and is more than
        // enough detail for the scaled-down thumbnail paint.
        const int MaxW = 1280, MaxH = 720;
        double scale = Math.Min(1.0, Math.Min((double)MaxW / w, (double)MaxH / h));
        int captureW = (int)Math.Round(w * scale);
        int captureH = (int)Math.Round(h * scale);

        Bitmap bmp = new Bitmap(captureW, captureH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            if (scale < 1.0)
            {
                // PrintWindow renders at source size, so for downscaling we need
                // to capture full-res first and StretchBlt into the smaller target.
                using var full = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var fg = Graphics.FromImage(full))
                {
                    IntPtr hdc = fg.GetHdc();
                    try
                    {
                        bool ok = User32.PrintWindow(hwnd, hdc, User32.PW_RENDERFULLCONTENT);
                        if (!ok)
                        {
                            fg.ReleaseHdc(hdc);
                            return null;
                        }
                    }
                    finally
                    {
                        try { fg.ReleaseHdc(hdc); } catch { }
                    }
                }
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, captureW, captureH);
            }
            else
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    bool ok = User32.PrintWindow(hwnd, hdc, User32.PW_RENDERFULLCONTENT);
                    if (!ok)
                    {
                        g.ReleaseHdc(hdc);
                        bmp.Dispose();
                        return null;
                    }
                }
                finally
                {
                    try { g.ReleaseHdc(hdc); } catch { }
                }
            }
        }

        return bmp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureTimer.Stop();
        if (_winEvents != null)
            _winEvents.WindowMinimizeStart -= OnMinimizeStart;
        foreach (var (_, bmp) in _lastFrames) bmp.Dispose();
        _lastFrames.Clear();
    }
}
