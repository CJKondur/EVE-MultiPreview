using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;



/// <summary>
/// Manages global hotkey registration and dispatch using Win32 RegisterHotKey.
/// Thread-safe: hotkeys are registered on the WPF UI thread via a hidden message window.
/// Supports per-character, group cycling, visibility toggles, and all AHK hotkey types.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private HwndSource? _hwndSource;
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private int _nextId = 1;
    private bool _suspended = false;
    private bool _eveOnlyScope = false; // When true, hotkeys only fire when EVE is foreground
    private AppSettings? _appSettings; // For TOS key-block guard
    private int _suspendHotkeyId = -1; // Suspend hotkey is always allowed
    private bool _hotkeysActive = false; // Whether non-suspend hotkeys are currently registered

    // Stored hotkey specs for re-registration when EVE windows appear/disappear
    private readonly List<HotkeySpec> _storedSpecs = new();
    private record HotkeySpec(uint Modifiers, uint VirtualKey, Action Action, bool AllowRepeat);

    // ── Mouse button hotkey support (WH_MOUSE_LL) ──
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private User32.LowLevelMouseProc? _mouseHookProc; // prevent GC
    private readonly List<MouseButtonBinding> _mouseBindings = new();
    private readonly List<MouseButtonBinding> _storedMouseBindings = new(); // persist across suspend
    private record MouseButtonBinding(uint Modifiers, string ButtonName, Action Action);

    // ── Keyboard pass-through support (WH_KEYBOARD_LL) ──
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private User32.LowLevelKeyboardProc? _keyboardHookProc; // prevent GC
    private readonly List<KeyboardBinding> _keyboardBindings = new();
    private readonly List<KeyboardBinding> _storedKeyboardBindings = new();
    private record KeyboardBinding(uint Modifiers, uint VirtualKey, Action Action, bool AllowRepeat, bool Swallow = false);

    // Events for external wiring
    public event Action? SuspendToggled;

    public bool IsSuspended => _suspended;

    /// <summary>Initialize the hotkey service. Must be called on UI thread.</summary>
    public void Initialize()
    {
        var parameters = new HwndSourceParameters("EveMultiPreviewHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    /// <summary>Register a global hotkey.</summary>
    /// <returns>Hotkey ID, or -1 on failure.</returns>
    public int Register(uint modifiers, uint key, Action action, bool allowRepeat = false)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\tensk\Desktop\EVE Projects\EveMultiPreview\hklog.txt", $"Register called: Mod={modifiers}, Key={key}, repeat={allowRepeat}\n"); } catch {}
        if (_hwndSource == null) return -1;

        int id = _nextId++;
        uint finalMods = allowRepeat ? modifiers : (modifiers | User32.MOD_NOREPEAT);
        if (User32.RegisterHotKey(_hwndSource.Handle, id, finalMods, key))
        {
            _hotkeyActions[id] = action;
            Debug.WriteLine($"[Hotkey:Register] \u2705 Registered ID={id}, Mod=0x{modifiers:X}, Key=0x{key:X}, repeat={allowRepeat}");
            return id;
        }

        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        try { System.IO.File.AppendAllText(@"C:\Users\tensk\Desktop\EVE Projects\EveMultiPreview\hklog.txt", $"[Hotkey:Register] Failed Mod=0x{modifiers:X}, Key=0x{key:X} (err={err})\n"); } catch {}
        Debug.WriteLine($"[Hotkey:Register] ❌ Failed Mod=0x{modifiers:X}, Key=0x{key:X} (err={err})");
        return -1;
    }

    /// <summary>Unregister a specific hotkey by ID.</summary>
    public void Unregister(int id)
    {
        if (_hwndSource == null) return;
        User32.UnregisterHotKey(_hwndSource.Handle, id);
        _hotkeyActions.Remove(id);
    }

    /// <summary>Unregister all hotkeys (including suspend).</summary>
    public void UnregisterAll()
    {
        if (_hwndSource == null) return;
        foreach (var id in _hotkeyActions.Keys.ToList())
            User32.UnregisterHotKey(_hwndSource.Handle, id);
        _hotkeyActions.Clear();
        _storedSpecs.Clear();
        _storedMouseBindings.Clear();
        _storedKeyboardBindings.Clear();
        RemoveMouseHook();
        RemoveKeyboardHook();
        _suspendHotkeyId = -1;
        _hotkeysActive = false;
        _nextId = 1;
    }

    /// <summary>Register all stored non-suspend hotkeys. Call when EVE windows appear.</summary>
    public void ActivateHotkeys()
    {
        if (_hotkeysActive || _suspended || _hwndSource == null) return;
        foreach (var spec in _storedSpecs)
        {
            int id = _nextId++;
            uint finalMods = spec.AllowRepeat ? spec.Modifiers : (spec.Modifiers | User32.MOD_NOREPEAT);
            if (User32.RegisterHotKey(_hwndSource.Handle, id, finalMods, spec.VirtualKey))
                _hotkeyActions[id] = spec.Action;
        }
        _hotkeysActive = true;
        Debug.WriteLine($"[Hotkey:Activate] ✅ Activated {_storedSpecs.Count} hotkeys (EVE windows detected)");
    }

    /// <summary>Unregister all non-suspend hotkeys. Call when last EVE window closes.</summary>
    public void DeactivateHotkeys()
    {
        if (!_hotkeysActive || _hwndSource == null) return;
        foreach (var id in _hotkeyActions.Keys.ToList())
        {
            if (id == _suspendHotkeyId) continue;
            User32.UnregisterHotKey(_hwndSource.Handle, id);
            _hotkeyActions.Remove(id);
        }
        _hotkeysActive = false;
        Debug.WriteLine("[Hotkey:Deactivate] ⏸ Deactivated hotkeys (no EVE windows)");
    }

    /// <summary>Toggle suspend state — properly releases all hooks back to Windows.</summary>
    public void ToggleSuspend()
    {
        _suspended = !_suspended;

        if (_suspended)
        {
            // Suspend: unregister all keyboard hotkeys EXCEPT the suspend toggle itself
            if (_hwndSource != null)
            {
                foreach (var id in _hotkeyActions.Keys.ToList())
                {
                    if (id == _suspendHotkeyId) continue;
                    User32.UnregisterHotKey(_hwndSource.Handle, id);
                    _hotkeyActions.Remove(id);
                }
            }
            _hotkeysActive = false;

            // Remove mouse/keyboard hook so inputs return to Windows
            RemoveMouseHook();
            RemoveKeyboardHook();

            Debug.WriteLine("[Hotkey:Suspend] ⏸ All hotkeys unregistered, hooks removed — keys returned to Windows");
        }
        else
        {
            // Resume: re-register all stored keyboard hotkeys
            ActivateHotkeys();

            // Re-install hooks
            ReinstallMouseBindings();
            ReinstallKeyboardBindings();

            Debug.WriteLine("[Hotkey:Suspend] ▶ All hotkeys re-registered, hooks restored");
        }

        SuspendToggled?.Invoke();
        Debug.WriteLine($"[Hotkey:Scope] ⏸ Suspend: {_suspended}");
    }

    /// <summary>
    /// Register all hotkeys from settings. Call from UI thread after services are wired.
    /// </summary>
    public void RegisterFromSettings(AppSettings settings, Profile profile,
        ThumbnailManager thumbnailManager, Action openSettings)
    {
        UnregisterAll();

        // Store settings ref for TOS guard
        _appSettings = settings;

        // Set EVE Only scope from settings
        _eveOnlyScope = !settings.GlobalHotkeys;
        Debug.WriteLine($"[Hotkey:Scope] \uD83D\uDD27 EVE Only scope: {_eveOnlyScope}");

        // Suspend hotkey
        _suspendHotkeyId = RegisterAhkHotkey(settings.SuspendHotkey, () => ToggleSuspend());

        // Internal activation hotkey (vkE8) — mirrors AHK's ActivateForgroundWindow.
        // When ActivateWindow sends vkE8 through the input pipeline, this WM_HOTKEY
        // handler fires and calls SetForegroundWindow. Because the activation happens
        // INSIDE the keyboard input pipeline, Windows preserves the physical keyboard
        // state (held keys like D) across the focus transition.
        Register(0, User32.VK_ACTIVATION, () =>
        {
            Interop.User32.SetForegroundWindowForPending();
        });

        // Non-suspend hotkeys are stored and initially deactivated
        // They'll be activated when EVE windows are detected

        // Click-through toggle
        StoreAhkHotkey(settings.ClickThroughHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.ToggleClickThrough();
        });

        // Hide/Show all thumbnails
        StoreAhkHotkey(settings.HideShowThumbnailsHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.ToggleAllVisibility();
        });

        // Hide/Show primary only
        StoreAhkHotkey(settings.HidePrimaryHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.TogglePrimaryVisibility();
        });

        // Hide/Show secondary (PiP) only
        StoreAhkHotkey(settings.HideSecondaryHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.ToggleSecondaryVisibility();
        });

        // ── Char-select cycling hotkeys (AHK CharSelectCycling) ──
        if (settings.CharSelectCyclingEnabled)
        {
            StoreRepeatableAhkHotkey(settings.CharSelectForwardHotkey, () =>
            {
                if (_suspended) return;
                thumbnailManager.CycleCharSelect(forward: true);
            });
            StoreRepeatableAhkHotkey(settings.CharSelectBackwardHotkey, () =>
            {
                if (_suspended) return;
                thumbnailManager.CycleCharSelect(forward: false);
            });
            Debug.WriteLine("[Hotkey:Register] 🔄 Char-select cycling hotkeys stored (repeatable)");
        }

        // ── Quick-Switch Wheel hotkey ──
        if (!string.IsNullOrWhiteSpace(settings.QuickSwitchHotkey))
        {
            StoreAhkHotkey(settings.QuickSwitchHotkey, () =>
            {
                if (_suspended) return;
                thumbnailManager.ShowQuickSwitch();
            });
            Debug.WriteLine("[Hotkey:Register] 🎡 Quick-Switch wheel hotkey stored");
        }

        // Profile cycle forward
        StoreAhkHotkey(settings.ProfileCycleForwardHotkey, () =>
        {
            if (_suspended) return;
            ProfileCycleForward?.Invoke();
        });

        // Profile cycle backward
        StoreAhkHotkey(settings.ProfileCycleBackwardHotkey, () =>
        {
            if (_suspended) return;
            ProfileCycleBackward?.Invoke();
        });

        // ── Per-character hotkeys from profile ───────────────────────

        var sharedHotkeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (charName, binding) in profile.Hotkeys)
        {
            string hotkeyStr = binding.Key;
            if (string.IsNullOrWhiteSpace(hotkeyStr)) continue;

            if (!sharedHotkeys.ContainsKey(hotkeyStr))
                sharedHotkeys[hotkeyStr] = new List<string>();
            sharedHotkeys[hotkeyStr].Add(charName);
        }

        foreach (var (hotkeyStr, members) in sharedHotkeys)
        {
            var capturedMembers = members;
            if (capturedMembers.Count == 1)
            {
                string capturedName = capturedMembers[0];
                StoreAhkHotkey(hotkeyStr, () =>
                {
                    if (_suspended) return;
                    thumbnailManager.ActivateEveWindow(IntPtr.Zero, capturedName);
                });
            }
            else
            {
                StoreRepeatableAhkHotkey(hotkeyStr, () =>
                {
                    if (_suspended) return;
                    thumbnailManager.CycleGroup(hotkeyStr + "_shared", capturedMembers, forward: true);
                });
            }
        }

        // Group cycling hotkeys (from profile HotkeyGroups)
        var fwdMerged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var bwdMerged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupName, group) in profile.HotkeyGroups)
        {
            Debug.WriteLine($"[Hotkey:Register] 🔧 Group '{groupName}': Fwd='{group.ForwardsHotkey}', Bwd='{group.BackwardsHotkey}', Members=[{string.Join(", ", group.Characters)}]");

            if (!string.IsNullOrEmpty(group.ForwardsHotkey))
            {
                if (!fwdMerged.ContainsKey(group.ForwardsHotkey))
                    fwdMerged[group.ForwardsHotkey] = new List<string>();
                fwdMerged[group.ForwardsHotkey].AddRange(group.Characters);
            }
            if (!string.IsNullOrEmpty(group.BackwardsHotkey))
            {
                if (!bwdMerged.ContainsKey(group.BackwardsHotkey))
                    bwdMerged[group.BackwardsHotkey] = new List<string>();
                bwdMerged[group.BackwardsHotkey].AddRange(group.Characters);
            }
        }

        // Store merged forward hotkeys
        foreach (var (hotkeyStr, mergedMembers) in fwdMerged)
        {
            var members = mergedMembers;
            StoreRepeatableAhkHotkey(hotkeyStr, () =>
            {
                if (_suspended) return;
                thumbnailManager.CycleGroup(hotkeyStr + "_fwd", members, forward: true);
            });
        }

        // Store merged backward hotkeys
        foreach (var (hotkeyStr, mergedMembers) in bwdMerged)
        {
            var members = mergedMembers;
            StoreRepeatableAhkHotkey(hotkeyStr, () =>
            {
                if (_suspended) return;
                thumbnailManager.CycleGroup(hotkeyStr + "_bwd", members, forward: false);
            });
        }

        Debug.WriteLine($"[Hotkey:Register] ✅ Stored {_storedSpecs.Count} hotkeys, suspend ID={_suspendHotkeyId} (eveOnly={_eveOnlyScope})");
    }

    // Events for profile cycling
    public event Action? ProfileCycleForward;
    public event Action? ProfileCycleBackward;

    /// <summary>
    /// Register a hotkey from an AHK-format string (e.g., "NumpadDiv", "^a", "ctrl & 1").
    /// Returns the hotkey ID, or -1 on failure.
    /// </summary>
    public int RegisterAhkHotkey(string ahkKeyString, Action action)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return -1;

        // Check if this is a mouse button hotkey
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString);
        if (IsMouseButton(keyPart))
        {
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Registered mouse binding: {ahkKeyString}");
            return -2; // Special ID for mouse bindings
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        Debug.WriteLine($"[Hotkey:Parse] 🔧 ParseAhk '{ahkKeyString}' → Mod=0x{parsedMods:X}, VK=0x{vk:X}");
        if (vk == 0) return -1;

        bool isPassThrough = ahkKeyString.Contains('~');
        if (isPassThrough)
        {
            AddKeyboardBinding(parsedMods, vk, action, false);
            return -2;
        }

        return Register(parsedMods, vk, action);
    }

    /// <summary>Store a hotkey spec without registering it. Will be registered when ActivateHotkeys is called.</summary>
    public void StoreAhkHotkey(string ahkKeyString, Action action)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return;

        // Mouse buttons go through mouse hook, not RegisterHotKey
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString);
        if (IsMouseButton(keyPart))
        {
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Stored mouse binding: {ahkKeyString}");
            return;
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        if (vk == 0) return;

        bool isPassThrough = ahkKeyString.Contains('~');
        if (isPassThrough)
        {
            AddKeyboardBinding(parsedMods, vk, action, false);
            return;
        }

        _storedSpecs.Add(new HotkeySpec(parsedMods, vk, action, false));
    }

    /// <summary>Store a repeatable hotkey spec without registering it.</summary>
    public void StoreRepeatableAhkHotkey(string ahkKeyString, Action action)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return;

        // Mouse buttons go through mouse hook
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString);
        if (IsMouseButton(keyPart))
        {
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Stored repeatable mouse binding: {ahkKeyString}");
            return;
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        if (vk == 0) return;
        
        bool isPassThrough = ahkKeyString.Contains('~');
        if (isPassThrough)
        {
            AddKeyboardBinding(parsedMods, vk, action, true);
            return;
        }

        _storedSpecs.Add(new HotkeySpec(parsedMods, vk, action, true));
    }

    /// <summary>
    /// Register a repeatable hotkey (allows key-repeat when held down).
    /// Used for cycle hotkeys so holding continuously cycles through clients.
    /// </summary>
    public int RegisterRepeatableAhkHotkey(string ahkKeyString, Action action)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return -1;
        var (mods, vk) = ParseAhkHotkeyString(ahkKeyString);
        if (vk == 0) return -1;
        
        return Register(mods, vk, action, allowRepeat: true);
    }

    /// <summary>
    /// Parse an AHK-format hotkey string into Win32 modifiers and virtual key code.
    /// Supports AHK formats:
    ///   - Prefix modifiers: ^ (Ctrl), ! (Alt), + (Shift), # (Win)
    ///   - Named modifiers: "ctrl & key", "alt & key"
    ///   - Direct key names: "F1", "NumpadDiv", "Home", "PgUp"
    /// </summary>
    public static (uint Modifiers, uint VirtualKey) ParseAhkHotkeyString(string ahkKey)
    {
        if (string.IsNullOrWhiteSpace(ahkKey)) return (0, 0);

        uint mods = 0;
        string keyPart = ahkKey.Trim();

        // Handle AHK "modifier & key" format (e.g., "ctrl & 1", "Xbutton1 & 1")
        if (keyPart.Contains('&'))
        {
            var parts = keyPart.Split('&', 2);
            string modPart = parts[0].Trim().ToLowerInvariant();
            keyPart = parts[1].Trim();

            // Parse the modifier part — could have prefix modifiers on it too
            // e.g., "^XButton1" means Ctrl+XButton1
            while (modPart.Length > 0)
            {
                if (modPart[0] == '^') { mods |= User32.MOD_CONTROL; modPart = modPart[1..]; }
                else if (modPart[0] == '!') { mods |= User32.MOD_ALT; modPart = modPart[1..]; }
                else if (modPart[0] == '+') { mods |= User32.MOD_SHIFT; modPart = modPart[1..]; }
                else if (modPart[0] == '#') { mods |= User32.MOD_WIN; modPart = modPart[1..]; }
                else if (modPart[0] is '~' or '$' or '*') { modPart = modPart[1..]; }
                else break;
            }

            // The remaining modPart is the modifier key name
            if (modPart.Contains("ctrl") || modPart.Contains("control")) mods |= User32.MOD_CONTROL;
            else if (modPart.Contains("alt")) mods |= User32.MOD_ALT;
            else if (modPart.Contains("shift")) mods |= User32.MOD_SHIFT;
            else if (modPart.Contains("win") || modPart.Contains("lwin") || modPart.Contains("rwin")) mods |= User32.MOD_WIN;
            // XButton1/XButton2 as modifier: not supported by RegisterHotKey, skip
        }
        else
        {
            // Parse prefix modifiers from the key string (e.g., "^a" = Ctrl+A)
            while (keyPart.Length > 0)
            {
                if (keyPart[0] == '^') { mods |= User32.MOD_CONTROL; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '!') { mods |= User32.MOD_ALT; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '+') { mods |= User32.MOD_SHIFT; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '#') { mods |= User32.MOD_WIN; keyPart = keyPart[1..]; }
                else if (keyPart[0] is '~' or '$' or '*') { keyPart = keyPart[1..]; }
                else break;
            }
        }

        uint vk = ParseVirtualKey(keyPart);
        return (mods, vk);
    }

    /// <summary>
    /// Parse a separate modifiers+key pair into Win32 format.
    /// Used by code that already has them split.
    /// </summary>
    public static (uint Modifiers, uint VirtualKey) ParseHotkeyString(string modifiers, string key)
    {
        uint mods = 0;
        if (!string.IsNullOrEmpty(modifiers))
        {
            string modStr = modifiers.ToLowerInvariant();
            if (modStr.Contains("ctrl") || modStr.Contains("control")) mods |= User32.MOD_CONTROL;
            if (modStr.Contains("alt")) mods |= User32.MOD_ALT;
            if (modStr.Contains("shift")) mods |= User32.MOD_SHIFT;
            if (modStr.Contains("win")) mods |= User32.MOD_WIN;
        }

        uint vk = ParseVirtualKey(key);
        return (mods, vk);
    }

    /// <summary>Convert a key name to a Win32 virtual key code. Supports AHK naming conventions.</summary>
    private static uint ParseVirtualKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;

        key = key.Trim().ToUpperInvariant();

        // Function keys
        if (key.StartsWith("F") && int.TryParse(key[1..], out int fNum) && fNum >= 1 && fNum <= 24)
            return (uint)(0x70 + fNum - 1); // VK_F1 = 0x70

        // Number keys
        if (key.Length == 1 && char.IsDigit(key[0]))
            return (uint)key[0]; // VK_0 to VK_9

        // Letter keys
        if (key.Length == 1 && char.IsLetter(key[0]))
            return (uint)key[0]; // VK_A to VK_Z

        // Numpad digits (Numpad0-Numpad9)
        if (key.StartsWith("NUMPAD") && int.TryParse(key[6..], out int npNum) && npNum >= 0 && npNum <= 9)
            return (uint)(0x60 + npNum); // VK_NUMPAD0 = 0x60

        // Special keys — includes AHK key names
        return key switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" or "BACK" or "BS" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            // AHK uses both PgUp/PgDn and PageUp/PageDown
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "PAUSE" => 0x13,
            "SCROLLLOCK" => 0x91,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            // Numpad operation keys — AHK names + standard names
            "NUMPADADD" or "ADD" => 0x6B,
            "NUMPADSUBTRACT" or "NUMPADSUB" or "SUBTRACT" => 0x6D,
            "NUMPADMULTIPLY" or "NUMPADMULT" or "MULTIPLY" => 0x6A,
            "NUMPADDIVIDE" or "NUMPADDIV" or "DIVIDE" => 0x6F,
            "NUMPADDECIMAL" or "NUMPADDOT" or "DECIMAL" => 0x6E,
            "NUMPADENTER" => 0x0D, // Same as Enter
            // Mouse buttons (limited support via RegisterHotKey)
            "XBUTTON1" => 0x05, // VK_XBUTTON1
            "XBUTTON2" => 0x06, // VK_XBUTTON2
            "MBUTTON" => 0x04,  // VK_MBUTTON
            // OEM keys
            "SEMICOLON" or "SC" => 0xBA,
            "EQUALS" or "EQUAL" => 0xBB,
            "COMMA" => 0xBC,
            "MINUS" or "HYPHEN" => 0xBD,
            "PERIOD" or "DOT" => 0xBE,
            "SLASH" => 0xBF,
            "BACKQUOTE" or "TILDE" => 0xC0,
            "LBRACKET" or "[" => 0xDB,
            "BACKSLASH" or "\\" => 0xDC,
            "RBRACKET" or "]" => 0xDD,
            "QUOTE" or "'" => 0xDE,
            _ => 0
        };
    }

    /// <summary>Windows message handler — dispatches WM_HOTKEY to registered actions.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();

            // ── EVE Only scope check ──
            if (_eveOnlyScope)
            {
                try
                {
                    var fgHwnd = User32.GetForegroundWindow();
                    string? fgProc = User32.GetProcessName(fgHwnd);
                    if (fgProc != "exefile" && fgProc != "EveMultiPreview")
                    {
                        Debug.WriteLine($"[Hotkey:Blocked] 🚫 EVE Only scope blocked ID={id} (fg={fgProc})");
                        return IntPtr.Zero; // Don't fire
                    }
                }
                catch { }
            }

            // M5: Exclude settings window from hotkey activation (matches AHK)
            try
            {
                var fgHwnd2 = User32.GetForegroundWindow();
                string? fgTitle = User32.GetWindowTitle(fgHwnd2);
                if (fgTitle != null && fgTitle.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                    && User32.GetProcessName(fgHwnd2) == "EveMultiPreview")
                {
                    Debug.WriteLine($"[Hotkey:Blocked] 🚫 Settings window active, blocked ID={id}");
                    return IntPtr.Zero;
                }
            }
            catch { }

            Debug.WriteLine($"[Hotkey:Fired] \u26A1 WM_HOTKEY ID={id}");
            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                // ── TOS Key-Block Guard ──
                if (_appSettings?.EnableKeyBlockGuard == true)
                {
                    // Extract the VK from lParam (high word)
                    uint hotkeyVk = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
                    if (User32.IsGameKeyHeld(hotkeyVk))
                    {
                        Debug.WriteLine($"[Hotkey:TOS] \uD83D\uDEAB Blocked ID={id} — game key held (guard active)");
                        handled = true;
                        return IntPtr.Zero;
                    }
                }

                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Hotkey:Fired] \u274C Action exception for ID={id}: {ex.Message}");
                }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        RemoveMouseHook();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
    }

    // ── Mouse Button Hotkey Support ──────────────────────────────────

    private static readonly HashSet<string> MouseButtonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "XBUTTON1", "XBUTTON2", "MBUTTON", "MOUSE4", "MOUSE5", "MIDDLECLICK"
    };

    /// <summary>Check if a key name refers to a mouse button.</summary>
    public static bool IsMouseButton(string keyName)
        => !string.IsNullOrWhiteSpace(keyName) && MouseButtonNames.Contains(keyName.Trim());

    /// <summary>Extract modifier prefixes and the base key name from an AHK string.</summary>
    private static (uint Mods, string KeyPart) ExtractModsAndKey(string ahkKey)
    {
        uint mods = 0;
        string keyPart = ahkKey.Trim();

        // Handle "modifier & key" format
        if (keyPart.Contains('&'))
        {
            var parts = keyPart.Split('&', 2);
            string modPart = parts[0].Trim().ToLowerInvariant();
            keyPart = parts[1].Trim();
            if (modPart.Contains("ctrl") || modPart.Contains("control")) mods |= User32.MOD_CONTROL;
            else if (modPart.Contains("alt")) mods |= User32.MOD_ALT;
            else if (modPart.Contains("shift")) mods |= User32.MOD_SHIFT;
            else if (modPart.Contains("win")) mods |= User32.MOD_WIN;
        }
        else
        {
            while (keyPart.Length > 0)
            {
                if (keyPart[0] == '^') { mods |= User32.MOD_CONTROL; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '!') { mods |= User32.MOD_ALT; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '+') { mods |= User32.MOD_SHIFT; keyPart = keyPart[1..]; }
                else if (keyPart[0] == '#') { mods |= User32.MOD_WIN; keyPart = keyPart[1..]; }
                else if (keyPart[0] is '~' or '$' or '*') { keyPart = keyPart[1..]; }
                else break;
            }
        }

        return (mods, keyPart);
    }

    private void AddMouseBinding(uint mods, string buttonName, Action action)
    {
        // Normalize aliases
        buttonName = buttonName switch
        {
            "MOUSE4" => "XBUTTON1",
            "MOUSE5" => "XBUTTON2",
            "MIDDLECLICK" => "MBUTTON",
            _ => buttonName
        };

        var binding = new MouseButtonBinding(mods, buttonName, action);
        _mouseBindings.Add(binding);
        _storedMouseBindings.Add(binding); // keep for suspend/resume
        EnsureMouseHook();
    }

    /// <summary>Reinstall mouse bindings from stored specs after resume from suspend.</summary>
    private void ReinstallMouseBindings()
    {
        if (_storedMouseBindings.Count == 0) return;
        foreach (var binding in _storedMouseBindings)
            _mouseBindings.Add(binding);
        EnsureMouseHook();
    }

    private void EnsureMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero || _suspended) return;

        _mouseHookProc = MouseHookCallback; // prevent GC collection
        _mouseHookHandle = User32.SetWindowsHookEx(
            User32.WH_MOUSE_LL,
            _mouseHookProc,
            User32.GetModuleHandle(null),
            0);

        Debug.WriteLine($"[Hotkey:Mouse] 🪝 Mouse hook installed: {_mouseHookHandle != IntPtr.Zero}");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            Debug.WriteLine("[Hotkey:Mouse] 🛑 Mouse hook removed");
        }
        _mouseBindings.Clear();
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _mouseBindings.Count > 0)
        {
            int msg = wParam.ToInt32();
            string? buttonName = null;

            if (msg == User32.WM_MBUTTONDOWN)
            {
                buttonName = "MBUTTON";
            }
            else if (msg == User32.WM_XBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                int xButton = User32.HIWORD(hookStruct.mouseData);
                buttonName = xButton == User32.XBUTTON1 ? "XBUTTON1" :
                             xButton == User32.XBUTTON2 ? "XBUTTON2" : null;
            }

            if (buttonName != null)
            {
                // Check modifiers
                uint currentMods = 0;
                if (User32.IsKeyDown(0x11)) currentMods |= User32.MOD_CONTROL; // VK_CONTROL
                if (User32.IsKeyDown(0x12)) currentMods |= User32.MOD_ALT;     // VK_MENU
                if (User32.IsKeyDown(0x10)) currentMods |= User32.MOD_SHIFT;   // VK_SHIFT
                if (User32.IsKeyDown(0x5B) || User32.IsKeyDown(0x5C)) currentMods |= User32.MOD_WIN;

                foreach (var binding in _mouseBindings)
                {
                    if (binding.ButtonName == buttonName && binding.Modifiers == currentMods)
                    {
                        // Mouse hooks ALWAYS use EVE-only scope to prevent stealing
                        // XButton/MButton from other applications (even when GlobalHotkeys is on)
                        try
                        {
                            var fgHwnd = User32.GetForegroundWindow();
                            string? fgProc = User32.GetProcessName(fgHwnd);
                            if (fgProc != "exefile" && fgProc != "EveMultiPreview") continue;
                        }
                        catch { }

                        // Settings window block
                        try
                        {
                            var fgHwnd = User32.GetForegroundWindow();
                            string? fgTitle = User32.GetWindowTitle(fgHwnd);
                            if (fgTitle != null && fgTitle.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                                && User32.GetProcessName(fgHwnd) == "EveMultiPreview") continue;
                        }
                        catch { }

                        Debug.WriteLine($"[Hotkey:Mouse] ⚡ Fired: {buttonName} (mods=0x{currentMods:X})");
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            try { binding.Action.Invoke(); }
                            catch (Exception ex) { Debug.WriteLine($"[Hotkey:Mouse] ❌ Action error: {ex.Message}"); }
                        });

                        // Return 1 to suppress the mouse event from reaching other apps
                        return (IntPtr)1;
                    }
                }
            }
        }

        return User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // ── Keyboard Pass-Through Support ────────────────────────────────

    private readonly HashSet<uint> _keysDown = new();

    private void AddKeyboardBinding(uint mods, uint vk, Action action, bool allowRepeat, bool swallow = false)
    {
        var binding = new KeyboardBinding(mods, vk, action, allowRepeat, swallow);
        _keyboardBindings.Add(binding);
        _storedKeyboardBindings.Add(binding); // keep for suspend/resume
        EnsureKeyboardHook();
        Debug.WriteLine($"[Hotkey:Keyboard] ⌨️ Registered {(swallow ? "swallowing" : "passthrough")} binding: VK=0x{vk:X}");
    }

    private void ReinstallKeyboardBindings()
    {
        if (_storedKeyboardBindings.Count == 0) return;
        foreach (var binding in _storedKeyboardBindings)
            _keyboardBindings.Add(binding);
        EnsureKeyboardHook();
    }

    private void EnsureKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero || _suspended) return;

        _keyboardHookProc = KeyboardHookCallback; // prevent GC
        _keyboardHookHandle = User32.SetWindowsHookEx(
            User32.WH_KEYBOARD_LL,
            _keyboardHookProc,
            User32.GetModuleHandle(null),
            0);

        Debug.WriteLine($"[Hotkey:Keyboard] 🪝 Keyboard hook installed: {_keyboardHookHandle != IntPtr.Zero}");
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
            Debug.WriteLine("[Hotkey:Keyboard] 🛑 Keyboard hook removed");
        }
        _keyboardBindings.Clear();
        _keysDown.Clear();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _keyboardBindings.Count > 0)
        {
            int msg = wParam.ToInt32();
            if (msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
                uint vk = hookStruct.vkCode;

                // Ignore modifier keys themselves as primary triggers
                if (vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12)
                    return User32.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);

                bool isRepeat = _keysDown.Contains(vk);
                _keysDown.Add(vk);

                uint currentMods = 0;
                if (User32.IsKeyDown(0x11)) currentMods |= User32.MOD_CONTROL;
                if (User32.IsKeyDown(0x12)) currentMods |= User32.MOD_ALT;
                if (User32.IsKeyDown(0x10)) currentMods |= User32.MOD_SHIFT;
                if (User32.IsKeyDown(0x5B) || User32.IsKeyDown(0x5C)) currentMods |= User32.MOD_WIN;

                bool actionFired = false;
                foreach (var binding in _keyboardBindings)
                {
                    if (binding.VirtualKey == vk && binding.Modifiers == currentMods)
                    {
                        if (isRepeat && !binding.AllowRepeat) continue;

                        // EVE-only scope logic
                        // Non-swallowed keys (Passthrough) ALWAYS test scope to avoid stealing keys like ~ from other apps.
                        // Swallowed keys (Cycle Hotkeys) respect the user's Global Hotkey setting (_eveOnlyScope).
                        if (_eveOnlyScope || !binding.Swallow)
                        {
                            try
                            {
                                var fgHwnd = User32.GetForegroundWindow();
                                string? fgProc = User32.GetProcessName(fgHwnd);
                                if (fgProc != "exefile" && fgProc != "EveMultiPreview") continue;
                                
                                string? fgTitle = User32.GetWindowTitle(fgHwnd);
                                if (fgTitle != null && fgTitle.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                                    && fgProc == "EveMultiPreview") continue;
                            }
                            catch { }
                        }

                        Debug.WriteLine($"[Hotkey:Keyboard] ⚡ Passthrough Fired: VK=0x{vk:X} (Mods=0x{currentMods:X})");
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            try { binding.Action.Invoke(); }
                            catch (Exception ex) { Debug.WriteLine($"[Hotkey:Keyboard] ❌ Action error: {ex.Message}"); }
                        });
                        actionFired = true;
                    }
                }
                
                // If ANY matching binding has Swallow=true, block the key
                // so it never enters the keyboard state.
                if (actionFired)
                {
                    foreach (var binding in _keyboardBindings)
                    {
                        if (binding.VirtualKey == vk && binding.Modifiers == currentMods && binding.Swallow)
                            return (IntPtr)1; // Block the key from reaching any application
                    }
                }
            }
            else if (msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP)
            {
                var hookStruct = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
                _keysDown.Remove(hookStruct.vkCode);
            }
        }
        return User32.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }
}
