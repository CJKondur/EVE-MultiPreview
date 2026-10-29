using System;
using System.Collections.Concurrent;
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
    public static extern bool IsWindow(IntPtr hWnd);

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

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>When Windows last saw keyboard or mouse input, on Environment.TickCount's clock (both are the low 32 bits
    /// of the same millisecond counter), or null when it won't say (IDEAS #41).</summary>
    public static int? LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? unchecked((int)info.dwTime) : null;
    }

    // -- Can this process's low-level hooks see input aimed at a window? (IDEAS #41) --
    // Windows doesn't call a normal process's hook for input aimed at an elevated window, so a silent hook there is
    // expected, not dropped. The hook watchdog asks before it judges anything.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int MediumIntegrity = 0x2000;

    /// <summary>The process's integrity level (0x2000 medium, 0x3000 high, ...), or null when Windows won't say.</summary>
    private static int? ProcessIntegrity(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        var label = Marshal.AllocHGlobal(64);   // TOKEN_MANDATORY_LABEL: one SID_AND_ATTRIBUTES plus its one-RID SID
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, label, 64, out _)) return null;
            var sid = Marshal.ReadIntPtr(label);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count > 0 ? Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            CloseHandle(token);
        }
    }

    private static readonly Lazy<int> SelfIntegrity = new(() => ProcessIntegrity(GetCurrentProcess()) ?? MediumIntegrity);

    /// <summary>True when this process's low-level hooks and injected keys reach the window: it exists, and its
    /// process's integrity level is not above this one's. That is the comparison UIPI makes, not "elevated": a program
    /// can run at high integrity with an unelevated token (TeamSpeak 3 did, 2026-10-29). When Windows won't say (no
    /// access to the owner), false: the caller must not read a silent hook as a dropped one.</summary>
    public static bool HooksSeeInputFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try { return ProcessIntegrity(process) is int other && other <= SelfIntegrity.Value; }
        finally { CloseHandle(process); }
    }

    /// <summary>A key is being hidden after a switch right now (its release is still to be swallowed). The hook
    /// watchdog never removes the keyboard hook then: that would strand the key down in the client (IDEAS #41).</summary>
    public static bool HasHiddenHeldKeys => !_hiddenHeldKeys.IsEmpty;

    /// <summary>The time (Environment.TickCount's clock) of the message being handled: for a WM_HOTKEY, when the
    /// system posted it, so a handler that ran late still knows when the key was pressed (IDEAS #41).</summary>
    [DllImport("user32.dll")]
    public static extern int GetMessageTime();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

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
    /// <summary>Title bar (WS_BORDER | WS_DLGFRAME). EVE "Window mode" has it; "Fixed Window" doesn't.</summary>
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
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

    public const int SM_SWAPBUTTON = 23;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // Left-handed users swap the mouse buttons in Windows, which swaps their
    // MEANING: the physical right button then sends WM_LBUTTONDOWN. Mouse
    // MESSAGES honour that swap, but GetAsyncKeyState does NOT - VK_LBUTTON is
    // always the physical left button. A drag opened by a message and then
    // polled by the raw VK therefore reads "not held" on its first tick and
    // dies instantly, which is why left-handed users could not drag a window
    // with either button. Poll these wherever a mouse MESSAGE began the
    // interaction. Read live, never cached: users toggle the setting freely.
    public static int VkPrimaryButton =>
        GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;

    public static int VkSecondaryButton =>
        GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_LBUTTON : VK_RBUTTON;

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
    /// <summary>Post the request to the target window's thread instead of blocking on it.
    /// SetWindowPos on ANOTHER PROCESS's window is synchronous by default — it waits for
    /// that window's thread to pump messages. An EVE client busy rendering can take a
    /// while, and a client switch makes several such calls, which is what produced the
    /// 0.5-1s switch delay reported on issue #100.</summary>
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const int SW_MINIMIZE = 6;
    public const int SW_FORCEMINIMIZE = 11;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const uint WPF_RESTORETOMAXIMIZED = 0x0002;

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

    /// <summary>The Win32 INPUT union. MOUSEINPUT is the LARGEST member, so it must be
    /// declared even though we only ever send keyboard events: the union's size sets
    /// sizeof(INPUT), and SendInput validates the cbSize we pass against its own.
    /// Declaring only KEYBDINPUT gives 32 bytes on x64 where Windows expects 40, and
    /// EVERY SendInput call then fails with ERROR_INVALID_PARAMETER (87) - silently,
    /// because the return value was discarded. That made the device-state half of the
    /// held-key hybrid dead code from the day it was written (#92, #108).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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
            // Each iteration is wrapped so a transient failure (e.g. OOM on
            // HashSet alloc, or a future change introducing a throwable call)
            // can't silently fault the task and leave keys logically pressed
            // forever in the tracked EVE clients. Previously a single fault
            // killed the poller; the next FixTargetHeldKeys would spawn a
            // fresh one, but in the gap any new key DOWNs would target a
            // stale snapshot. Keep the poller alive, log, and continue.
            while (true)
            {
                try
                {
                    await Task.Delay(50);
                    List<(int vk, HashSet<IntPtr> hwnds)> released = new();
                    bool exitAfterFinalUps = false;
                    lock (_heldKeyLock)
                    {
                        foreach (var kv in _heldKeyClients)
                        {
                            if (!IsKeyDown(kv.Key))
                                released.Add((kv.Key, new HashSet<IntPtr>(kv.Value)));
                        }
                        foreach (var r in released) _heldKeyClients.Remove(r.vk);
                        if (_heldKeyClients.Count == 0)
                            exitAfterFinalUps = true;
                    }
                    // Send UPs synchronously so the WM_KEYUP messages are
                    // posted before the poller's Task completes. The previous
                    // implementation spawned Task.Run for this and returned
                    // immediately; if the process exited microseconds later,
                    // the spawned task could be aborted and the UP would
                    // never reach EVE — leaving the held letter "pressed."
                    // PostMessage is async at the OS level so this stays fast
                    // (~150us total for a typical small fleet).
                    if (released.Count > 0) SendKeyUps(released);
                    if (exitAfterFinalUps) return;
                }
                catch (Exception ex)
                {
                    EveMultiPreview.Services.DiagnosticsService.LogInjection(
                        $"[HeldKeyPoller] ❌ Iteration faulted, continuing: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        static void SendKeyUps(List<(int vk, HashSet<IntPtr> hwnds)> released)
        {
            const uint WM_KEYUP_LOCAL = 0x0101;
            foreach (var (vk, hwnds) in released)
            {
                // Mirror the hybrid DOWN (#92): PostMessage WM_KEYUP to each client
                // we told the key was down (message-queue readers), AND a global
                // SendInput UP (device state). The physical key is already up by the
                // time the poller sees the release, so the global UP is a harmless
                // clean-up; the per-client UPs release the message-queue held state.
                uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParam = (IntPtr)unchecked((int)(0xC0000000 | (scan << 16) | 1));
                foreach (var h in hwnds)
                    PostMessage(h, WM_KEYUP_LOCAL, (IntPtr)vk, lParam);
                SendInputScan(vk, keyUp: true);
                EveMultiPreview.Services.DiagnosticsService.LogInjection(
                    $"[HeldKeyPoller] ⤴ UP key 0x{vk:X} → PostMessage {hwnds.Count} client(s) + global SendInput");
            }
        }
    }

    /// <summary>
    /// The keys currently held that would be broadcast/propagated to a client on a
    /// switch (the same set <see cref="FixTargetHeldKeys"/> injects), as friendly
    /// labels for the broadcast-key HUD — modifiers first, then keys, then mouse.
    /// Returns empty when nothing broadcastable is held (modifiers alone don't count,
    /// matching the injection gate). Pure read of live key state; injects nothing.
    /// </summary>
    public static List<string> GetHeldBroadcastKeys()
    {
        var keys = new List<string>();
        // Carrying held keys is off (#112): nothing would be broadcast, so the HUD stays idle.
        if (EveMultiPreview.Services.DiagnosticsService.GlobalSettings?.PropagateHeldKeys != true) return keys;
        void AddIf(int vk, string name)
        {
            if (CycleKeysToIgnore.Contains(vk)) return;
            if (IsKeyDown(vk)) keys.Add(name);
        }

        AddIf(0x0D, "Enter");
        AddIf(0x20, "Space");
        for (int i = 0x30; i <= 0x39; i++) AddIf(i, ((char)i).ToString());        // 0-9
        for (int i = 0x41; i <= 0x5A; i++) AddIf(i, ((char)i).ToString());        // A-Z
        for (int i = 0x70; i <= 0x87; i++) AddIf(i, "F" + (i - 0x70 + 1));        // F1-F24
        if (IsKeyDown(0x04)) keys.Add("MMB");
        if (IsKeyDown(0x05)) keys.Add("Mouse4");
        if (IsKeyDown(0x06)) keys.Add("Mouse5");

        if (keys.Count == 0) return keys; // nothing broadcastable held → HUD idle

        var combo = new List<string>();
        if (IsKeyDown(0x11)) combo.Add("Ctrl");
        if (IsKeyDown(0x12)) combo.Add("Alt");
        if (IsKeyDown(0x10)) combo.Add("Shift");
        combo.AddRange(keys);
        return combo;
    }

    /// <summary>
    /// Inject a GLOBAL keyboard event via SendInput using the key's scancode.
    /// Unlike PostMessage(WM_KEYDOWN), which only reaches a window's message
    /// queue, SendInput drives the OS device/raw-input layer that DirectInput
    /// reads — which is how EVE tracks held action keys (#92). SendInput is
    /// untargeted: it lands on whatever window is foreground when it fires, so
    /// callers MUST confirm the intended client holds foreground first.
    /// Scancode-based (KEYEVENTF_SCANCODE) so it survives keyboard-layout
    /// differences between clients. Sticky letters (A–Z) are non-extended.
    /// </summary>
    /// <summary>Inject a scancode key event. Returns the number of events actually
    /// inserted - 0 means the OS REFUSED it, and the reason matters: if the foreground
    /// window belongs to a process running at a higher integrity level (EVE started as
    /// administrator while we are not), UIPI blocks injected input into it and returns
    /// ERROR_ACCESS_DENIED. That failure is invisible unless this value is checked, which
    /// is how a "the log says we sent it" false trail happened on #108.</summary>
    private static uint SendInputScan(int vk, bool keyUp)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wScan = scan;
        inputs[0].U.ki.dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>The ordinary (non-modifier) keys a switch carries or hides: Enter, Space,
    /// 0-9, A-Z (EVE's click keys: A align, S warp, D dock, Q approach…) and F1-F24.</summary>
    private static readonly int[] OrdinaryHeldKeys =
        new[] { 0x0D, 0x20 }
        .Concat(Enumerable.Range(0x30, 10))    // 0-9
        .Concat(Enumerable.Range(0x41, 26))    // A-Z
        .Concat(Enumerable.Range(0x70, 24))    // F1-F24
        .ToArray();

    // ── Hide still-held keys from the client you switch to (#115) ──────
    // An EVE key still down when you switch is seen held by the client you land on,
    // and its release then counts there as a tap: holding A to align on one client
    // and switching made the NEXT client align to whatever its overview had selected.
    // This releases the key in the client you LEAVE and swallows its leftover
    // auto-repeats and its real release, so the new client never sees it at all.
    private static readonly ConcurrentDictionary<uint, byte> _hiddenHeldKeys = new();

    /// <summary>Set by HotkeyService while its keyboard hook is running with the option on:
    /// without the hook the leftover repeats and release would reach the new client anyway.</summary>
    public static volatile bool HeldKeyHidingReady;

    /// <summary>The keys a switch hides (Plan 41: pure, so a test can hold keys without pressing them): none unless
    /// <paramref name="ready"/> (HideHeldKeysOnSwitch on, with the keyboard hook running), else every ordinary key that is
    /// held and is not a switching key.</summary>
    public static List<int> HeldKeysToHide(bool ready, Func<int, bool> isDown, ICollection<int> ignore) =>
        ready ? OrdinaryHeldKeys.Where(vk => !ignore.Contains(vk) && isDown(vk)).ToList() : new List<int>();

    /// <summary>Call BEFORE activating the next client.</summary>
    public static void HideHeldKeysBeforeSwitch()
    {
        var toHide = HeldKeysToHide(HeldKeyHidingReady, IsKeyDown, CycleKeysToIgnore);
        if (toHide.Count == 0) return;   // the option is off (or nothing is held): nothing is sent
        var released = new List<int>();
        foreach (var vk in toHide)
        {
            _hiddenHeldKeys[(uint)vk] = 0;   // first, so no real repeat slips in between
            if (SendInputScan(vk, keyUp: true) == 0)
            {
                // Refused (e.g. EVE elevated): the key is still logically down, and
                // swallowing its real release would leave it stuck down system-wide.
                _hiddenHeldKeys.TryRemove((uint)vk, out _);
                continue;
            }
            released.Add(vk);
        }
        if (released.Count == 0) return;
        // The injected release must be delivered while the OLD client is still in front,
        // or the new one receives it - the very tap this exists to prevent. The async key
        // state flips once Windows has routed it, so wait for that (normally < 1 ms).
        for (int i = 0; i < 30 && released.Any(IsKeyDown); i++)
            System.Threading.Thread.Sleep(1);
        EveMultiPreview.Services.DiagnosticsService.LogInjection(
            $"[HideHeldKeys] released before switch: {string.Join(",", released.Select(k => $"0x{k:X}"))}");
    }

    /// <summary>Keyboard-hook side: true = swallow this REAL keystroke. A hidden key's
    /// repeats are swallowed until its real release, which is swallowed too and ends it.
    /// If that release never arrives (taken by other software), the next real press and
    /// release of the key are swallowed once and the entry clears itself.</summary>
    public static bool ConsumeHiddenHeldKey(uint vk, bool keyUp)
    {
        if (!_hiddenHeldKeys.ContainsKey(vk)) return false;
        if (keyUp) _hiddenHeldKeys.TryRemove(vk, out _);
        return true;
    }

    /// <summary>What one switch carries: the held ordinary keys and mouse buttons (only with <c>carryKeys</c>,
    /// PropagateHeldKeys) and the held Shift, Ctrl and Alt (only with <c>carryModifiers</c>, PropagateHeldModifiers).</summary>
    public sealed record HeldCarryPlan(List<int> Keys, List<int> Modifiers, bool Mouse1, bool Mouse2, bool Mouse3)
    {
        /// <summary>Anything to carry at all; false = FixTargetHeldKeys sends nothing.</summary>
        public bool Any => Keys.Count > 0 || Modifiers.Count > 0 || Mouse1 || Mouse2 || Mouse3;
    }

    /// <summary>The carry of one switch, decided from the two options and the key state (Plan 41: pure, so a test can hold
    /// keys without pressing them). A switching key (<paramref name="ignore"/>) is never carried.</summary>
    public static HeldCarryPlan PlanHeldCarry(bool carryKeys, bool carryModifiers, Func<int, bool> isDown, ICollection<int> ignore)
    {
        // Opt-in since 2.3.42 (#112): off = letters, digits, F-keys, Enter, Space and
        // mouse buttons are left alone on a switch.
        var keys = carryKeys ? OrdinaryHeldKeys.Where(vk => !ignore.Contains(vk) && isDown(vk)).ToList() : new List<int>();

        // Opt-in since 2.3.42 (#108): on some setups the client already sees the
        // physically-held modifier and these synthetic events do not help - and the
        // re-press briefly releases it, which may be worse than doing nothing. Off =
        // pre-2.3.30 behaviour: leave a held modifier entirely alone.
        var modifiers = carryModifiers
            ? new[] { 0x10, 0x11, 0x12 }.Where(mod => !ignore.Contains(mod) && isDown(mod)).ToList()   // Shift, Ctrl, Alt
            : new List<int>();

        // Mouse hotkeys (MButton, XButton1/Mouse4, XButton2/Mouse5) go with the other keys.
        return new HeldCarryPlan(keys, modifiers, carryKeys && isDown(0x04), carryKeys && isDown(0x05), carryKeys && isDown(0x06));
    }

    /// <summary>Looks up to <paramref name="attempts"/> times, <paramref name="tick"/> apart, whether <paramref name="hwnd"/>
    /// is the window in front; true as soon as it is, false when it never was.</summary>
    public static async Task<bool> WaitUntilForeground(IntPtr hwnd, Func<IntPtr> foreground, Func<Task> tick, int attempts = 15)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (foreground() == hwnd) return true;
            await tick();
        }
        return false;
    }

    /// <summary>Presses each of <paramref name="keys"/> that is STILL down, asking <paramref name="isDown"/> immediately
    /// before each press. The carry waits for the foreground before it presses, and a key the user released in that time
    /// must not be pressed down again: nothing would ever send its release.</summary>
    public static void PressStickyKeys(IEnumerable<int> keys, Func<int, bool> isDown, Action<int> press)
    {
        foreach (var vk in keys)
        {
            if (!isDown(vk)) continue;
            press(vk);
        }
    }

    /// <summary>Runs every pulse's <c>Down</c>, waits <paramref name="hold"/>, then runs the <c>Up</c> of every pulse whose
    /// down was started - in a finally, so a throw anywhere in between (a failing down, a failing hold) cannot leave a
    /// pulse key or mouse button down in the client. The exception still reaches the caller.</summary>
    public static async Task HoldPulse(IEnumerable<(Action Down, Action Up)> pulses, Func<Task> hold)
    {
        var ups = new List<Action>();
        try
        {
            foreach (var (down, up) in pulses)
            {
                ups.Add(up);   // before the down: a down that throws half way still gets its up
                down();
            }
            await hold();
        }
        finally
        {
            foreach (var up in ups) up();
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

        // Re-injected so held actions carry across client switches (matches AHK behavior).
        var settings = EveMultiPreview.Services.DiagnosticsService.GlobalSettings;
        var plan = PlanHeldCarry(settings?.PropagateHeldKeys == true, settings?.PropagateHeldModifiers == true, IsKeyDown, CycleKeysToIgnore);
        if (!plan.Any) return;   // both options off (the default), or nothing held: nothing is sent

        List<int> pressedKeys = plan.Keys;

        // Held MODIFIERS (Ctrl / Shift / Alt) must cross a client switch too:
        // Ctrl+click is EVE's lock-target. They are deliberately NOT in
        // keysToCheck because they need different treatment from both buckets:
        // not a one-shot pulse (that releases the modifier the frame it lands)
        // and not the letters' hybrid PostMessage+SendInput. The user is
        // physically holding the key, so global/DirectInput state is ALREADY
        // correct - only the incoming client's per-window key state is stale.
        // PostMessage alone fixes that; adding SendInput would mutate global
        // modifier state for no gain and risk a system-wide stuck Ctrl.
        List<int> heldModifiers = plan.Modifiers;

        // Handle Mouse Hotkeys (MButton, XButton1/Mouse4, XButton2/Mouse5)
        bool hasMouse1 = plan.Mouse1;
        bool hasMouse2 = plan.Mouse2;
        bool hasMouse3 = plan.Mouse3;

        // Get Keyboard Layouts for logging
        uint myThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        IntPtr myHkl = GetKeyboardLayout(myThread);
        IntPtr targetHkl = GetKeyboardLayout(targetThread);

        if (pressedKeys.Count > 0 || heldModifiers.Count > 0 || hasMouse1 || hasMouse2 || hasMouse3)
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
            if (letterKeys.Count > 0 || heldModifiers.Count > 0)
            {
                lock (_heldKeyLock)
                {
                    foreach (var vk in letterKeys.Concat(heldModifiers))
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
                // Plan 41: the carry starts once the target is the window in front, as it did before Plan 35. The switch
                // then made the target foreground before this method was called (ActivateWindow's synchronous tiers);
                // since 36e8cfd it is the vk0xE8 bridge, whose WM_HOTKEY runs SetForegroundWindow a few ms after
                // ActivateWindow returns. So the carry waits for that (the same 150 ms budget as the device half below),
                // then keeps its old 20 ms. A client already in front, or restored from minimized, passes at once or when
                // its restore lands.
                await WaitUntilForeground(hwnd, GetForegroundWindow, () => Task.Delay(10));
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

                // Modifier DOWN, message half. Sent before the sticky/pulse keys so a
                // Ctrl+<key> combination arrives in the right order. No UP here; the
                // poller fires it on physical release.
                int modPosted = 0, modPostFailed = 0, modPostErr = 0;
                foreach (var vk in heldModifiers)
                {
                    uint modScan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                    IntPtr lParamModDown = (IntPtr)((modScan << 16) | 1);
                    if (PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamModDown)) modPosted++;
                    else { modPostFailed++; modPostErr = Marshal.GetLastWin32Error(); }
                }
                if (heldModifiers.Count > 0)
                    LogInjection(modPostFailed == 0
                        ? $"[FixTargetHeldKeys] ⌨ PostMessage DOWN {modPosted} modifier(s) → HWND {hwnd}"
                        : $"[FixTargetHeldKeys] ❌ PostMessage DOWN REFUSED for {modPostFailed}/{heldModifiers.Count} modifier(s) → HWND {hwnd} (err={modPostErr}{(modPostErr == 5 ? " ACCESS_DENIED - the client is running at a higher integrity level than we are, e.g. EVE started as administrator" : "")})");

                // Sticky-key DOWN — HYBRID injection (#92). Two EVE input readers
                // need two different things, so we do both:
                //   1) PostMessage(WM_KEYDOWN) — a discrete press MESSAGE to the
                //      specific client. Edge-triggered actions (e.g. F = engage
                //      drones) fire on this. Worked pre-2.3.14; the SendInput-only
                //      approach removed it and regressed those actions, because a
                //      physically-held key is already down so a bare SendInput
                //      down produces no new press edge.
                //   2) SendInput scancode DOWN — drives the GLOBAL device state
                //      (DirectInput) for level-held click-modifiers (e.g. D = dock)
                //      on setups PostMessage alone didn't reach. Untargeted → only
                //      fired once our client actually holds foreground.
                // No UP here; the poller fires both UPs on physical release.
                // PressStickyKeys looks at the key state right before each press: a letter released during the waits
                // above is not pressed down again (here, and again before the device half below).
                if (letterKeys.Count > 0)
                {
                    PressStickyKeys(letterKeys, IsKeyDown, vk =>
                    {
                        uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                        IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1);
                        PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
                    });
                }

                // SendInput is UNTARGETED, so it must wait until our client actually
                // holds foreground or it would assert device state at whatever window
                // is in front. One wait shared by modifiers and letters.
                bool isFg = false;
                if (letterKeys.Count > 0 || heldModifiers.Count > 0)
                {
                    // ~150ms, not 60: a cycle across many heavy EVE clients can take
                    // longer than 60ms to actually hand over foreground, and missing the
                    // window silently drops the device half - the difference between a
                    // held modifier locking a target and doing nothing (#108). Loop exits
                    // on the first check in the normal case, so this costs nothing.
                    isFg = await WaitUntilForeground(hwnd, GetForegroundWindow, () => Task.Delay(10));
                }

                // Modifier DOWN, device half (#108). PostMessage alone reaches EVE's UI
                // layer - Ctrl-click row select responds to it - but NOT its lock-target
                // action, which reads the device state. That is the same two-reader split
                // the letters' hybrid below was built for, and it is why a lock modifier
                // remapped to a LETTER locked on switch while Ctrl never did: letters got
                // this half, modifiers did not. Re-check IsKeyDown immediately before
                // injecting: asserting a modifier the user has already released would
                // leave it stuck system-wide, and the release poller reads the same
                // GetAsyncKeyState we would have just falsified, so it could not undo it.
                if (isFg && heldModifiers.Count > 0)
                {
                    int asserted = 0, refused = 0, sendErr = 0;
                    foreach (var vk in heldModifiers)
                    {
                        // RE-PRESS, not a bare DOWN. The key is physically held, so the
                        // device state already reads down and a lone SendInput DOWN changes
                        // nothing - no edge is produced (the sticky-key note below says the
                        // same thing about letters). EVE's lock-target action wants that
                        // edge: releasing Ctrl by hand after a switch and pressing it again
                        // always locks, which is precisely this pair. UP then DOWN, with no
                        // await between them, so the modifier is never observably released.
                        // Guarded by IsKeyDown so a modifier the user has already let go of
                        // is never re-asserted - the release poller reads the same
                        // GetAsyncKeyState we would be falsifying and could not undo it.
                        if (!IsKeyDown(vk)) continue;

                        // NEVER device-re-press SHIFT. Windows counts five consecutive
                        // Shift presses as the Sticky Keys gesture, and it does not care
                        // that they are synthetic: cycling five clients with Shift held
                        // popped Sticky Keys on a user's machine. Harmless before 2.3.34
                        // only because SendInput was silently failing every call; the
                        // moment it started working, this became a real keystroke storm.
                        // Shift still gets the PostMessage half above, which is targeted
                        // at the one client and cannot trip an accessibility gesture.
                        if (vk == 0x10) continue;

                        SendInputScan(vk, keyUp: true);
                        if (SendInputScan(vk, keyUp: false) == 0)
                        { refused++; sendErr = Marshal.GetLastWin32Error(); }
                        else asserted++;
                    }
                    if (refused > 0)
                        LogInjection($"[FixTargetHeldKeys] ❌ SendInput RE-PRESS REFUSED by the OS for {refused} modifier(s) → foreground HWND {hwnd} (err={sendErr}{(sendErr == 5 ? " ACCESS_DENIED - injected input is blocked into a higher-integrity window, e.g. EVE started as administrator while we are not" : "")})");
                    if (asserted > 0)
                        LogInjection($"[FixTargetHeldKeys] ⌨ SendInput RE-PRESS {asserted} modifier(s) → foreground HWND {hwnd}");
                }

                if (letterKeys.Count > 0)
                {
                    if (isFg)
                    {
                        PressStickyKeys(letterKeys, IsKeyDown, vk =>
                        {
                            SendInputScan(vk, keyUp: false);
                        });
                        LogInjection($"[FixTargetHeldKeys] ⌨ PostMessage + SendInput DOWN {letterKeys.Count} sticky key(s) → foreground HWND {hwnd}");
                    }
                    else
                    {
                        LogInjection($"[FixTargetHeldKeys] ⌨ PostMessage DOWN {letterKeys.Count} sticky key(s) (SendInput skipped — HWND {hwnd} not foreground, fg=0x{GetForegroundWindow().ToInt64():X})");
                    }
                }

                // One-shot DOWN, 30 ms, one-shot UP only - letters stay down until physical release. HoldPulse sends
                // the UP of every pulse it started in a finally, so a throw in between cannot leave a pulse key or a
                // mouse button down in the client.
                var pulses = new List<(Action Down, Action Up)>();
                foreach (var vk in pulseKeys)
                {
                    uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                    IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1);
                    IntPtr lParamUp = (IntPtr)unchecked((int)(0xC0000000 | (scanCode << 16) | 1));
                    pulses.Add((
                        () =>
                        {
                            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
                        },
                        () =>
                        {
                            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, lParamUp);
                        }));
                }

                if (hasMouse1 || hasMouse2 || hasMouse3)
                {
                    pulses.Add((
                        () =>
                        {
                            if (hasMouse1) PostMessage(hwnd, 0x0207, IntPtr.Zero, lParamMouse);
                            if (hasMouse2) PostMessage(hwnd, 0x020B, (IntPtr)0x00010000, lParamMouse);
                            if (hasMouse3) PostMessage(hwnd, 0x020B, (IntPtr)0x00020000, lParamMouse);
                        },
                        () =>
                        {
                            if (hasMouse1) PostMessage(hwnd, 0x0208, IntPtr.Zero, lParamMouse);
                            if (hasMouse2) PostMessage(hwnd, 0x020C, (IntPtr)0x00010000, lParamMouse);
                            if (hasMouse3) PostMessage(hwnd, 0x020C, (IntPtr)0x00020000, lParamMouse);
                        }));
                }

                await HoldPulse(pulses, () => Task.Delay(30));

                LogInjection($"[FixTargetHeldKeys] ✅ Finished injecting (sticky:{letterKeys.Count}, pulse:{pulseKeys.Count}) for HWND {hwnd}");
            }).ContinueWith(
                // The carry is fire-and-forget: without this a throw would be lost. The pulse keys and mouse buttons
                // were already released by HoldPulse's finally; the poller still releases the sticky ones. Type and
                // message only, in the opt-in injection log - no stack trace.
                t => LogInjection($"[FixTargetHeldKeys] ❌ carry stopped for HWND {hwnd}: {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"),
                System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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

    /// <summary>Raised when a switch found an administrator window in front, which this process's hooks and injected
    /// keys can't reach (App tells the user once per session).</summary>
    public static event Action? ElevatedWindowInFront;

    /// <summary>
    /// Switches to an EVE client the way EVE-X-Preview does (src/Main_Class.ahk ActivateEVEWindow / ActivateForgroundWindow):
    /// a client already in front is left alone; a minimized one is left to its caller, who restores it with
    /// ShowWindowAsync, which activates it; any other client gets one spare key (vk0xE8, no function in the game) sent
    /// by InjectVirtualKey. MultiPreview's own RegisterHotKey on that key consumes it, and the WM_HOTKEY it raises runs
    /// SetForegroundWindow with the right to take the foreground (HotkeyService.RegisterActivationHotkey). Nothing else
    /// touches the client: no AttachThreadInput, no BringWindowToTop, no SetWindowPos, no SetFocus.
    ///
    /// Why only this: a key held across the switch (Shift+F for drones, Shift or Ctrl+Space, D) has to take effect in
    /// the new client, as it does under EVE-X-Preview. An earlier direct SetForegroundWindow, a phantom keystroke and
    /// an AttachThreadInput borrow each ran before the bridge; the last shared the input queue of the client being left
    /// and could desync the held-key state of the next one.
    ///
    /// Asynchronous: the activation lands when the bridge's WM_HOTKEY is dispatched, a few ms after this returns, so the
    /// foreground has not changed yet when it returns.
    /// </summary>
    public static void ActivateWindow(IntPtr hwnd)
    {
        // A new activation supersedes any queued one. The bridge parks a target in
        // PendingActivateHwnd and relies on the vk0xE8 WM_HOTKEY to consume it; if
        // that dispatch never arrives, the target is stranded and the NEXT activation's
        // WM_HOTKEY would consume it and raise the previous client instead of the one
        // just clicked. Clearing here makes that impossible (#95).
        PendingActivateHwnd = IntPtr.Zero;

        if (GetForegroundWindow() == hwnd) return;

        EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateWindow] Foreground shift requested for HWND {hwnd}");

        // A minimized client: the callers (ThumbnailManager.ActivateEveWindow, CycleCharSelect) restore it with
        // ShowWindowAsync, which activates it; EVE-X-Preview does nothing else for an iconic client. Calling
        // SW_RESTORE again here once raced with that restore and un-maximized a maximized window.
        if (IsIconic(hwnd)) return;

        // An administrator window in front while this process is not elevated: Windows drops the bridge's SendInput
        // aimed at it (UIPI, and SendInput doesn't say so), its WM_HOTKEY never comes and nothing switches. The click
        // or hotkey that got us here was input this process received, which is the right to take the foreground, so
        // SetForegroundWindow goes direct, once more if refused, as the bridge's handler does. Only then: every other
        // switch keeps the bridge (#108).
        var front = GetForegroundWindow();
        if (front != IntPtr.Zero && !HooksSeeInputFor(front))
        {
            SwitchOverElevatedWindow(hwnd, front);
            return;
        }

        // No z-order call and no direct SetForegroundWindow on the client (#108): a second activation of the same
        // window ran EVE's focus handling twice and the first click after a switch only moved EVE's focus to the
        // overview. The bridge's SetForegroundWindow is the only one.
        EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateWindow] vk0xE8 bridge for HWND {hwnd}");
        PendingActivateHwnd = hwnd;
        InjectVirtualKey(VK_ACTIVATION);
    }

    /// <summary>ActivateWindow's switch while an administrator window is in front: the bridge handler's own call,
    /// SetForegroundWindow once more if refused, and nothing else.</summary>
    private static void SwitchOverElevatedWindow(IntPtr hwnd, IntPtr front)
    {
        bool switched = SetForegroundWindow(hwnd) || SetForegroundWindow(hwnd);
        EveMultiPreview.Services.DiagnosticsService.LogWindowHook(
            $"[ActivateWindow] elevated window {front} in front: direct SetForegroundWindow for HWND {hwnd} -> {switched}");
        ElevatedWindowInFront?.Invoke();
    }
    
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);



    // ── Process Name Helper ──────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    // ── Message Posting ──────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
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
        return ProcessNames.Get(pid);
    }

    /// <summary>What <see cref="ReadProcessName"/> answers for a process that exists but won't say what it is (a
    /// system process such as dwm). Never an EVE client and never this app.</summary>
    public const string ProtectedProcess = "(protected)";

    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    /// <summary>Asks Windows for one process's exe name ("exefile"). One open and one query: no list of every process,
    /// and no wait that lets other window messages run (see <see cref="ProcessNames"/>, which remembers the answer).
    /// The special cases answer as System.Diagnostics.Process did, because callers decide by them: process 0 (what a
    /// window that is already gone reports) is "Idle"; a process that exists but can't be read is
    /// <see cref="ProtectedProcess"/>; only a process that is gone is null.</summary>
    public static string? ReadProcessName(uint pid)
    {
        if (pid == 0) return "Idle";
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? ProtectedProcess : null;
        try
        {
            var path = new StringBuilder(1024);
            uint size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(process, 0, path, ref size) && size > 0 ? ProcessNameFromPath(path.ToString(0, (int)size)) : ProtectedProcess;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>The file name without its folder and without ".exe", as System.Diagnostics.Process named it.</summary>
    public static string ProcessNameFromPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static bool IsEveProcessName(string? procName)
    {
        if (string.IsNullOrEmpty(procName)) return false;
        return procName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This process's own name. The exe is EVE.MultiPreview.exe as downloaded from a release,
    /// EVE MultiPreview.exe once the in-app updater has saved it, and whatever a player renames it to.</summary>
    private static readonly string OwnProcessName = ProcessNameFromPath(Environment.ProcessPath ?? "");

    /// <summary>Whether a process name is MultiPreview's: this process's own name, or one of the names its exe has
    /// had. Until it knew the download's name, a freshly downloaded copy took its own settings window and menus for
    /// another program (hotkeys set to work only while EVE is in front, thumbnails that hide when EVE loses focus).</summary>
    public static bool IsAppProcessName(string? procName)
    {
        if (string.IsNullOrEmpty(procName)) return false;
        return procName.Equals("EveMultiPreview", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("EVE MultiPreview", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("EVE.MultiPreview", StringComparison.OrdinalIgnoreCase) ||
               (OwnProcessName.Length > 0 && procName.Equals(OwnProcessName, StringComparison.OrdinalIgnoreCase));
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
    public const int WM_LBUTTONUP   = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP   = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP   = 0x0208;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP   = 0x020C;
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

    // ── Message loop (for running a WH_MOUSE_LL hook on a dedicated thread) ──
    // A low-level hook's callback is dispatched on the thread that installed it,
    // and that thread MUST pump messages. Running the mouse hook on its own
    // thread keeps system-wide mouse input off the busy WPF UI thread.
    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

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
