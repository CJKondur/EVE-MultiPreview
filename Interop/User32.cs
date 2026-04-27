using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace EveMultiPreview.Interop;

/// <summary>
/// P/Invoke wrappers for User32.dll — window enumeration, positioning, hotkeys, etc.
/// </summary>
public static class User32
{
    // ── Window Enumeration ───────────────────────────────────────────

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);



    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    // ── PrintWindow (frozen-frame capture) ────────────────────────────
    // PW_RENDERFULLCONTENT (0x02) uses DWM composition — required for DirectX
    // swap-chain windows (EVE) where the normal WM_PRINT path yields black.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ── Window Positioning ───────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out DwmApi.RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out DwmApi.RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // ── Window Placement (save/restore exact positions) ──────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public DwmApi.RECT rcNormalPosition;
    }



    // ── Extended Window Styles ────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWLP_HWNDPARENT = -8;
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;

    // ── Layered Window ───────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    // ── Window Region ────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    public const int RGN_DIFF = 4; // Subtracts rgn2 from rgn1

    // ── Keyboard State ───────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    // ── SetWindowPos Constants ───────────────────────────────────────

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOZORDER = 0x0004;

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const int SW_MINIMIZE = 6;
    public const int SW_FORCEMINIMIZE = 11;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;

    // ── Global Hotkeys ───────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    // ── SendInput (AHK virtualkey activation trick) ────────────────

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const uint MAPVK_VK_TO_VSC = 0;
    public const uint MAPVK_VSC_TO_VK = 1;

    /// <summary>
    /// AHK virtualkey: 0xE8 — unused virtual key used as activation trigger.
    /// Matches AHK Main_Class.ahk L22: static virtualKey := "vk0xE8"
    /// </summary>
    public const ushort VK_ACTIVATION = 0xE8;

    /// <summary>
    /// Inject a virtual key press + release via SendInput.
    /// Matches AHK: SendInput("{Blind}{vk0xE8}")
    /// This gives the thread legitimate foreground activation rights
    /// because Windows treats it as real keyboard input.
    /// </summary>
    public static void InjectVirtualKey(ushort vk)
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = vk;

        // Key up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = vk;
        inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Explicitly fires WM_KEYDOWN/WM_KEYUP directly into the target window's message queue.
    /// Both Chromium login screens and DirectInput EVE clients can occasionally drop held-key
    /// hardware repeat boundaries during WPF message pump interruptions. Pulsing targeted
    /// PostMessages guarantees the module reliably fires without corrupting the global OS state.
    /// </summary>
    public static HashSet<int> CycleKeysToIgnore = new HashSet<int>();

    // ── Sticky-held-letter tracking (issue #23) ─────────────────────────
    // EVE click-modifier keys (D=dock, Q=approach, W=warp-to, etc.) need to STAY
    // pressed on every client we cycle to until the user physically releases them
    // — pulsing DOWN+UP would tell the new client the key was released the same
    // frame it arrived, defeating the modifier.
    //
    // _heldKeyClients maps each tracked letter VK → the set of HWNDs we've sent
    // a synthetic WM_KEYDOWN to. The poller watches IsKeyDown for each tracked
    // VK; when the user releases the physical key, it blasts WM_KEYUP to every
    // client we'd told it was held, then drops the entry. Letters get sticky
    // treatment; everything else (digits / F-keys / Enter / Space / mouse
    // buttons) keeps the existing one-shot DOWN+UP pulse.
    private static readonly Dictionary<int, HashSet<IntPtr>> _heldKeyClients = new();
    private static readonly object _heldKeyLock = new();
    private static Task? _heldKeyPoller;
    private static IntPtr _lastInjectedHwnd;

    private static void EnsureHeldKeyPoller()
    {
        if (_heldKeyPoller != null && !_heldKeyPoller.IsCompleted) return;

        _heldKeyPoller = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(50);
                List<(int vk, HashSet<IntPtr> hwnds)> released = new();
                lock (_heldKeyLock)
                {
                    foreach (var kv in _heldKeyClients)
                    {
                        if (!IsKeyDown(kv.Key))
                            released.Add((kv.Key, new HashSet<IntPtr>(kv.Value)));
                    }
                    foreach (var r in released) _heldKeyClients.Remove(r.vk);
                    if (_heldKeyClients.Count == 0)
                    {
                        // Send pending UPs outside lock then exit poller
                        var snapshot = released;
                        Task.Run(() => SendKeyUps(snapshot));
                        return;
                    }
                }
                if (released.Count > 0) SendKeyUps(released);
            }
        });

        static void SendKeyUps(List<(int vk, HashSet<IntPtr> hwnds)> released)
        {
            const uint WM_KEYUP_LOCAL = 0x0101;
            foreach (var (vk, hwnds) in released)
            {
                uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParam = (IntPtr)unchecked((int)(0xC0000000 | (scan << 16) | 1));
                foreach (var h in hwnds)
                    PostMessage(h, WM_KEYUP_LOCAL, (IntPtr)vk, lParam);
                EveMultiPreview.Services.DiagnosticsService.LogInjection(
                    $"[HeldKeyPoller] ⤴ Released key 0x{vk:X} on {hwnds.Count} client(s)");
            }
        }
    }

    public static void FixTargetHeldKeys(IntPtr hwnd)
    {
        uint WM_KEYDOWN = 0x0100;
        uint WM_KEYUP = 0x0101;

        void LogInjection(string msg)
        {
            EveMultiPreview.Services.DiagnosticsService.LogInjection(msg);
        }

        List<int> keysToCheck = new List<int>
        {
            0x0D, // Enter
            0x20  // Space
        };

        // 0 to 9 (0x30 - 0x39)
        for (int i = 0x30; i <= 0x39; i++) keysToCheck.Add(i);
        // A to Z (0x41 - 0x5A) — includes navigation keys (D=dock, Q=approach, W=warp, etc.)
        // Re-injected so held actions carry across client switches (matches AHK behavior).
        for (int i = 0x41; i <= 0x5A; i++) keysToCheck.Add(i);
        // F1 to F12 (0x70 - 0x7B)
        for (int i = 0x70; i <= 0x7B; i++) keysToCheck.Add(i);
        // F13 to F24 (0x7C - 0x87)
        for (int i = 0x7C; i <= 0x87; i++) keysToCheck.Add(i);

        List<int> pressedKeys = new List<int>();
        foreach (var vk in keysToCheck)
        {
            if (CycleKeysToIgnore.Contains(vk)) continue;

            if (IsKeyDown(vk))
            {
                pressedKeys.Add(vk);
            }
        }

        // Handle Mouse Hotkeys (MButton, XButton1/Mouse4, XButton2/Mouse5)
        bool hasMouse1 = IsKeyDown(0x04);
        bool hasMouse2 = IsKeyDown(0x05);
        bool hasMouse3 = IsKeyDown(0x06);

        // Get Keyboard Layouts for logging
        uint myThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        IntPtr myHkl = GetKeyboardLayout(myThread);
        IntPtr targetHkl = GetKeyboardLayout(targetThread);

        if (pressedKeys.Count > 0 || hasMouse1 || hasMouse2 || hasMouse3)
        {
            bool hasCtrl = IsKeyDown(0x11);
            bool hasAlt = IsKeyDown(0x12);
            bool hasShift = IsKeyDown(0x10);

            string keyStr = string.Join(",", pressedKeys.Select(k => $"0x{k:X}"));
            LogInjection($"[FixTargetHeldKeys] 🎯 HWND {hwnd} | Keys: {keyStr} | Modifiers: Ctrl:{hasCtrl} Alt:{hasAlt} Shift:{hasShift} | M1:{hasMouse1} M2:{hasMouse2} M3:{hasMouse3} | HKL:0x{myHkl:X8}/0x{targetHkl:X8}");

            POINT pt;
            GetCursorPos(out pt);
            ScreenToClient(hwnd, ref pt);
            IntPtr lParamMouse = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));

            // Split into "sticky" letters (A-Z, EVE click-modifiers) and "one-shot"
            // keys (digits / F-keys / Enter / Space). Letters get DOWN-only and
            // are released by EnsureHeldKeyPoller when the user lifts the key.
            var letterKeys = pressedKeys.Where(vk => vk >= 0x41 && vk <= 0x5A).ToList();
            var pulseKeys  = pressedKeys.Where(vk => vk <  0x41 || vk >  0x5A).ToList();

            // Track sticky letters: add the previously-injected hwnd (so it gets
            // an UP later too — its native held state from when the user first
            // pressed the key never received a UP because it lost focus first)
            // and the new hwnd. Adds are idempotent within a HashSet.
            IntPtr previousHwnd = _lastInjectedHwnd;
            if (letterKeys.Count > 0)
            {
                lock (_heldKeyLock)
                {
                    foreach (var vk in letterKeys)
                    {
                        if (!_heldKeyClients.TryGetValue(vk, out var set))
                        {
                            set = new HashSet<IntPtr>();
                            _heldKeyClients[vk] = set;
                        }
                        if (previousHwnd != IntPtr.Zero && previousHwnd != hwnd)
                            set.Add(previousHwnd);
                        set.Add(hwnd);
                    }
                    EnsureHeldKeyPoller();
                }
                _lastInjectedHwnd = hwnd;
            }

            Task.Run(async () =>
            {
                await Task.Delay(20);

                void FlushModifier(uint vk)
                {
                    if (!IsKeyDown((int)vk))
                    {
                        uint scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
                        IntPtr lParamUpMod = (IntPtr)unchecked((int)(0xC0000000 | (scan << 16) | 1));
                        PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, lParamUpMod);
                    }
                }
                FlushModifier(0x10); // Shift
                FlushModifier(0x11); // Ctrl
                FlushModifier(0x12); // Alt
                FlushModifier(0x5B); // LWin

                // Sticky-letter DOWN — no UP is sent here. The poller fires UP
                // when the user physically releases the key.
                foreach (var vk in letterKeys)
                {
                    uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                    IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1);
                    PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
                }

                // One-shot DOWN
                foreach (var vk in pulseKeys)
                {
                    uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                    IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1);
                    PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
                }

                if (hasMouse1) PostMessage(hwnd, 0x0207, IntPtr.Zero, lParamMouse);
                if (hasMouse2) PostMessage(hwnd, 0x020B, (IntPtr)0x00010000, lParamMouse);
                if (hasMouse3) PostMessage(hwnd, 0x020B, (IntPtr)0x00020000, lParamMouse);

                await Task.Delay(30);

                // One-shot UP only — letters stay down until physical release.
                foreach (var vk in pulseKeys)
                {
                    uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                    IntPtr lParamUp = (IntPtr)unchecked((int)(0xC0000000 | (scanCode << 16) | 1));
                    PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, lParamUp);
                }

                if (hasMouse1) PostMessage(hwnd, 0x0208, IntPtr.Zero, lParamMouse);
                if (hasMouse2) PostMessage(hwnd, 0x020C, (IntPtr)0x00010000, lParamMouse);
                if (hasMouse3) PostMessage(hwnd, 0x020C, (IntPtr)0x00020000, lParamMouse);

                LogInjection($"[FixTargetHeldKeys] ✅ Finished injecting (sticky:{letterKeys.Count}, pulse:{pulseKeys.Count}) for HWND {hwnd}");
            });
        }
    }

    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// The target hwnd for the next vkE8 WM_HOTKEY activation.
    /// Mirrors AHK's This.ActivateHwnd.
    /// </summary>
    public static IntPtr PendingActivateHwnd;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    /// <summary>
    /// Native Window Activation: Focuses the EVE Client locally.
    ///
    /// Activations dispatched from the WH_MOUSE_LL hook callback (XButton/MButton
    /// cycle hotkeys) run in a context where our process holds no foreground-
    /// activation rights — the suppressed click never reached any window, so
    /// Windows considers no process to have "received the last input event".
    /// SetForegroundWindow then silently fails and the EVE client never comes
    /// to front, even though the cycle's internal state and highlight border
    /// have already advanced.
    ///
    /// To recover, we mirror the AHK Main_Class trick: queue the target on
    /// PendingActivateHwnd and synthesize a vk0xE8 keystroke. HotkeyService has
    /// a global RegisterHotKey on that vk; the SendInput delivery fires WM_HOTKEY
    /// in our message thread, which arrives with foreground-activation rights,
    /// and the hotkey handler legally calls SetForegroundWindow on the queued
    /// HWND. The direct SetForegroundWindow call below is kept as a fast path
    /// for cases where we already have rights (WM_HOTKEY dispatch, real click,
    /// our app being foreground); when it succeeds, the WM_HOTKEY-driven path
    /// arrives moments later and is a no-op.
    /// </summary>
    public static void ActivateWindow(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;

        EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateWindow] Standard WIN32 Foreground Shift for HWND {hwnd}");

        PendingActivateHwnd = hwnd;
        InjectVirtualKey(VK_ACTIVATION);

        SetForegroundWindow(hwnd);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }
    
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);



    // ── Process Name Helper ──────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    // ── Message Posting ──────────────────────────────────────────────

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint SC_CLOSE = 0xF060;

    // ── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Get the window title text for a given HWND.
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Get the window class name for a given HWND.
    /// </summary>
    public static string GetWindowClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Get the process name (e.g. "exefile") for the process owning a window.
    /// </summary>
    public static string? GetProcessName(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsEveProcessName(string? procName)
    {
        if (string.IsNullOrEmpty(procName)) return false;
        return procName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAppProcessName(string? procName)
    {
        if (string.IsNullOrEmpty(procName)) return false;
        return procName.Equals("EveMultiPreview", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("EVE MultiPreview", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEveOrAppProcess(string? procName)
    {
        return IsEveProcessName(procName) || IsAppProcessName(procName);
    }

    /// <summary>
    /// Find all visible windows belonging to a specific process name.
    /// </summary>
    public static List<(IntPtr Hwnd, string Title)> FindEveWindows(HashSet<IntPtr>? keepHwnds = null)
    {
        var results = new List<(IntPtr, string)>();
        EnumWindows((hWnd, _) =>
        {
            // Windows Virtual Desktops or EVE fullscreen transitions briefly hide the window.
            // If it's a known tracked HWND, keep yielding it so we don't prematurely destroy the thumbnail.
            bool isVisible = IsWindowVisible(hWnd);
            bool isKept = keepHwnds != null && keepHwnds.Contains(hWnd);
            
            if (!isVisible && !isKept) return true;

            string? procName = GetProcessName(hWnd);
            if (IsEveProcessName(procName))
            {
                string title = GetWindowTitle(hWnd);
                results.Add((hWnd, title));
            }
            return true;
        }, IntPtr.Zero);
        return results;
    }



    /// <summary>
    /// TOS guard — returns true if ANY non-modifier key is physically held down,
    /// excluding the triggering hotkey's own VK. Scans all VK codes 0x08–0xFE.
    /// Mirrors AHK _IsGameKeyHeld for input-broadcast prevention.
    /// </summary>
    public static bool IsGameKeyHeld(uint? excludeVk = null)
    {
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            // Skip the triggering hotkey's own VK
            if (excludeVk.HasValue && vk == (int)excludeVk.Value) continue;
            // Skip modifier keys (VK_SHIFT, VK_CONTROL, VK_MENU)
            if (vk >= 0x10 && vk <= 0x12) continue;
            // Skip VK_LWIN, VK_RWIN
            if (vk == 0x5B || vk == 0x5C) continue;
            // Skip VK_LSHIFT..VK_RMENU
            if (vk >= 0xA0 && vk <= 0xA5) continue;
            // Skip mouse buttons (VK_LBUTTON..VK_XBUTTON2)
            if (vk >= 0x01 && vk <= 0x06) continue;

            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if a key is currently held down (hardware-level).
    /// </summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>
    /// Create a border region (outer rect minus inner rect) for a window.
    /// Returns IntPtr.Zero on failure.
    /// </summary>
    public static IntPtr CreateBorderRegion(int width, int height, int borderThickness)
    {
        var outer = CreateRectRgn(0, 0, width, height);
        var inner = CreateRectRgn(borderThickness, borderThickness,
            width - borderThickness, height - borderThickness);
        CombineRgn(outer, outer, inner, RGN_DIFF);
        DeleteObject(inner);
        return outer;
    }

    // ── Low-Level Mouse & Keyboard Hooks ─────────────────────────────
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int XBUTTON1 = 0x0001;
    public const int XBUTTON2 = 0x0002;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public System.Drawing.Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Extract high word (used for XButton number from mouseData).</summary>
    public static int HIWORD(uint dword) => (int)((dword >> 16) & 0xFFFF);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    // ── SetWinEventHook (foreground / minimize / window lifecycle) ────────
    // Used by WinEventHookService to replace per-tick polling with event-driven
    // wake-ups. WINEVENT_OUTOFCONTEXT requires the installing thread to have a
    // message pump; the callback fires on that same thread (UI thread for WPF).

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_OBJECT_CREATE       = 0x8000;
    public const uint EVENT_OBJECT_DESTROY      = 0x8001;
    public const uint EVENT_OBJECT_NAMECHANGE   = 0x800C;
    public const uint EVENT_SYSTEM_FOREGROUND   = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND   = 0x0017;

    public const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;
}
