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
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int infoLength, out int returned);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    private static bool? ProcessIsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        try { return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null; }
        finally { CloseHandle(token); }
    }

    private static readonly Lazy<bool> SelfElevated = new(() => ProcessIsElevated(GetCurrentProcess()) == true);

    /// <summary>True when this process's low-level hooks can see input aimed at the window: it exists, and it is not an
    /// elevated window while this process is not. When Windows won't say (no access to the owner), false: the caller
    /// must not read a silent hook as a dropped one.</summary>
    public static bool HooksSeeInputFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (SelfElevated.Value) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try { return ProcessIsElevated(process) == false; }
        finally { CloseHandle(process); }
    }

    /// <summary>The time (Environment.TickCount's clock) of the message being handled: for a WM_HOTKEY, when the
    /// system posted it, so a handler that ran late still knows when the key was pressed (IDEAS #41).</summary>
    [DllImport("user32.dll")]
    public static extern int GetMessageTime();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

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
    /// declared even though the only event sent is the vk0xE8 activation key: the union's size sets
    /// sizeof(INPUT), and SendInput validates the cbSize we pass against its own.
    /// Declaring only KEYBDINPUT gives 32 bytes on x64 where Windows expects 40, and
    /// EVERY SendInput call then fails with ERROR_INVALID_PARAMETER (87) - silently,
    /// because the return value is discarded (#92, #108).</summary>
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

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// The target hwnd for the next vkE8 WM_HOTKEY activation.
    /// Mirrors AHK's This.ActivateHwnd.
    /// </summary>
    public static IntPtr PendingActivateHwnd;

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

        // No z-order call and no direct SetForegroundWindow on the client (#108): a second activation of the same
        // window ran EVE's focus handling twice and the first click after a switch only moved EVE's focus to the
        // overview. The bridge's SetForegroundWindow is the only one.
        EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateWindow] vk0xE8 bridge for HWND {hwnd}");
        PendingActivateHwnd = hwnd;
        InjectVirtualKey(VK_ACTIVATION);
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
