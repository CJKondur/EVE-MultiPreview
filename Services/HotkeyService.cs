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
    // Parallel to _hotkeyActions — the trigger virtual-key for each registered
    // hotkey ID. Used by WndProc to discard queued WM_HOTKEY events whose
    // trigger key has already been physically released. Without this, the OS
    // auto-repeat queue can pile up faster than action processing drains it,
    // and cycling continues for seconds after the user lets go.
    private readonly Dictionary<int, uint> _hotkeyVks = new();
    private int _nextId = 1;
    private bool _suspended = false;
    private bool _eveOnlyScope = false; // When true, hotkeys only fire when EVE is foreground
    private AppSettings? _appSettings; // For TOS key-block guard
    private int _suspendHotkeyId = -1; // Suspend hotkey is always allowed
    private readonly List<int> _activationHotkeyIds = new(); // vkE8 internal activation hotkeys
    private bool _hotkeysActive = false; // Whether non-suspend hotkeys are currently registered

    // ── Capture mode (the settings window's hotkey dialog) ──
    // While the new settings window records a hotkey, every key and button this service
    // holds is released, so the press reaches the dialog: RegisterHotKey, the keystroke-
    // hiding hook and the mouse hook would otherwise swallow it (and switch clients), and
    // the dialog's clash warning could never see an already-bound key. Counted, so nested
    // Begin/End pairs are safe; SettingsUi.HotkeyCapture.Run always ends it in a finally.
    private int _captureDepth;
    private string _suspendAhk = "";

    // Foreground-gated registration (issue #68). In EVE-only scope, hotkeys must be
    // UNREGISTERED while a non-EVE app is foreground — otherwise RegisterHotKey
    // captures the key system-wide and swallows it (consumed but not actioned),
    // never reaching e.g. a spreadsheet. We re-evaluate registration on every
    // foreground change so keys pass through whenever they wouldn't be actioned.
    private WinEventHookService? _scopeWinEvents;
    private Func<bool>? _hasWindowsProvider;

    // Stored hotkey specs for re-registration when EVE windows appear/disappear
    private readonly List<HotkeySpec> _storedSpecs = new();
    // BaseModifiers = the user's originally specified modifier set (before wildcard
    // expansion). Used at activation time to break collisions by specificity — a
    // spec derived from "Ctrl+Alt+1" (BaseModifiers=Ctrl|Alt) wins the Ctrl+Alt+1
    // slot over one derived from "Ctrl+1" (BaseModifiers=Ctrl) even though both
    // wildcard-expand to include Ctrl+Alt+1.
    // Priority breaks (Modifiers,VK) collisions in ActivateHotkeys BEFORE specificity:
    // a higher-priority binding wins a shared key. Character- and group-specific
    // cycle/activation bindings use SPECIFIC_PRIORITY so they beat the profile-wide
    // "cycle all" when the user binds the same key to both — otherwise the global
    // cycle (registered first) won the slot and the group-forward key ran client-order
    // cycling instead of group-order (issue #75). Default 0 = ordinary binding.
    private const int SPECIFIC_PRIORITY = 10;
    private record HotkeySpec(uint Modifiers, uint VirtualKey, Action Action, bool AllowRepeat, uint BaseModifiers, int Priority = 0, string Hotkey = "");

    // Track repeatable hotkey IDs
    private readonly HashSet<int> _repeatableIds = new();
    // Throttle ALL hotkeys: minimum interval between successive fires of the same ID.
    // RegisterHotKey floods WM_HOTKEY on OS key-repeat (~30ms). AHK's Hotkey() hook
    // naturally fires once-per-press; this throttle emulates that behavior.
    // The actual delay comes from the user-configurable AppSettings.CycleDelayMs
    // (default 100); fallback to 100 if settings haven't been wired yet.
    private int CycleDelayMs => _appSettings?.CycleDelayMs ?? 100;
    // When false, a held cycle hotkey cycles exactly once per press (issue #59).
    private bool CycleWhileHeld => _appSettings?.CycleWhileHeld ?? true;
    private readonly Dictionary<int, long> _lastRepeatFireTick = new();
    // When each WM_HOTKEY was POSTED (whether or not we acted on it). The gap between
    // posts tells a held key's OS auto-repeat train (~30ms apart) from a fresh tap, so
    // the train can be paced to CycleDelayMs while a tap still fires instantly (#60).
    private readonly Dictionary<int, long> _lastHotkeyPostTick = new();

    // Held-to-cycle is driven by the OS key-repeat WM_HOTKEY stream: cycle hotkeys are
    // registered WITHOUT MOD_NOREPEAT, so Windows re-posts while the key is physically
    // held and stops the moment it is released. No self-running repeat timer exists, so
    // cycling can never outlive the key press (see the note at the registration site).

    // ── Mouse button hotkey support (WH_MOUSE_LL) ──
    // The low-level mouse hook runs on a DEDICATED thread with its own message
    // loop — NOT the WPF UI thread. A WH_MOUSE_LL callback is dispatched on the
    // installing thread, and every system-wide mouse event serializes through it;
    // installing it on the UI thread meant thumbnail compositing / timers stalled
    // mouse input and the whole desktop's cursor stuttered while boxing. The
    // matched button action and the repeat timer are still marshaled to the UI
    // dispatcher; only the fast suppress/pass-through decision runs on this thread.
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private System.Threading.Thread? _mouseHookThread;
    private uint _mouseHookThreadId;
    // Signaled once the hook thread has published its native thread id, so a
    // remove that races a just-started thread can always post WM_QUIT to it.
    private readonly System.Threading.ManualResetEventSlim _mouseHookReady = new(false);
    // Copy-on-write: the hook thread reads this while the UI thread changes it, so a change builds a new list and
    // swaps the reference; the callback takes one snapshot per event and never sees a list being modified.
    private volatile List<MouseButtonBinding> _mouseBindings = new();
    private readonly List<MouseButtonBinding> _storedMouseBindings = new(); // persist across suspend
    private record MouseButtonBinding(uint Modifiers, string ButtonName, Action Action, bool AllowRepeat);

    // The pause key when it is a mouse button: the one binding the mouse hook keeps while paused, so the button
    // that paused can also resume (IDEAS P24). Null for a keyboard pause key or none.
    private MouseButtonBinding? _suspendMouseBinding;

    // ── Hide hotkey keystrokes from the clients (WH_KEYBOARD_LL, #108) ──
    // RegisterHotKey swallows a hotkey's key-DOWN but NOT its key-UP: the up goes to
    // whatever is foreground when the key is released, and after a client-switching
    // hotkey that is the client we just switched INTO. Every Ctrl+TAB cycle therefore
    // dropped an orphan "Ctrl+TAB up" on the incoming client, right before the user's
    // first click - which only focused EVE's overview instead of locking the target.
    // AutoHotkey, and so EVE-X-Preview, hides BOTH halves with its keyboard hook
    // (AHK hotkey.cpp / hook.cpp). This does the same: a real keystroke that lands on
    // one of our registered slots has its down AND up swallowed here, and we post the
    // same WM_HOTKEY our window already handles, so scope, the Settings block,
    // throttling and repeat pacing are all unchanged. It must be both halves or
    // neither - swallowing only the up leaves the key logically stuck down
    // system-wide (verified). RegisterHotKey stays registered underneath, so injected
    // keys, and everything if Windows ever drops this hook, keep today's path.
    private record KeySlot(int Id, bool Repeat);
    private volatile Dictionary<(uint Mods, uint Vk), KeySlot>? _keyHookMap; // published by the UI thread
    private System.Threading.Thread? _keyHookThread;
    private uint _keyHookThreadId;
    private readonly System.Threading.ManualResetEventSlim _keyHookReady = new(false);

    // -- Hook watchdog (IDEAS #41) --
    // Windows silently removes a low-level hook whose callback runs past LowLevelHooksTimeout. Each callback stamps the
    // time, its only extra work; HookWatchdog compares the stamps with input Windows saw, and a missing hook is put back
    // through the same path pause and resume use. Everything but the stamps runs on the UI thread.
    private volatile int _mouseHookTick;      // Environment.TickCount at the mouse hook's last call, or its install
    private volatile int _keyHookTick;        // the same for the keyboard hook
    private int _keyHookInstalledTick;
    private int _mouseHookGeneration;         // bumps whenever this service installs or removes the mouse hook
    private readonly HookWatchdog _mouseWatchdog = new(), _keyWatchdog = new();
    private System.Windows.Threading.DispatcherTimer? _watchdogTimer;
    private bool _disposed;
    private bool _mouseLimitNoted, _keyLimitNoted;   // each hook's "limit reached" note is written once
    private int _keyHookGeneration;           // bumps whenever this service installs or removes the keyboard hook
    private IntPtr _hooksSeeHwnd;             // the last foreground window asked about, and the answer
    private bool _hooksSeeInput;
    private IntPtr _hooksSeeCursorHwnd;       // the same for the top-level window under the cursor
    private bool _hooksSeeCursor;
    private IntPtr _hotkeyHwnd;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_UP = 0x80;
    // Marks a WM_HOTKEY posted by the hook (lParam bit 15 is never set by Windows,
    // whose lParam is modifiers | vk<<16), purely so the cycling log can say so.
    private const int HiddenKeystrokeMark = 0x8000;

    // Mouse buttons don't have OS-level auto-repeat the way keyboard keys do —
    // holding XButton2 only fires a single WM_XBUTTONDOWN. For repeatable
    // bindings (cycle hotkeys) we run our own repeat: when the hook sees a
    // matching DOWN event we start a DispatcherTimer that re-fires the action
    // until the corresponding UP event arrives.
    // Both the initial repeat delay and the steady-state interval read from
    // the user-configurable CycleDelayMs setting at the moment the timer arms,
    // so changes via Settings take effect on the next button press.
    private System.Windows.Threading.DispatcherTimer? _mouseRepeatTimer;
    private string? _mouseRepeatButton;            // "XBUTTON1" / "XBUTTON2" / "MBUTTON" while repeating, else null
    private MouseButtonBinding? _mouseRepeatBinding;

    // Events for external wiring
    public event Action? SuspendToggled;

    public bool IsSuspended => _suspended;

    /// <summary>The buttons the mouse hook acts on now: every mouse binding, or while paused only the pause key (tests).</summary>
    public IReadOnlyList<string> HookedMouseButtons => _mouseBindings.Select(b => b.ButtonName).ToList();

    /// <summary>The open settings window (App sets it when settings opens and clears it when it closes). Hotkeys don't
    /// fire while it is in front; its title is translated, so the English-title match alone missed it (IDEAS P23).</summary>
    public IntPtr SettingsWindowHandle { get; set; }

    /// <summary>The open command palette (IDEAS #14): App sets its handle while it shows and clears it when it closes. While it
    /// is the foreground window the other hotkeys stay quiet, as they do for the settings window, so a letter or a cycle key
    /// typed into it switches no client; only its own key still gets through, to close it.</summary>
    public IntPtr CommandPaletteWindowHandle { get; set; }

    /// <summary>The foreground window as the palette check reads it (tests hand in another).</summary>
    public Func<IntPtr> ForegroundWindow { get; set; } = User32.GetForegroundWindow;

    private Action? _paletteKeyAction;   // the palette key's own action, to tell it from the others

    /// <summary>Whether a hotkey is held back: the palette is in front and the key is not the palette's own.</summary>
    public static bool PaletteBlocks(bool paletteInFront, bool isPaletteKey) => paletteInFront && !isPaletteKey;

    private bool PaletteInFront()
    {
        var palette = CommandPaletteWindowHandle;
        return palette != IntPtr.Zero && ForegroundWindow() == palette;
    }

    /// <summary>Hotkeys stay quiet while a MultiPreview settings window is in front (matches AHK): the open settings
    /// window by its handle, or a MultiPreview window whose title says "Settings" (the old window's title is English in
    /// every language).</summary>
    public static bool IsSettingsForeground(IntPtr foreground, IntPtr settingsWindow, string? title, string? processName) =>
        IsSettingsForegroundLazy(foreground, settingsWindow, () => title, () => processName);

    /// <summary>Same answer as the overload above, in the cheapest order: the handle first, then (only while settings is
    /// open) whether the settings window owns the window in front, then the title, and the process lookup (a system
    /// process snapshot) only when the title says "Settings". This runs when a hotkey fires or a bound mouse button is
    /// pressed, so the providers are only called when needed.</summary>
    /// <param name="rootOwner">The window that ultimately owns <paramref name="foreground"/> (GetAncestor GA_ROOTOWNER).
    /// The settings window's own dialogs (a question, the colour picker, the hotkey capture, a name box) have translated
    /// titles; their owner is the settings window in every language (IDEAS P23).</param>
    public static bool IsSettingsForegroundLazy(IntPtr foreground, IntPtr settingsWindow, Func<string?> title, Func<string?> processName,
        Func<IntPtr>? rootOwner = null)
    {
        if (foreground == IntPtr.Zero) return false;
        if (foreground == settingsWindow) return true;
        if (settingsWindow != IntPtr.Zero && rootOwner != null && rootOwner() == settingsWindow) return true;
        var t = title();
        return t != null && t.Contains("Settings", StringComparison.OrdinalIgnoreCase) && User32.IsAppProcessName(processName());
    }

    private bool SettingsInFront(IntPtr foreground) =>
        IsSettingsForegroundLazy(foreground, SettingsWindowHandle, () => User32.GetWindowTitle(foreground), () => User32.GetProcessName(foreground),
            () => User32.GetAncestor(foreground, User32.GA_ROOTOWNER));

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
        _hotkeyHwnd = _hwndSource.Handle;

        RegisterActivationHotkey();

        // IDEAS #41: the mouse hook is looked at every 2 s; the keyboard hook is judged when a hotkey arrives (WndProc).
        _watchdogTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HookWatchdog.MouseLookMs) };
        _watchdogTimer.Tick += (_, _) => CheckMouseHook();
        _watchdogTimer.Start();
    }

    /// <summary>
    /// Register the internal vk0xE8 hotkey used as the foreground-rights bridge.
    ///
    /// Why this exists: When a cycle hotkey is bound to a mouse button (XButton1/2,
    /// MButton), it's caught via WH_MOUSE_LL and dispatched through BeginInvoke.
    /// In that dispatch context our process has not "received the last input
    /// event" (the hook suppressed it system-wide), so SetForegroundWindow
    /// silently fails — the cycle's internal state advances and the highlight
    /// border updates, but the EVE client never actually comes to front.
    ///
    /// AHK Main_Class works around this by SendInput-ing an unused virtual key
    /// (vk0xE8) and RegisterHotKey-ing the same vk so the synthesized keystroke
    /// fires WM_HOTKEY in our message thread. That dispatch arrives with
    /// foreground-activation rights, which we use to call SetForegroundWindow
    /// on the queued PendingActivateHwnd. We mirror that here, and do exactly what EVE-X-Preview does in the handler:
    /// SetForegroundWindow (a second time if the first is refused) and no restore or z-order call; ActivateWindow does not
    /// queue a minimized client, and its caller restores that one.
    /// </summary>
    private void RegisterActivationHotkey()
    {
        if (_hwndSource == null) return;

        int before = _activationHotkeyIds.Count;
        _activationHotkeyIds.AddRange(RegisterUnderEveryModifier((mods, bridge) => Register(mods, User32.VK_ACTIVATION, bridge), () =>
        {
            var target = User32.PendingActivateHwnd;
            if (target == IntPtr.Zero) return;
            User32.PendingActivateHwnd = IntPtr.Zero;

            try
            {
                // EVE-X-Preview's handler: SetForegroundWindow, once more if it was refused. Nothing else.
                if (!User32.SetForegroundWindow(target))
                    User32.SetForegroundWindow(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Hotkey:Activation] ❌ vk0xE8 SetForegroundWindow failed: {ex.Message}");
            }
        }));

        int registered = _activationHotkeyIds.Count - before;
        if (registered > 0)
        {
            Debug.WriteLine($"[Hotkey:Activation] 🔑 vk0xE8 internal activation hotkey registered under {registered}/16 modifier combinations");
        }
        else
        {
            Debug.WriteLine("[Hotkey:Activation] ⚠ Failed to register vk0xE8 — mouse-hook activations may not bring EVE clients to front when our process lacks foreground rights");
        }
    }

    /// <summary>Registers the bridge under every mix of Ctrl, Shift, Alt and Win (16, none included), as EVE-X-Preview
    /// does (src/Main_Class.ahk lines 54-56). RegisterHotKey matches modifiers exactly: with only the bare key, a held
    /// modifier let the vk0xE8 press reach the window in front and the bridge's WM_HOTKEY never came. Every combination runs
    /// <paramref name="bridge"/>; the ids that registered are returned.</summary>
    public static List<int> RegisterUnderEveryModifier(Func<uint, Action, int> register, Action bridge)
    {
        var ids = new List<int>();
        const uint every = User32.MOD_ALT | User32.MOD_CONTROL | User32.MOD_SHIFT | User32.MOD_WIN;   // 0xF
        for (uint mods = 0; mods <= every; mods++)
        {
            int id = register(mods, bridge);
            if (id > 0) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Register a global hotkey.</summary>
    /// <returns>Hotkey ID, or -1 on failure.</returns>
    public int Register(uint modifiers, uint key, Action action, bool allowRepeat = false)
    {
        if (_hwndSource == null) return -1;

        int id = _nextId++;
        uint finalMods = allowRepeat ? modifiers : (modifiers | User32.MOD_NOREPEAT);
        if (User32.RegisterHotKey(_hwndSource.Handle, id, finalMods, key))
        {
            _hotkeyActions[id] = action;
            _hotkeyVks[id] = key;
            Debug.WriteLine($"[Hotkey:Register] ✅ Registered ID={id}, Mod=0x{modifiers:X}, Key=0x{key:X}, repeat={allowRepeat}");
            return id;
        }

        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        _lastRegisterError = err;   // IDEAS #42: the pause key is registered here, and its refusal is told
        Debug.WriteLine($"[Hotkey:Register] ❌ Failed Mod=0x{modifiers:X}, Key=0x{key:X} (err={err})");
        return -1;
    }

    /// <summary>Unregister a specific hotkey by ID.</summary>
    public void Unregister(int id)
    {
        if (_hwndSource == null) return;
        User32.UnregisterHotKey(_hwndSource.Handle, id);
        _hotkeyActions.Remove(id);
        _hotkeyVks.Remove(id);
    }

    /// <summary>Unregister all user-facing hotkeys (including suspend). The
    /// internal vk0xE8 activation hotkey is preserved — it's an infrastructure
    /// bridge for foreground rights, not a user binding, and must survive
    /// settings reloads.</summary>
    public void UnregisterAll()
    {
        // Preserve cached actions + VKs for any IDs we keep so the dispatcher can
        // still find them when WM_HOTKEY arrives.
        var preservedActions = new Dictionary<int, Action>();
        var preservedVks = new Dictionary<int, uint>();
        foreach (var id in _hotkeyActions.Keys.ToList())
        {
            if (_activationHotkeyIds.Contains(id))
            {
                preservedActions[id] = _hotkeyActions[id];
                if (_hotkeyVks.TryGetValue(id, out var vk)) preservedVks[id] = vk;
                continue;
            }
            if (_hwndSource != null) User32.UnregisterHotKey(_hwndSource.Handle, id);   // null only in tests that never Initialize()
        }
        _hotkeyActions.Clear();
        _hotkeyVks.Clear();
        foreach (var kv in preservedActions) _hotkeyActions[kv.Key] = kv.Value;
        foreach (var kv in preservedVks) _hotkeyVks[kv.Key] = kv.Value;

        _repeatableIds.Clear();
        // Don't clear _lastRepeatFireTick entries for preserved IDs (none use
        // throttle, but conceptually correct); just leave it — stale entries
        // for the unregistered IDs are harmless.
        _lastRepeatFireTick.Clear();
        _lastHotkeyPostTick.Clear();
        _storedSpecs.Clear();
        _storedMouseBindings.Clear();
        _suspendMouseBinding = null;
        RemoveMouseHook();
        _keyHookMap = null;
        _suspendHotkeyId = -1;
        _hotkeysActive = false;
        // NB: don't reset _nextId — the preserved activation-hotkey IDs would
        // collide with the next Register() call. Letting it grow monotonically
        // costs nothing (int32 has plenty of room) and keeps IDs unique.
    }

    /// <summary>Wire foreground-change events so registration can be gated on which
    /// app is in front (issue #68). <paramref name="hasWindows"/> reports whether any
    /// EVE client is currently tracked. Call once at startup.</summary>
    public void AttachWinEvents(WinEventHookService winEvents, Func<bool> hasWindows)
    {
        if (_scopeWinEvents != null) return;
        _scopeWinEvents = winEvents;
        _hasWindowsProvider = hasWindows;
        _scopeWinEvents.ForegroundChanged += OnForegroundChangedForScope;
    }

    private void OnForegroundChangedForScope(IntPtr _) => EvaluateRegistration();

    /// <summary>What <see cref="EvaluateRegistration"/> does next.</summary>
    public enum RegistrationStep { Leave, Activate, Deactivate }

    /// <summary>
    /// The registration rule, kept pure so it can be tested (issue #68):
    /// • Suspended → leave as-is (ToggleSuspend owns that state).
    /// • Capturing in the settings window → leave as-is (EndCapture restores).
    /// • No EVE clients tracked → unregister (keys work normally everywhere).
    /// • EVE-only scope → register ONLY while EVE or this app is foreground, so a
    ///   keystroke that wouldn't be actioned is never consumed.
    /// • Global scope → register whenever clients exist (capture everywhere).
    /// </summary>
    public static RegistrationStep DecideRegistration(bool suspended, bool capturing, Func<bool> hasWindows,
        bool eveOnlyScope, Func<bool> foregroundIsEveOrApp)
    {
        if (suspended || capturing) return RegistrationStep.Leave;
        if (!hasWindows()) return RegistrationStep.Deactivate;
        if (!eveOnlyScope) return RegistrationStep.Activate;
        return foregroundIsEveOrApp() ? RegistrationStep.Activate : RegistrationStep.Deactivate;
    }

    /// <summary>
    /// Single source of truth for whether the user's hotkeys should currently be
    /// registered with the OS. Drives both the foreground-change events and the
    /// safety-net poll timer. The rule is <see cref="DecideRegistration"/>.
    /// </summary>
    public void EvaluateRegistration()
    {
        var step = DecideRegistration(_suspended, IsCapturing,
            () => _hasWindowsProvider?.Invoke() ?? true,
            _eveOnlyScope,
            () =>
            {
                try { return User32.IsEveOrAppProcess(User32.GetProcessName(User32.GetForegroundWindow())); }
                catch { return false; }
            });
        if (step == RegistrationStep.Activate) ActivateHotkeys();
        else if (step == RegistrationStep.Deactivate) DeactivateHotkeys();
    }

    /// <summary>Register all stored non-suspend hotkeys. Call when EVE windows appear.</summary>
    public void ActivateHotkeys()
    {
        if (_hotkeysActive || _suspended || _captureDepth > 0 || _hwndSource == null)
        {
            return;
        }
        // Resolve collisions before registering: multiple stored specs may target
        // the same (VK, final-mods) slot because every stored hotkey exhaustively
        // expands over all 16 modifier combinations. Without this pass, a generic
        // "Ctrl+1" binding would claim the Ctrl+Alt+1 slot and block a specific
        // "Ctrl+Alt+1" binding from ever firing. Specificity (more user-specified
        // modifier bits) wins each slot.
        var winners = new Dictionary<(uint Mods, uint Vk), HotkeySpec>();
        foreach (var spec in _storedSpecs)
        {
            var key = (spec.Modifiers, spec.VirtualKey);
            int specBits = System.Numerics.BitOperations.PopCount(spec.BaseModifiers);
            if (!winners.TryGetValue(key, out var current))
            {
                winners[key] = spec;
            }
            else
            {
                int curBits = System.Numerics.BitOperations.PopCount(current.BaseModifiers);
                // Specificity wins FIRST: a deliberately-modified binding (e.g. Ctrl+Alt+1)
                // must keep its slot against a plain binding that wildcard-expands into it.
                // Only on EQUAL specificity does priority break the tie, so a character/group
                // cycle beats the global "cycle all" on a shared key (issue #75).
                if (specBits > curBits || (specBits == curBits && spec.Priority > current.Priority))
                    winners[key] = spec;
            }
        }

        int ok = 0, fail = 0;
        var refusedNow = new List<RefusedSlot>();   // IDEAS #42
        // Only slots that actually registered - a key another app owns is not ours to hide.
        var hiddenSlots = new Dictionary<(uint Mods, uint Vk), KeySlot>();
        foreach (var spec in winners.Values)
        {
            int id = _nextId++;
            // Held-to-cycle is driven by the OS key-repeat WM_HOTKEY stream, so cycle
            // hotkeys are registered WITHOUT MOD_NOREPEAT. Everything else keeps it
            // (exactly one WM_HOTKEY per press).
            //
            // Why: the previous design cycled from our own timer that polled
            // GetAsyncKeyState for the release. That is the app's ONLY release sensor,
            // and it can be wrong — a remapper such as X-Mouse Button Control synthesises
            // the key and emits its key-UP from its own low-level hook. Lose that one
            // event and the key stays latched down system-wide, GetAsyncKeyState reports
            // "still held" forever, and the timer cycled on its own for the full 60s cap
            // (~600 client switches) with the user touching nothing — and every new press
            // re-armed a fresh 60s, so mashing the key to stop it only prolonged it.
            //
            // The OS repeat stream cannot do that: it is produced by the physical key
            // being held, stops the instant the key is released, and an injected key that
            // never repeats simply yields one WM_HOTKEY. Runaway cycling becomes
            // structurally impossible, so no timer and no safety cap are needed.
            bool osDrivesHold = spec.AllowRepeat && CycleWhileHeld;
            uint finalMods = osDrivesHold ? spec.Modifiers : (spec.Modifiers | User32.MOD_NOREPEAT);
            if (User32.RegisterHotKey(_hwndSource.Handle, id, finalMods, spec.VirtualKey))
            {
                _hotkeyActions[id] = spec.Action;
                _hotkeyVks[id] = spec.VirtualKey;
                if (spec.AllowRepeat)
                    _repeatableIds.Add(id);
                hiddenSlots[(spec.Modifiers & 0xF, spec.VirtualKey)] = new KeySlot(id, osDrivesHold);
                ok++;
            }
            else
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();   // first: the next call to Windows replaces it
                fail++;
                // IDEAS #42: every refused slot with what Windows said. HotkeyRefusals.Told picks the ones worth
                // telling: the combination the player bound, and not the slot the pause key itself holds.
                refusedNow.Add(new RefusedSlot(spec.Hotkey, spec.Modifiers, spec.BaseModifiers, spec.VirtualKey, error));
            }
        }
        // The keyboard hook hides hotkey keystrokes from the clients.
        bool hideHotkeys = _appSettings?.HideHotkeyKeystrokes != false && hiddenSlots.Count > 0;
        if (hideHotkeys)
        {
            EnsureKeyHook();
            _keyHookMap = hiddenSlots;
        }
        else
        {
            RemoveKeyHook();
        }
        _hotkeysActive = true;
        // IDEAS #42: raises Changed only when the list is not what it was
        Refused.Registered(HotkeyRefusals.Told(refusedNow, _suspendHotkeyId > 0 ? _suspendSlot : null));
        App.PerfLog($"[Hotkey:Activate] Registered {ok}/{_storedSpecs.Count} specs ({fail} failed, {_hotkeyActions.Count} total actions)");
    }

    /// <summary>Unregister all non-suspend hotkeys. Call when last EVE window closes.</summary>
    public void DeactivateHotkeys()
    {
        if (!_hotkeysActive || _hwndSource == null) return;
        foreach (var id in _hotkeyActions.Keys.ToList())
        {
            if (id == _suspendHotkeyId) continue;
            if (_activationHotkeyIds.Contains(id)) continue;
            User32.UnregisterHotKey(_hwndSource.Handle, id);
            _hotkeyActions.Remove(id);
            _hotkeyVks.Remove(id);
        }
        _repeatableIds.Clear();
        _lastRepeatFireTick.Clear();
        _lastHotkeyPostTick.Clear();
        _keyHookMap = null; // nothing registered -> nothing to hide; keys pass straight through
        _hotkeysActive = false;
        Debug.WriteLine("[Hotkey:Deactivate] ⏸ Deactivated hotkeys (no EVE windows)");
    }

    /// <summary>Toggle suspend state — properly releases all hooks back to Windows.</summary>
    public void ToggleSuspend()
    {
        _suspended = !_suspended;

        if (_suspended)
        {
            // Suspend: unregister all keyboard hotkeys EXCEPT the suspend toggle
            // and the internal vk0xE8 activation hotkey.
            if (_hwndSource != null)
            {
                foreach (var id in _hotkeyActions.Keys.ToList())
                {
                    if (id == _suspendHotkeyId) continue;
                    if (_activationHotkeyIds.Contains(id)) continue;
                    User32.UnregisterHotKey(_hwndSource.Handle, id);
                    _hotkeyActions.Remove(id);
                    _hotkeyVks.Remove(id);
                }
            }
            _hotkeysActive = false;

            // Inputs go back to Windows: the keyboard hook goes, and the mouse hook keeps only a pause key on a
            // mouse button, so that button can resume (IDEAS P24).
            RemoveKeyHook();
            ReinstallMouseBindings();

            Debug.WriteLine("[Hotkey:Suspend] ⏸ All hotkeys unregistered, hooks removed — keys returned to Windows");
        }
        else
        {
            // Resume: re-register stored hotkeys, but honor EVE-only scope +
            // current foreground so we don't re-introduce the consume-not-action
            // behavior while a non-EVE app is in front (issue #68).
            EvaluateRegistration();

            // Re-install hooks
            ReinstallMouseBindings();

            Debug.WriteLine("[Hotkey:Suspend] ▶ All hotkeys re-registered, hooks restored");
        }

        SuspendToggled?.Invoke();
        Debug.WriteLine($"[Hotkey:Scope] ⏸ Suspend: {_suspended}");
    }

    /// <summary>True while the settings window's hotkey dialog is open.</summary>
    public bool IsCapturing => _captureDepth > 0;

    /// <summary>Release every hotkey and hook (the vk0xE8 activation bridge stays) until the
    /// matching <see cref="EndCapture"/>. Stored specs and mouse bindings are kept.</summary>
    public void BeginCapture()
    {
        if (_captureDepth++ > 0) return;
        if (_hwndSource != null)
        {
            foreach (var id in _hotkeyActions.Keys.ToList())
            {
                if (_activationHotkeyIds.Contains(id)) continue;
                User32.UnregisterHotKey(_hwndSource.Handle, id);
                _hotkeyActions.Remove(id);
                _hotkeyVks.Remove(id);
            }
        }
        _suspendHotkeyId = -1;
        _repeatableIds.Clear();
        _lastRepeatFireTick.Clear();
        _lastHotkeyPostTick.Clear();
        _hotkeysActive = false;
        RemoveMouseHook();
        RemoveKeyHook();
    }

    /// <summary>Undo <see cref="BeginCapture"/>: the suspend key, the mouse bindings (while paused only a
    /// pause key on a mouse button) and the foreground-gated registration all come back as they were.</summary>
    public void EndCapture()
    {
        if (_captureDepth == 0 || --_captureDepth > 0) return;
        RestoreSuspendHotkey();
        ReinstallMouseBindings();
        EvaluateRegistration();
    }

    /// <summary>A keyboard suspend key is registered directly; a mouse-button one lives in the
    /// stored mouse bindings and comes back with them.</summary>
    private void RestoreSuspendHotkey()
    {
        if (_suspendHotkeyId > 0 || string.IsNullOrWhiteSpace(_suspendAhk)) return;
        if (IsMouseButton(ExtractModsAndKey(_suspendAhk).KeyPart)) return;
        _suspendHotkeyId = RegisterAhkHotkey(_suspendAhk, () => ToggleSuspend());
        NoteSuspendKey();
    }

    /// <summary>
    /// Register all hotkeys from settings. Call from UI thread after services are wired.
    /// </summary>
    public void RegisterFromSettings(AppSettings settings, Profile profile,
        ThumbnailManager thumbnailManager, Action openSettings)
    {
        UnregisterAll();
        // IDEAS #42: what Windows refused stays listed until the registration that follows replaces it. Only where
        // none follows (paused, or no EVE client) is it forgotten: before the pause key is noted again, below.
        if (HotkeyRefusals.ForgottenAtASettingsChange(_suspended, _hasWindowsProvider?.Invoke() ?? true)) Refused.Forget();

        Debug.WriteLine($"[Hotkey:Settings] Profile hotkeys: {profile.Hotkeys.Count}, Groups: {profile.HotkeyGroups.Count}");
        foreach (var (name, binding) in profile.Hotkeys)
            Debug.WriteLine($"[Hotkey:Settings]   char='{name}' key='{binding.Key}'");

        // Store settings ref for TOS guard
        _appSettings = settings;

        // Set EVE Only scope from settings
        _eveOnlyScope = !settings.GlobalHotkeys;
        Debug.WriteLine($"[Hotkey:Scope] \uD83D\uDD27 EVE Only scope: {_eveOnlyScope}");

        // Suspend hotkey. In capture mode a keyboard suspend key waits for EndCapture (a
        // mouse one is stored with the other mouse bindings; EnsureMouseHook waits too).
        _suspendAhk = settings.SuspendHotkey;
        _suspendHotkeyId = IsCapturing && !IsMouseButton(ExtractModsAndKey(_suspendAhk).KeyPart)
            ? -1
            : RegisterSuspendKey(settings.SuspendHotkey);
        NoteSuspendKey();

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

        // Hide/Show crops only (issue #66). Routed through ThumbnailManager so
        // HotkeyService needs no direct CropManager reference.
        StoreAhkHotkey(settings.HideShowCropsHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.ToggleCropsVisibility();
        });

        // Optional global layout undo/redo (unbound by default; pick non-EVE keys).
        StoreAhkHotkey(settings.UndoLayoutHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.UndoLayout();
        });
        StoreAhkHotkey(settings.RedoLayoutHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.RedoLayout();
        });

        StoreAhkHotkey(settings.CloseAllClientsHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.CloseAllClients();
        });

        // Lock thumbnail positions toggle (issue #10)
        StoreAhkHotkey(settings.LockPositionsHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.ToggleLockPositions();
        });

        // Global cycle across ALL tracked clients, profile/group-agnostic (issue #9)
        StoreRepeatableAhkHotkey(settings.GlobalCycleForwardHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.CycleAll(forward: true);
        });
        StoreRepeatableAhkHotkey(settings.GlobalCycleBackwardHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.CycleAll(forward: false);
        });

        // Back to the client used before this one (IDEAS #56). One press, one switch: not repeatable.
        StoreAhkHotkey(settings.PreviousClientHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.SwitchToPreviousClient();
        });

        // To the client behind the alert that needs the player (IDEAS #17). One press, one client: not repeatable.
        StoreAhkHotkey(settings.GoToLastAlertHotkey, () =>
        {
            if (_suspended) return;
            thumbnailManager.GoToLastAlert();
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

        // Do-not-disturb (IDEAS #32): alert sounds off or on again. It switches no client.
        StoreAhkHotkey(settings.DoNotDisturbHotkey, () =>
        {
            if (_suspended) return;
            DoNotDisturbToggled?.Invoke();
        });

        // The hotkey cheat sheet (IDEAS #16). It switches no client, so it is no cycle-ignore key.
        StoreAhkHotkey(settings.HotkeySheetHotkey, () =>
        {
            if (_suspended) return;
            HotkeySheetToggled?.Invoke();
        });

        // Focus mode on or off (IDEAS #35). It switches no client, so it is no cycle-ignore key.
        StoreAhkHotkey(settings.FocusModeHotkey, () =>
        {
            if (_suspended) return;
            FocusModeToggled?.Invoke();
        });

        // The settings window (IDEAS P5): App.OpenSettings opens it, or raises it when it is open. It switches no client,
        // so it is no cycle-ignore key.
        StoreAhkHotkey(settings.OpenSettingsHotkey, () =>
        {
            if (_suspended) return;
            openSettings();
        });

        // The command palette (IDEAS #14): App shows it, or closes it when it is open. The key itself switches no client (the
        // row chosen in the palette does, through the same runner as a command from another program), so it is no cycle-ignore key.
        Action palette = () =>
        {
            if (_suspended) return;
            CommandPaletteRequested?.Invoke();
        };
        _paletteKeyAction = palette;
        StoreAhkHotkey(settings.CommandPaletteHotkey, palette);

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
                }, priority: SPECIFIC_PRIORITY);
            }
            else
            {
                StoreRepeatableAhkHotkey(hotkeyStr, () =>
                {
                    if (_suspended) return;
                    thumbnailManager.CycleGroup(hotkeyStr + "_shared", capturedMembers, forward: true);
                }, priority: SPECIFIC_PRIORITY);
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
            }, priority: SPECIFIC_PRIORITY);
        }

        // Store merged backward hotkeys
        foreach (var (hotkeyStr, mergedMembers) in bwdMerged)
        {
            var members = mergedMembers;
            StoreRepeatableAhkHotkey(hotkeyStr, () =>
            {
                if (_suspended) return;
                thumbnailManager.CycleGroup(hotkeyStr + "_bwd", members, forward: false);
            }, priority: SPECIFIC_PRIORITY);
        }

        App.PerfLog($"[Hotkey:Register] Stored {_storedSpecs.Count} specs, suspend ID={_suspendHotkeyId} (eveOnly={_eveOnlyScope})");
    }

    // Events for profile cycling
    public event Action? ProfileCycleForward;
    public event Action? ProfileCycleBackward;

    /// <summary>The do-not-disturb hotkey was pressed (IDEAS #32). App flips it and says so.</summary>
    public event Action? DoNotDisturbToggled;

    /// <summary>The hotkey cheat sheet's key was pressed (IDEAS #16). App opens the sheet, or closes it when it is open.</summary>
    public event Action? HotkeySheetToggled;

    /// <summary>Focus mode's key was pressed (IDEAS #35). App turns focus mode on or off and says so.</summary>
    public event Action? FocusModeToggled;

    /// <summary>The command palette's key was pressed (IDEAS #14). App opens the palette, or closes it when it is open.</summary>
    public event Action? CommandPaletteRequested;

    // ── Hotkeys Windows refused (IDEAS #42) ──
    /// <summary>The hotkeys Windows refused the last time they were registered, and whether they have been registered
    /// at all. Settings › Clients › Hotkeys lists them; App says so once in the tray.</summary>
    public RefusedHotkeyList Refused { get; } = new();

    private int _lastRegisterError;

    // The combination the pause key was registered with (its modifiers and its key), or null: no pause key, one on
    // a mouse button, or one Windows refused. It counts only while _suspendHotkeyId > 0. A second binding of that
    // combination is refused by MultiPreview's own doing, not by another program (HotkeyRefusals.Told).
    private (uint Modifiers, uint VirtualKey)? _suspendSlot;

    /// <summary>Whether Windows refused the pause key just now. A mouse button is never refused (its id is -2), an
    /// empty or unreadable key was never asked for, capture mode leaves it unregistered on purpose, and without a
    /// window (tests) nothing was asked. It also keeps the slot a registered pause key holds.</summary>
    private void NoteSuspendKey()
    {
        var pause = ParseAhkHotkeyString(_suspendAhk ?? "");
        _suspendSlot = _suspendHotkeyId > 0 ? pause : null;
        bool refused = _suspendHotkeyId == -1 && _hwndSource != null && !IsCapturing
                       && pause.VirtualKey != 0;
        Refused.PauseKey(refused ? new RefusedHotkey(_suspendAhk ?? "", _lastRegisterError) : null);
    }

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
            // RegisterAhkHotkey is used for one-shot bindings (Suspend toggle).
            // No auto-repeat — single-fire only.
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action, allowRepeat: false);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Registered mouse binding: {ahkKeyString}");
            return -2; // Special ID for mouse bindings
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        Debug.WriteLine($"[Hotkey:Parse] 🔧 ParseAhk '{ahkKeyString}' → Mod=0x{parsedMods:X}, VK=0x{vk:X}");
        if (vk == 0) return -1;

        return Register(parsedMods, vk, action);
    }

    /// <summary>The pause key. A keyboard key is registered as before; a mouse button joins the mouse bindings as the
    /// one the hook keeps while paused, so the button that paused can also resume (IDEAS P24).</summary>
    private int RegisterSuspendKey(string ahkKeyString)
    {
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString ?? "");
        if (!IsMouseButton(keyPart)) return RegisterAhkHotkey(ahkKeyString ?? "", () => ToggleSuspend());
        AddMouseBinding(mods, keyPart.ToUpperInvariant(), () => ToggleSuspend(), allowRepeat: false, isSuspend: true);
        return -2;   // RegisterAhkHotkey's id for a mouse binding
    }

    /// <summary>Store a hotkey spec without registering it. Will be registered when ActivateHotkeys is called.
    /// <paramref name="priority"/> wins (Modifiers,VK) collisions — use SPECIFIC_PRIORITY for
    /// character/group-specific bindings so they beat the global cycle on a shared key (issue #75).</summary>
    public void StoreAhkHotkey(string ahkKeyString, Action action, int priority = 0)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return;

        // Mouse buttons go through mouse hook, not RegisterHotKey
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString);
        if (IsMouseButton(keyPart))
        {
            // Single-fire binding — character activation, toggle, etc. No repeat.
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action, allowRepeat: false);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Stored mouse binding: {ahkKeyString}");
            return;
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        if (vk == 0)
        {
            Debug.WriteLine($"[Hotkey:Store] ⚠️ Failed to parse '{ahkKeyString}' — VK=0");
            return;
        }

        Debug.WriteLine($"[Hotkey:Store] 📦 Storing '{ahkKeyString}' → Mod=0x{parsedMods:X}, VK=0x{vk:X} (16 wildcard combos)");

        // Wildcard expansion emulates AHK's *hotkey: the binding fires even while
        // the user holds a modifier. EVE players depend on that (Ctrl held to lock).
        // Its cost is that a bare key also claims Ctrl+<key>, blocking paste (#105),
        // so it can be turned off for exact matching - m=0 is the exact combination.
        uint combos = _appSettings?.HotkeyWildcardModifiers == false ? 1u : 16u;
        for (uint m = 0; m < combos; m++)
        {
            _storedSpecs.Add(new HotkeySpec(parsedMods | m, vk, action, false, parsedMods, priority, ahkKeyString));
        }
    }

    /// <summary>Store a repeatable hotkey spec without registering it.
    /// <paramref name="priority"/> wins (Modifiers,VK) collisions (see <see cref="StoreAhkHotkey"/>).</summary>
    public void StoreRepeatableAhkHotkey(string ahkKeyString, Action action, int priority = 0)
    {
        if (string.IsNullOrWhiteSpace(ahkKeyString)) return;

        // Mouse buttons go through mouse hook
        var (mods, keyPart) = ExtractModsAndKey(ahkKeyString);
        if (IsMouseButton(keyPart))
        {
            // Repeatable binding — cycle hotkey. Held button auto-repeats via
            // the mouse-hook timer (mouse buttons have no OS-level repeat).
            AddMouseBinding(mods, keyPart.ToUpperInvariant(), action, allowRepeat: true);
            Debug.WriteLine($"[Hotkey:Mouse] 🖱️ Stored repeatable mouse binding: {ahkKeyString}");
            return;
        }

        var (parsedMods, vk) = ParseAhkHotkeyString(ahkKeyString);
        if (vk == 0) return;
        
        // Wildcard expansion emulates AHK's *hotkey: the binding fires even while
        // the user holds a modifier. EVE players depend on that (Ctrl held to lock).
        // Its cost is that a bare key also claims Ctrl+<key>, blocking paste (#105),
        // so it can be turned off for exact matching - m=0 is the exact combination.
        uint combos = _appSettings?.HotkeyWildcardModifiers == false ? 1u : 16u;
        for (uint m = 0; m < combos; m++)
        {
            _storedSpecs.Add(new HotkeySpec(parsedMods | m, vk, action, true, parsedMods, priority, ahkKeyString));
        }
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

        // Scancode form: "SC###" (hex). Used by AHK for hardware keys that have
        // no Windows-level name (e.g. the backtick/tilde key on some layouts
        // shows up as SC029). We resolve it through MapVirtualKey so the caller
        // can still RegisterHotKey on the resulting VK.
        if (key.Length > 2 && key[0] == 'S' && key[1] == 'C' &&
            uint.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint scanCode))
        {
            uint mappedVk = User32.MapVirtualKey(scanCode, User32.MAPVK_VSC_TO_VK);
            if (mappedVk != 0) return mappedVk;
        }

        // AHK's vkNN form (hex): any key, including ones with no name below (F13–F24 sent by keyboard software,
        // media and vendor keys). The settings window stores a key it can't name this way (IDEAS P22).
        if (key.Length is 3 or 4 && key[0] == 'V' && key[1] == 'K' &&
            uint.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint vkCode) &&
            vkCode is >= 0x08 and <= 0xFE)
            return vkCode;

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
            // OEM keys — accept the AHK long names, the bare punctuation
            // glyph (so KeyToAhkName's round-trip succeeds), and the layout-
            // agnostic Win32 "OemN" names (matches EVE-O-Preview's manual
            // workaround for international keyboards: Swiss QWERTZ users can
            // bind their § key by editing the config to "Oem2", since WPF
            // captures the §-key as Key.OemQuestion → "/"). See issue #28.
            "SEMICOLON" or "OEMSEMICOLON" or "OEM1" or ";" or "SC" => 0xBA,
            "EQUALS" or "EQUAL" or "OEMPLUS" or "=" => 0xBB,
            "COMMA" or "OEMCOMMA" or "," => 0xBC,
            "MINUS" or "HYPHEN" or "OEMMINUS" or "-" => 0xBD,
            "PERIOD" or "DOT" or "OEMPERIOD" or "." => 0xBE,
            "SLASH" or "OEMQUESTION" or "OEM2" or "/" => 0xBF,
            "BACKQUOTE" or "TILDE" or "OEMTILDE" or "OEM3" or "`" or "~" => 0xC0,
            "LBRACKET" or "OEMOPENBRACKETS" or "OEM4" or "[" => 0xDB,
            "BACKSLASH" or "OEMPIPE" or "OEM5" or "\\" => 0xDC,
            // VK_OEM_102 (0xE2) — the EXTRA key ISO keyboards place next to left
            // Shift (the <>| / extra-backslash key). WPF reports it as
            // Key.OemBackslash, distinct from OemPipe (0xDC) above. Without this
            // it parsed to VK 0 and silently failed to register (issue #67).
            "OEMBACKSLASH" or "OEM102" or "OEM_102" => 0xE2,
            "RBRACKET" or "OEMCLOSEBRACKETS" or "OEM6" or "]" => 0xDD,
            "QUOTE" or "OEMQUOTES" or "OEM7" or "'" => 0xDE,
            // VK_OEM_8 — country-specific (e.g. ° on Swiss French, Function
            // key modifier on some laptops). No clean glyph alias.
            "OEM8" => 0xDF,
            _ => 0
        };
    }

    /// <summary>Windows message handler — dispatches WM_HOTKEY to registered actions.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            // IDEAS #41: a hotkey whose keystroke the keyboard hook should have hidden arrived the ordinary way.
            if ((lParam.ToInt64() & HiddenKeystrokeMark) == 0) CheckKeyHook(id, User32.GetMessageTime());
            // The internal vk0xE8 activation hotkey is a foreground-rights bridge
            // synthesized by our own ActivateWindow path. It must always fire
            // regardless of EVE-only scope, the Settings-window block, or the
            // repeat throttle — those guards exist to filter user-driven input.
            bool isActivationHotkey = _activationHotkeyIds.Contains(id);

            // ── EVE Only scope check ──
            if (_eveOnlyScope && !isActivationHotkey)
            {
                try
                {
                    var fgHwnd = User32.GetForegroundWindow();
                    string? fgProc = User32.GetProcessName(fgHwnd);
                    if (!User32.IsEveOrAppProcess(fgProc))
                    {
                        Debug.WriteLine($"[Hotkey:Blocked] 🚫 EVE Only scope blocked ID={id} (fg={fgProc})");
                        return IntPtr.Zero; // Don't fire
                    }
                }
                catch { }
            }

            // M5: Exclude settings window from hotkey activation (matches AHK)
            if (!isActivationHotkey)
            {
                try
                {
                    var fgHwnd2 = User32.GetForegroundWindow();
                    if (SettingsInFront(fgHwnd2))
                    {
                        Debug.WriteLine($"[Hotkey:Blocked] 🚫 Settings window active, blocked ID={id}");
                        return IntPtr.Zero;
                    }
                }
                catch { }
            }

            // The command palette (IDEAS #14) takes typed text like the settings window: no other hotkey while it is in front.
            if (!isActivationHotkey)
            {
                try
                {
                    bool paletteKey = _hotkeyActions.TryGetValue(id, out var keyAction) && ReferenceEquals(keyAction, _paletteKeyAction);
                    if (PaletteBlocks(PaletteInFront(), paletteKey))
                    {
                        Debug.WriteLine($"[Hotkey:Blocked] Command palette in front, blocked ID={id}");
                        return IntPtr.Zero;
                    }
                }
                catch { }
            }

            // Cycle hotkeys now receive the OS key-repeat stream (no MOD_NOREPEAT), so a
            // held key posts WM_HOTKEY every ~30ms. Collapse that train to the user's
            // CycleDelayMs cadence — but a FRESH press must always fire immediately, or
            // deliberate fast taps get swallowed (issue #60).
            //
            // "Fresh press" vs "repeat train" is told apart by how fast the OS is posting:
            // auto-repeat arrives in a continuous ~30ms train, whereas a tap is preceded
            // by a real gap. So we time the POSTS (every WM_HOTKEY) separately from the
            // FIRES (only the ones we act on).
            const long RepeatDedupMs = 30;     // absorb accidental double-posts
            const long HoldTrainGapMs = 150;   // posts closer than this = OS auto-repeat
            if (!isActivationHotkey)
            {
                long now = Environment.TickCount64;

                long postGap = _lastHotkeyPostTick.TryGetValue(id, out long lastPost)
                    ? now - lastPost
                    : long.MaxValue;
                _lastHotkeyPostTick[id] = now;

                if (postGap < RepeatDedupMs) return IntPtr.Zero;

                // Inside an OS auto-repeat train (i.e. the key is being HELD): pace it.
                if (postGap < HoldTrainGapMs && _repeatableIds.Contains(id) &&
                    _lastRepeatFireTick.TryGetValue(id, out long lastFire) &&
                    (now - lastFire) < CycleDelayMs)
                {
                    return IntPtr.Zero;
                }
                _lastRepeatFireTick[id] = now;
            }

            // (Previously had an IsKeyDown gate here to drop queued WM_HOTKEY
            // events whose trigger key was already physically released, as a
            // workaround for queue buildup when actions ran slower than the
            // OS post rate. That broke software-emulated key pulses — mouse
            // drivers that send F6 down/up rapidly would have IsKeyDown
            // return false during the "up" portion of each pulse, dropping
            // legitimate cycles. With the User32.ActivateWindow fast path
            // restored to 2.0.7 timing, queue buildup is no longer a real
            // concern and the gate is removed. _hotkeyVks is kept for
            // potential future use.)

            Debug.WriteLine($"[Hotkey:Fired] ⚡ WM_HOTKEY ID={id}");
            if ((lParam.ToInt64() & HiddenKeystrokeMark) != 0)
                DiagnosticsService.LogCycling($"[Hotkey:KeyHook] ID={id} fired with its keystroke hidden from the clients (down and up)");
            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    App.PerfLog($"[Hotkey:Error] Action exception ID={id}: {ex.GetType().Name}");
                }
                handled = true;

                // Held-to-cycle needs nothing here: while the key is physically held the
                // OS keeps posting WM_HOTKEY and we land back in this handler, paced by
                // the throttle above. Releasing the key stops the posts, so cycling stops
                // on its own — no timer to run away.
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdogTimer?.Stop();
        _watchdogTimer = null;
        if (_scopeWinEvents != null)
        {
            _scopeWinEvents.ForegroundChanged -= OnForegroundChangedForScope;
            _scopeWinEvents = null;
        }
        UnregisterAll();
        // UnregisterAll preserves activation hotkeys — release them on shutdown.
        if (_hwndSource != null)
        {
            foreach (var id in _activationHotkeyIds)
            {
                User32.UnregisterHotKey(_hwndSource.Handle, id);
                _hotkeyActions.Remove(id);
                _hotkeyVks.Remove(id);
            }
            _activationHotkeyIds.Clear();
        }
        RemoveMouseHook();
        RemoveKeyHook();
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

    private void AddMouseBinding(uint mods, string buttonName, Action action, bool allowRepeat, bool isSuspend = false)
    {
        // Normalize aliases
        buttonName = buttonName switch
        {
            "MOUSE4" => "XBUTTON1",
            "MOUSE5" => "XBUTTON2",
            "MIDDLECLICK" => "MBUTTON",
            _ => buttonName
        };

        var binding = new MouseButtonBinding(mods, buttonName, action, allowRepeat);
        if (isSuspend) _suspendMouseBinding = binding;
        _storedMouseBindings.Add(binding); // keep for suspend/resume
        if (_suspended && !isSuspend) return;   // paused: only the pause key is hooked (IDEAS P24); the rest come back on resume
        _mouseBindings = new List<MouseButtonBinding>(_mouseBindings) { binding };
        EnsureMouseHook();
    }

    /// <summary>Re-arms the mouse hook from the stored bindings: all of them, or while paused only the pause key, so a
    /// pause key on a mouse button can resume (IDEAS P24) while every other button goes back to Windows. A running hook
    /// is stopped first rather than having its list changed under it.</summary>
    private void ReinstallMouseBindings()
    {
        RemoveMouseHook();
        _mouseBindings = !_suspended ? new List<MouseButtonBinding>(_storedMouseBindings)
            : _suspendMouseBinding != null ? new List<MouseButtonBinding> { _suspendMouseBinding }
            : new List<MouseButtonBinding>();
        EnsureMouseHook();
    }

    /// <summary>Installs the hook when there is something to hook. A service that was never Initialize()d (tests)
    /// installs none.</summary>
    private void EnsureMouseHook()
    {
        if (_mouseHookThread != null || _captureDepth > 0 || _mouseBindings.Count == 0 || _hwndSource == null) return;

        _mouseHookTick = Environment.TickCount;   // the watchdog's clock starts at install (IDEAS #41)
        _mouseHookGeneration++;
        _mouseHookReady.Reset();
        _mouseHookThread = new System.Threading.Thread(MouseHookThreadProc)
        {
            IsBackground = true,
            Name = "EmpMouseHook"
        };
        _mouseHookThread.Start();
        // Wait until the thread has published its id (microseconds in practice) so a
        // subsequent RemoveMouseHook can never miss it and orphan the thread.
        _mouseHookReady.Wait(1000);
        Debug.WriteLine("[Hotkey:Mouse] 🪝 Mouse hook thread starting");
    }

    /// <summary>Dedicated-thread body: install the LL mouse hook here so its
    /// callback is dispatched on this thread (not the UI thread), then pump
    /// messages until WM_QUIT is posted by RemoveMouseHook.</summary>
    private void MouseHookThreadProc()
    {
        _mouseHookThreadId = User32.GetCurrentThreadId();
        // The delegate lives in this thread's local, not a shared field: the next hook thread must not overwrite it while
        // this thread's hook is still installed, or the OS would call a collected delegate.
        User32.LowLevelMouseProc mouseProc = MouseHookCallback;
        // This thread owns the hook it installs: a replaced thread can wake from WM_QUIT after its successor has
        // installed a new hook, and must then unhook only its own, never the successor's.
        var handle = User32.SetWindowsHookEx(
            User32.WH_MOUSE_LL,
            mouseProc,
            User32.GetModuleHandle(null),
            0);
        _mouseHookHandle = handle;
        _mouseHookReady.Set(); // id + handle published
        Debug.WriteLine($"[Hotkey:Mouse] 🪝 Mouse hook installed on dedicated thread: {handle != IntPtr.Zero}");

        // Message loop — required for the LL hook callback to be delivered here.
        while (User32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { /* WM_QUIT ends the loop */ }

        if (handle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(handle);
            // Clear the shared field only if it is still ours.
            System.Threading.Interlocked.CompareExchange(ref _mouseHookHandle, IntPtr.Zero, handle);
        }
        GC.KeepAlive(mouseProc); // the OS holds only a raw pointer to this delegate
        Debug.WriteLine("[Hotkey:Mouse] 🛑 Mouse hook thread exited");
    }

    private void RemoveMouseHook()
    {
        StopMouseRepeat();
        if (_mouseHookThread != null)
        {
            _mouseHookGeneration++;   // the watchdog starts over (IDEAS #41)
            // Wake the hook thread's GetMessage loop so it unhooks and exits.
            if (_mouseHookThreadId != 0)
                User32.PostThreadMessage(_mouseHookThreadId, User32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _mouseHookThread = null;
            _mouseHookThreadId = 0;
            Debug.WriteLine("[Hotkey:Mouse] 🛑 Mouse hook removal requested");
        }
        _mouseBindings = new List<MouseButtonBinding>();
    }

    private void EnsureKeyHook()
    {
        if (_keyHookThread != null) return;
        _keyHookGeneration++;   // the watchdog starts over (IDEAS #41)
        _keyHookTick = _keyHookInstalledTick = Environment.TickCount;   // the watchdog's clock starts at install (IDEAS #41)
        _keyHookReady.Reset();
        _keyHookThread = new System.Threading.Thread(KeyHookThreadProc)
        {
            IsBackground = true,
            Name = "EmpKeyHook"
        };
        _keyHookThread.Start();
        _keyHookReady.Wait(1000); // thread id published, so RemoveKeyHook can always reach it
    }

    private void RemoveKeyHook()
    {
        _keyHookMap = null;
        if (_keyHookThread == null) return;
        _keyHookGeneration++;   // the watchdog starts over (IDEAS #41)
        if (_keyHookThreadId != 0)
            User32.PostThreadMessage(_keyHookThreadId, User32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _keyHookThread = null;
        _keyHookThreadId = 0;
    }

    /// <summary>IDEAS #41, every 2 s on the UI thread: puts the mouse hook back when Windows dropped it, through
    /// ReinstallMouseBindings, the path pause and resume use (so a paused service keeps only its pause button). With no
    /// mouse hook installed it reads nothing. The rules are HookWatchdog's; this gathers the inputs and acts. Never throws.</summary>
    private void CheckMouseHook()
    {
        try
        {
            if (_disposed) return;
            if (_mouseHookThread == null)
            {
                _mouseWatchdog.MouseLooksDead(new HookWatchdog.MouseLook(Environment.TickCount, false, _mouseHookGeneration, 0, null, null, false, true, false));
                return;
            }
            // The time first, the devices next, the hook's stamp last: a call that lands during the reads is then credited
            // to this look's stamp only if its input is in this look too, never ahead of it.
            int now = Environment.TickCount;
            bool gotCursor = User32.GetCursorPos(out var cursor);
            var lastInput = User32.LastInputTick();
            bool seeForeground = ForegroundHooksSeeInput(User32.GetForegroundWindow());
            bool seeUnderCursor = gotCursor && CursorWindowHooksSeeInput(cursor);
            bool buttonDown = BoundMouseButtonDown();
            int generation = _mouseHookGeneration;
            int stamp = _mouseHookTick;
            var look = new HookWatchdog.MouseLook(now, true, generation, stamp, lastInput,
                gotCursor ? (cursor.X, cursor.Y) : null, seeForeground, buttonDown, seeUnderCursor);
            if (!_mouseWatchdog.MouseLooksDead(look)) return;
            // Same UI-thread turn as the look, but nothing may have been removed or reinstalled since: ask again.
            if (!HookWatchdog.MouseMayReinstall(_disposed, _captureDepth > 0, _mouseHookThread != null, _mouseHookGeneration, look.Generation)) return;
            if (!_mouseWatchdog.MayReinstall(now)) { NoteLimitOnce(_mouseWatchdog, ref _mouseLimitNoted, "mouse"); return; }
            ReinstallMouseBindings();
            ErrorLog.Shared.Note($"[Hotkey:Watchdog] mouse hook stopped receiving input (silent {unchecked(now - stamp)} ms, foreground: "
                + $"{ForegroundExeName()}); reinstalling ({_mouseWatchdog.Reinstalls} of {HookWatchdog.MaxReinstallsPerSession}).", DateTime.Now);
        }
        catch (Exception ex) { Debug.WriteLine($"[Hotkey:Watchdog] Mouse check failed: {ex.Message}"); }
    }

    /// <summary>IDEAS #41, when a hotkey arrives without the hook's mark (<paramref name="messageTime"/> is when the system
    /// posted it): if its keystroke was one the keyboard hook hides and the hook was silent, twice, Windows dropped it, and
    /// it is put back with the same keys to hide. Cheap reads first (this runs for every unmarked hotkey, including a held
    /// key's repeats). The rules are HookWatchdog's. Never throws.</summary>
    private void CheckKeyHook(int id, int messageTime)
    {
        try
        {
            var map = _keyHookMap;
            if (_disposed || _keyHookThread == null) { _keyWatchdog.ForgetKeyStrike(); return; }
            if (map == null) return;   // hotkeys deactivated: nothing to judge, and a strike from before is left alone
            int lastCall = _keyHookTick;
            if (!HookWatchdog.KeyHookMissedPress(messageTime, lastCall, _keyHookInstalledTick)) { _keyWatchdog.ForgetKeyStrike(); return; }
            bool hidden = false;
            foreach (var slot in map.Values)
                if (slot.Id == id) { hidden = true; break; }
            var foreground = User32.GetForegroundWindow();
            var look = new HookWatchdog.KeyLook(messageTime, lastCall, _keyHookInstalledTick, _keyHookGeneration, _keyHookThread != null,
                _suspended, _captureDepth > 0, _disposed, hidden,
                ForegroundHooksSeeInput(foreground), foreground == IntPtr.Zero);
            if (!_keyWatchdog.KeyHookLooksDead(look)) return;
            if (!_keyWatchdog.MayReinstall(Environment.TickCount)) { NoteLimitOnce(_keyWatchdog, ref _keyLimitNoted, "keyboard"); return; }
            ReinstallKeyHook();
            ErrorLog.Shared.Note($"[Hotkey:Watchdog] keyboard hook stopped receiving input (silent {unchecked(messageTime - lastCall)} ms, foreground: "
                + $"{ForegroundExeName()}); reinstalling ({_keyWatchdog.Reinstalls} of {HookWatchdog.MaxReinstallsPerSession}).", DateTime.Now);
        }
        catch (Exception ex) { Debug.WriteLine($"[Hotkey:Watchdog] Key check failed: {ex.Message}"); }
    }

    /// <summary>A new keyboard hook thread with the same keys to hide. A press the old hook
    /// missed is left alone: the new hook doesn't own it, so hiding only its release can't strand the key down.</summary>
    private void ReinstallKeyHook() => ReinstallKeyHook(RemoveKeyHook, EnsureKeyHook);

    /// <summary>The same with the install and remove passed in, so a test can run it without a real hook.</summary>
    private void ReinstallKeyHook(Action remove, Action ensure)
    {
        var map = _keyHookMap;
        remove();
        ensure();
        _keyHookMap = map;
    }

    /// <summary>Whether the window in front is one this process's hooks can see input for (not elevated over a normal
    /// process). The answer for the last window asked about is kept, so the process lookup runs on a change of window.</summary>
    private bool ForegroundHooksSeeInput(IntPtr foreground) =>
        HooksSeeCached(foreground, ref _hooksSeeHwnd, ref _hooksSeeInput);

    /// <summary>The same for the top-level window under the cursor (a non-foreground elevated window may get input the
    /// hooks never see). Zero (nothing there, or can't tell) is "no".</summary>
    private bool CursorWindowHooksSeeInput(User32.POINT cursor)
    {
        var window = User32.WindowFromPoint(cursor);
        var root = window == IntPtr.Zero ? IntPtr.Zero : User32.GetAncestor(window, User32.GA_ROOT);
        return HooksSeeCached(root, ref _hooksSeeCursorHwnd, ref _hooksSeeCursor);
    }

    private static bool HooksSeeCached(IntPtr hwnd, ref IntPtr cachedHwnd, ref bool cachedResult)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd != cachedHwnd)
        {
            cachedHwnd = hwnd;
            cachedResult = User32.HooksSeeInputFor(hwnd);
        }
        return cachedResult;
    }

    /// <summary>A mouse button the hook has a binding for is physically down.</summary>
    private bool BoundMouseButtonDown()
    {
        foreach (var binding in _mouseBindings)
        {
            int vk = MouseButtonVirtualKey(binding.ButtonName);
            if (vk != 0 && User32.IsKeyDown(vk)) return true;
        }
        return false;
    }

    /// <summary>The virtual key whose state says whether a bound mouse button is down; 0 for any other name.</summary>
    public static int MouseButtonVirtualKey(string buttonName) =>
        buttonName switch { "MBUTTON" => 0x04, "XBUTTON1" => 0x05, "XBUTTON2" => 0x06, _ => 0 };

    /// <summary>The exe name of the window in front (no title, no path), for a note a reader can triage.</summary>
    private static string ForegroundExeName()
    {
        try { return User32.GetProcessName(User32.GetForegroundWindow()) ?? "unknown"; }
        catch { return "unknown"; }
    }

    /// <summary>One hook used up its session allowance: one note says which, then nothing more is written for it.</summary>
    private static void NoteLimitOnce(HookWatchdog watchdog, ref bool noted, string which)
    {
        if (!watchdog.LimitReached || noted) return;
        noted = true;
        ErrorLog.Shared.Note($"[Hotkey:Watchdog] the {which} hook has been reinstalled {HookWatchdog.MaxReinstallsPerSession} times this session; "
            + $"no more reinstalls of it will be tried.", DateTime.Now);
    }

    /// <summary>Dedicated-thread body, same shape as the mouse hook's: a low-level hook
    /// is called on its installing thread, so it gets its own message loop and never
    /// waits behind the UI thread. The pressed-key table is LOCAL to this thread: a
    /// hook being replaced can briefly overlap its successor, and each must only ever
    /// release keys it swallowed itself.</summary>
    private void KeyHookThreadProc()
    {
        var hiddenDown = new Dictionary<uint, KeySlot>();
        IntPtr handle = IntPtr.Zero;
        User32.LowLevelKeyboardProc proc = (nCode, wParam, lParam) =>
            KeyHookCallback(hiddenDown, handle, nCode, wParam, lParam);

        _keyHookThreadId = User32.GetCurrentThreadId();
        handle = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, proc, User32.GetModuleHandle(null), 0);
        _keyHookReady.Set();
        App.PerfLog($"[Hotkey:KeyHook] Keystroke-hiding hook installed: {handle != IntPtr.Zero}");

        while (User32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { /* WM_QUIT ends the loop */ }

        if (handle != IntPtr.Zero)
            User32.UnhookWindowsHookEx(handle);
        GC.KeepAlive(proc); // the OS holds only a raw pointer to this delegate
    }

    /// <summary>Runs for every keystroke system-wide, so it only does table lookups -
    /// no logging, no I/O, nothing that could approach the OS hook timeout.</summary>
    private IntPtr KeyHookCallback(Dictionary<uint, KeySlot> hiddenDown, IntPtr handle,
        int nCode, IntPtr wParam, IntPtr lParam)
    {
        _keyHookTick = Environment.TickCount;   // IDEAS #41: the stamp the hook check reads, this hook's only extra work
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
            // Real keystrokes only. Injected ones - our own vk0xE8 activation bridge,
            // remapper software - keep the RegisterHotKey path.
            if ((kb.flags & LLKHF_INJECTED) == 0)
            {
                uint vk = kb.vkCode;
                if ((kb.flags & LLKHF_UP) != 0)
                {
                    // Hide the release of a press we hid. Any other release is not ours.
                    if (hiddenDown.Remove(vk)) return (IntPtr)1;
                }
                else if (hiddenDown.TryGetValue(vk, out var held))
                {
                    // Keyboard auto-repeat of a press we own. Mirror RegisterHotKey: a
                    // repeat slot re-posts (held-to-cycle); a MOD_NOREPEAT slot does not.
                    if (held.Repeat) PostHiddenHotkey(held.Id, vk);
                    return (IntPtr)1;
                }
                else if (!User32.IsKeyDown((int)vk))
                {
                    // Fresh press (the async state does not include this event yet). A key
                    // that is ALREADY down was pressed before we owned it - e.g. held while
                    // the slots were published - so it is left alone end to end: hiding
                    // only its release would strand it logically down.
                    var map = _keyHookMap;
                    if (map != null && map.TryGetValue((CurrentHotkeyMods(), vk), out var slot))
                    {
                        hiddenDown[vk] = slot;
                        PostHiddenHotkey(slot.Id, vk);
                        return (IntPtr)1;
                    }
                }
            }
        }
        return User32.CallNextHookEx(handle, nCode, wParam, lParam);
    }

    private void PostHiddenHotkey(int id, uint vk) =>
        User32.PostMessage(_hotkeyHwnd, (uint)User32.WM_HOTKEY, (IntPtr)id,
            (IntPtr)((int)(vk << 16) | HiddenKeystrokeMark));

    /// <summary>Modifier state in RegisterHotKey's terms (either-side Ctrl/Alt/Shift/Win),
    /// matching what the OS itself compares against a registered slot.</summary>
    private static uint CurrentHotkeyMods()
    {
        uint m = 0;
        if (User32.IsKeyDown(0x12)) m |= User32.MOD_ALT;
        if (User32.IsKeyDown(0x11)) m |= User32.MOD_CONTROL;
        if (User32.IsKeyDown(0x10)) m |= User32.MOD_SHIFT;
        if (User32.IsKeyDown(0x5B) || User32.IsKeyDown(0x5C)) m |= User32.MOD_WIN;
        return m;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _mouseHookTick = Environment.TickCount;   // IDEAS #41: the stamp the hook check reads, this hook's only extra work
        var bindings = _mouseBindings;   // one snapshot per event
        if (nCode >= 0 && bindings.Count > 0)
        {
            int msg = wParam.ToInt32();
            string? buttonName = null;
            bool isDown = false, isUp = false;

            if (msg == User32.WM_MBUTTONDOWN) { buttonName = "MBUTTON"; isDown = true; }
            else if (msg == User32.WM_MBUTTONUP) { buttonName = "MBUTTON"; isUp = true; }
            else if (msg == User32.WM_XBUTTONDOWN || msg == User32.WM_XBUTTONUP)
            {
                var hookStruct = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                int xButton = User32.HIWORD(hookStruct.mouseData);
                buttonName = xButton == User32.XBUTTON1 ? "XBUTTON1" :
                             xButton == User32.XBUTTON2 ? "XBUTTON2" : null;
                isDown = msg == User32.WM_XBUTTONDOWN;
                isUp   = msg == User32.WM_XBUTTONUP;
            }

            if (buttonName != null)
            {
                bool hasAnyBinding = bindings.Any(b => b.ButtonName == buttonName);

                // UP: stop any in-flight repeat for this button. Suppress the UP
                // if we have a binding for this button so the focused app sees
                // matched DOWN/UP (we suppressed the DOWN earlier).
                if (isUp)
                {
                    // StopMouseRepeat touches a DispatcherTimer — marshal to UI thread
                    // (this callback now runs on the dedicated hook thread).
                    if (_mouseRepeatButton == buttonName)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke((Action)StopMouseRepeat);
                    return hasAnyBinding
                        ? (IntPtr)1
                        : User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                }

                if (isDown)
                {
                    // P42: read once; LogCycling checks this flag only after its argument is built, and building
                    // that text (a Count over the bindings, interpolations) is work this hook thread skips when off.
                    bool logCycling = EveMultiPreview.Services.DiagnosticsService.GlobalSettings?.EnableDebugLogging_Cycling == true;

                    // Check modifiers
                    uint currentMods = 0;
                    if (User32.IsKeyDown(0x11)) currentMods |= User32.MOD_CONTROL; // VK_CONTROL
                    if (User32.IsKeyDown(0x12)) currentMods |= User32.MOD_ALT;     // VK_MENU
                    if (User32.IsKeyDown(0x10)) currentMods |= User32.MOD_SHIFT;   // VK_SHIFT
                    if (User32.IsKeyDown(0x5B) || User32.IsKeyDown(0x5C)) currentMods |= User32.MOD_WIN;

                    var bestBinding = bindings.FirstOrDefault(b => b.ButtonName == buttonName && b.Modifiers == currentMods)
                                   ?? bindings.FirstOrDefault(b => b.ButtonName == buttonName && b.Modifiers == 0);

                    // Diagnostic (#92): the mouse-hook cycle path was a black box. Log
                    // every DOWN we matched to a button, the live modifiers, how many
                    // bindings exist for it, and whether one actually fired — so a
                    // "button is eaten but doesn't cycle" report can be pinpointed.
                    if (logCycling)
                        EveMultiPreview.Services.DiagnosticsService.LogCycling(
                            $"[Hotkey:Mouse] DOWN {buttonName} mods=0x{currentMods:X} " +
                            $"bindings-for-button={bindings.Count(b => b.ButtonName == buttonName)} " +
                            $"matched={(bestBinding != null ? $"yes(repeat={bestBinding.AllowRepeat})" : "NO — no binding for these modifiers")}");

                    if (bestBinding != null)
                    {
                        var binding = bestBinding;
                        // Mouse keybinds fire regardless of which app is foreground —
                        // the user explicitly bound this button for cycling EVE clients,
                        // so it should pull EVE forward whether they're in EVE, in their
                        // browser, on the desktop, or anywhere else. Activation rights
                        // for the cross-app foreground shift are handled by
                        // User32.ActivateWindow's vk0xE8 bridge.

                        // Settings window block — keep this guard. While the user is
                        // editing bindings in Settings, no cycle should fire and steal
                        // focus mid-capture.
                        try
                        {
                            var fgHwnd = User32.GetForegroundWindow();
                            if (SettingsInFront(fgHwnd))
                            {
                                if (logCycling)
                                    EveMultiPreview.Services.DiagnosticsService.LogCycling(
                                        $"[Hotkey:Mouse] {buttonName} SUPPRESSED — Settings window is foreground, cycle not fired");
                                return User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                            }
                        }
                        catch { }

                        // Command palette block (mouse): the same stand-down as for a key. Only the palette's own button gets through.
                        try
                        {
                            if (PaletteBlocks(PaletteInFront(), ReferenceEquals(binding.Action, _paletteKeyAction)))
                            {
                                if (logCycling)
                                    EveMultiPreview.Services.DiagnosticsService.LogCycling(
                                        $"[Hotkey:Mouse] {buttonName} SUPPRESSED - command palette is foreground, cycle not fired");
                                return User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                            }
                        }
                        catch { }

                        Debug.WriteLine($"[Hotkey:Mouse] ⚡ Fired: {buttonName} (mods=0x{currentMods:X}) repeat={binding.AllowRepeat}");
                        // Fire the action AND arm the repeat on the UI thread — both
                        // touch WPF state / a DispatcherTimer, and this callback runs
                        // on the dedicated hook thread.
                        bool armRepeat = binding.AllowRepeat && CycleWhileHeld;
                        var fireButton = buttonName;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            EveMultiPreview.Services.DiagnosticsService.LogCycling(
                                $"[Hotkey:Mouse] invoking cycle action for {fireButton} (armRepeat={armRepeat})");
                            try { binding.Action.Invoke(); }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Hotkey:Mouse] ❌ Action error: {ex.Message}");
                                EveMultiPreview.Services.DiagnosticsService.LogCycling(
                                    $"[Hotkey:Mouse] ❌ cycle action threw: {ex.GetType().Name}: {ex.Message}");
                            }

                            // Mouse buttons have no OS-level auto-repeat, so for cycle
                            // bindings we run our own DispatcherTimer that re-fires every
                            // CycleDelayMs until WM_*BUTTONUP.
                            if (armRepeat)
                                StartMouseRepeat(binding, fireButton);
                        });

                        // Return 1 to suppress the mouse event from reaching other apps
                        return (IntPtr)1;
                    }
                }
            }
        }

        return User32.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    /// <summary>Begin auto-repeating a mouse-bound action while the button is held.
    /// Stopped by the WM_*BUTTONUP that arrives in MouseHookCallback. There's a
    /// duration-based safety cap (60 s) so a missed UP can't cycle indefinitely.
    ///
    /// Note: we deliberately do NOT poll the button via GetAsyncKeyState here —
    /// that API is unreliable for mouse buttons when the calling process isn't
    /// the foreground process, which is exactly our scenario when cycling FROM
    /// a non-EVE window. WM_*BUTTONUP from the low-level hook chain always
    /// fires for real releases, so it's the authoritative stop signal.</summary>
    private void StartMouseRepeat(MouseButtonBinding binding, string buttonName)
    {
        StopMouseRepeat(); // cancel any prior repeat first

        _mouseRepeatBinding = binding;
        _mouseRepeatButton = buttonName;

        // Snapshot the user's current cycle-delay setting at arm time so changes
        // to Settings during a long hold don't retroactively jump the cadence.
        int delayMs = CycleDelayMs;

        // Hold-detection threshold: how long the button has to be down before
        // we treat the press as a deliberate "hold to cycle" rather than a
        // single tap. Without this, a tap that runs slightly longer than
        // CycleDelayMs (typical mouse taps can be 100–200 ms) would fire a
        // second cycle from the first auto-repeat tick before WM_*BUTTONUP
        // arrives. 250 ms matches the default Windows keyboard repeat delay
        // and is the rough threshold humans use to distinguish tap from hold.
        const int HoldDetectMs = 250;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HoldDetectMs)
        };
        _mouseRepeatTimer = timer;

        long startTicks = Environment.TickCount64;
        const long MaxRepeatDurationMs = 60_000;

        bool firstTick = true;
        timer.Tick += (_, _) =>
        {
            // Safety cap: if 60 s has passed without a WM_*BUTTONUP, something
            // got stuck (mouse disconnected, hook torn down between events,
            // etc.) — stop ourselves rather than cycle forever.
            if (Environment.TickCount64 - startTicks > MaxRepeatDurationMs)
            {
                Debug.WriteLine($"[Hotkey:Mouse] ⚠ Repeat safety-cap stop after {MaxRepeatDurationMs}ms ({buttonName})");
                StopMouseRepeat();
                return;
            }

            // After the hold-detect threshold, switch to steady-state cycle
            // cadence at the user's CycleDelayMs rate.
            if (firstTick)
            {
                firstTick = false;
                timer.Interval = TimeSpan.FromMilliseconds(delayMs);
            }

            var b = _mouseRepeatBinding;
            if (b == null) return;
            try { b.Action.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[Hotkey:Mouse] ❌ Repeat action error: {ex.Message}"); }
        };

        timer.Start();
    }

    private void StopMouseRepeat()
    {
        if (_mouseRepeatTimer != null)
        {
            _mouseRepeatTimer.Stop();
            _mouseRepeatTimer = null;
        }
        _mouseRepeatBinding = null;
        _mouseRepeatButton = null;
    }
}
