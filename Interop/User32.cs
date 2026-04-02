using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ── Window Positioning ───────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out DwmApi.RECT lpRect);

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

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
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

    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// The target hwnd for the next vkE8 WM_HOTKEY activation.
    /// Mirrors AHK's This.ActivateHwnd.
    /// </summary>
    public static IntPtr PendingActivateHwnd;

    /// <summary>
    /// Window activation: SetForegroundWindow + SetFocus.
    /// Uses AttachThreadInput to bypass UIPI/Foreground restrictions.
    /// </summary>
    public static void ActivateWindow(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;

        IntPtr fgHwnd = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fgHwnd, out _);
        uint myThread = GetCurrentThreadId();

        if (fgThread != myThread && fgThread != 0)
        {
            AttachThreadInput(myThread, fgThread, true);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
            AttachThreadInput(myThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
    }

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// Called from the vkE8 RegisterHotKey WM_HOTKEY handler.
    /// Mirrors AHK's ActivateForgroundWindow callback.
    /// </summary>
    public static void SetForegroundWindowForPending()
    {
        IntPtr hwnd = PendingActivateHwnd;
        if (hwnd == IntPtr.Zero) return;

        if (!SetForegroundWindow(hwnd))
            SetForegroundWindow(hwnd);
    }



    /// <summary>
    // Modifier VKs that EVE recognizes for key combinations
    private static readonly int[] ModifierVks = {
        0x10, // VK_SHIFT
        0x11, // VK_CONTROL
        0x12, // VK_MENU (Alt)
        0xA0, // VK_LSHIFT
        0xA1, // VK_RSHIFT
        0xA2, // VK_LCONTROL
        0xA3, // VK_RCONTROL
        0xA4, // VK_LMENU (Left Alt)
        0xA5, // VK_RMENU (Right Alt)
    };

    private static bool IsGameKey(int vk) =>
        (vk >= 0x41 && vk <= 0x5A) || // A-Z
        (vk >= 0x30 && vk <= 0x39) || // 0-9
        (vk >= 0x60 && vk <= 0x69) || // Numpad 0-9
        (vk >= 0x70 && vk <= 0x87) || // F1-F24
        (vk == 0x20);                 // Space

    /// <summary>
    /// Posts WM_KEYUP for held game keys then held modifiers (game keys first, modifiers last).
    /// Reverse order of PostGameKeysDown so EVE sees the combo released correctly.
    /// </summary>
    public static void PostGameKeysUp(IntPtr hwnd)
    {
        // Game keys UP first
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (IsGameKey(vk) && (GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParamUp = (IntPtr)((scanCode << 16) | 1u | (1u << 30) | (1u << 31));
                PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, lParamUp);
            }
        }
        // Modifiers UP last (Ctrl+D release = D up, then Ctrl up)
        foreach (int vk in ModifierVks)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParamUp = (IntPtr)((scanCode << 16) | 1u | (1u << 30) | (1u << 31));
                PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, lParamUp);
            }
        }
    }

    /// <summary>
    /// Posts WM_KEYDOWN for held modifiers first, then held game keys.
    /// Modifier-first order ensures EVE sees the combo correctly (e.g. Ctrl down, then D down = Ctrl+D).
    /// Also includes F1-F24 for EVE drone/module shortcuts.
    /// </summary>
    public static void PostGameKeysDown(IntPtr hwnd)
    {
        // Modifiers DOWN first (Ctrl+D = Ctrl down, then D down)
        foreach (int vk in ModifierVks)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1u);
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
            }
        }
        // Game keys DOWN after modifiers
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (IsGameKey(vk) && (GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                IntPtr lParamDown = (IntPtr)((scanCode << 16) | 1u);
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lParamDown);
            }
        }
    }

    /// <summary>
    /// Injects KEYDOWN events for all physically held game keys via SendInput.
    /// Unlike PostMessage, SendInput goes through the real Windows input pipeline
    /// (same as AHK's SendEvent), updating the system keyboard state that
    /// DirectInput/Raw Input reads. Targets the current foreground window.
    /// No KEYUP is sent — we want the key to stay "held."
    /// </summary>
    public static void SendGameKeysDown()
    {
        var inputs = new List<INPUT>();
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            bool isGameKey = (vk >= 0x41 && vk <= 0x5A) || // A-Z
                             (vk >= 0x30 && vk <= 0x39) || // 0-9
                             (vk >= 0x60 && vk <= 0x69) || // Numpad 0-9
                             (vk == 0x20);                 // Space

            if (isGameKey && (GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                var input = new INPUT { type = INPUT_KEYBOARD };
                input.U.ki.wVk = (ushort)vk;
                input.U.ki.wScan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                // No KEYEVENTF_KEYUP flag → this is a key-down event
                inputs.Add(input);
            }
        }
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Injects KEYUP events for all physically held game keys via SendInput.
    /// EVE triggers actions on key release — this completes the press→release cycle
    /// so the action actually fires on each client during rapid cycling.
    /// </summary>
    public static void SendGameKeysUp()
    {
        var inputs = new List<INPUT>();
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            bool isGameKey = (vk >= 0x41 && vk <= 0x5A) || // A-Z
                             (vk >= 0x30 && vk <= 0x39) || // 0-9
                             (vk >= 0x60 && vk <= 0x69) || // Numpad 0-9
                             (vk == 0x20);                 // Space

            if (isGameKey && (GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                var input = new INPUT { type = INPUT_KEYBOARD };
                input.U.ki.wVk = (ushort)vk;
                input.U.ki.wScan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
                input.U.ki.dwFlags = KEYEVENTF_KEYUP;
                inputs.Add(input);
            }
        }
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }



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
}
