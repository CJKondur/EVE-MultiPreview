using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace EveMultiPreview.Linux;

/// <summary>System-wide hotkeys on X11: grabs each key combination on the root window (with and without Caps Lock and
/// Num Lock, which X counts as modifiers) and runs its action on the UI thread. Keys are the Windows app's AHK-style
/// strings ("^!F5", "F1", "+a"). Its own X connection, read by one background thread.</summary>
public sealed unsafe class GlobalHotkeys
{
    private const string Lib = "libxcb.so.1";
    private const byte KeyPress = 2, KeyRelease = 3, GrabModeAsync = 1;
    private const ushort Shift = 1, Lock = 2, Control = 4, Mod1 = 8, Mod2 = 16, Mod4 = 64, AnyModifier = 0x8000;
    private const ushort Wanted = Shift | Control | Mod1 | Mod4;

    [DllImport(Lib)] private static extern uint xcb_grab_key(IntPtr c, byte ownerEvents, uint grabWindow, ushort modifiers, byte key, byte pointerMode, byte keyboardMode);
    [DllImport(Lib)] private static extern uint xcb_ungrab_key(IntPtr c, byte key, uint grabWindow, ushort modifiers);
    [DllImport(Lib)] private static extern IntPtr xcb_get_setup(IntPtr c);
    [DllImport(Lib)] private static extern uint xcb_get_keyboard_mapping(IntPtr c, byte firstKeycode, byte count);
    [DllImport(Lib)] private static extern IntPtr xcb_get_keyboard_mapping_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(Lib)] private static extern IntPtr xcb_get_keyboard_mapping_keysyms(IntPtr reply);
    [DllImport(Lib)] private static extern int xcb_get_keyboard_mapping_keysyms_length(IntPtr reply);
    [DllImport(Lib)] private static extern IntPtr xcb_wait_for_event(IntPtr c);
    [DllImport(Lib)] private static extern uint xcb_query_keymap(IntPtr c);
    [DllImport(Lib)] private static extern IntPtr xcb_query_keymap_reply(IntPtr c, uint cookie, IntPtr e);

    private readonly IntPtr _c;
    private readonly uint _root;
    private readonly Dictionary<uint, byte> _keycodeOf = new();
    private readonly object _gate = new();
    private Dictionary<(byte Key, ushort Mods), (Action Run, bool Repeatable)> _bound = new();

    public GlobalHotkeys()
    {
        _c = Xcb.xcb_connect(null, out int screen);
        if (Xcb.xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("Could not connect to the X server.");
        _root = Xcb.Root(_c, screen);
        ReadKeyboardMap();
        new Thread(Listen) { IsBackground = true, Name = "X hotkeys" }.Start();
    }

    /// <summary>Replaces every grab with <paramref name="keys"/>. Returns the keys that could not be bound: a name
    /// this keyboard has no key for, or one only Windows knows (mouse buttons, vkNN).</summary>
    public IReadOnlyList<string> Bind(IEnumerable<(string Key, Action Run)> keys) => Bind(keys.Select(k => (k.Key, k.Run, false)));

    /// <summary>Whether a held key keeps switching (CycleWhileHeld) and how often (CycleDelayMs), asked at each repeat; and
    /// whether the keys fire while other modifiers are held too (HotkeyWildcardModifiers), asked at each Bind.</summary>
    public Func<bool> CycleWhileHeld { get; set; } = () => true;
    public Func<int> CycleDelayMs { get; set; } = () => 100;
    public Func<bool> Wildcard { get; set; } = () => false;

    /// <summary>The grabs, as <see cref="Bind(IEnumerable{ValueTuple{string, Action}})"/>; a <paramref name="keys"/> entry that is
    /// repeatable (the cycle keys, as HotkeyService.StoreRepeatableAhkHotkey) runs again while the key is held when
    /// CycleWhileHeld is on, at most every CycleDelayMs; every other key runs once per press (MOD_NOREPEAT). With the wildcard
    /// switch on, each key is also grabbed with every other mix of Ctrl, Shift, Alt and Super held (AHK's "*").</summary>
    public IReadOnlyList<string> Bind(IEnumerable<(string Key, Action Run, bool Repeatable)> keys)
    {
        var refused = new List<string>();
        var bound = new Dictionary<(byte, ushort), (Action Run, bool Repeatable)>();
        bool wildcard = Wildcard();
        xcb_ungrab_key(_c, 0, _root, AnyModifier);
        foreach (var (key, run, repeatable) in keys)
        {
            if (Parse(key) is not { } k) { refused.Add(key); continue; }
            bound[k] = (run, repeatable);
            foreach (ushort extra in wildcard ? ExtraModifiers(k.Mods) : new ushort[] { 0 })
                foreach (ushort locks in new ushort[] { 0, Lock, Mod2, Lock | Mod2 })
                    xcb_grab_key(_c, 1, _root, (ushort)(k.Mods | extra | locks), k.Key, GrabModeAsync, GrabModeAsync);
        }
        Xcb.xcb_flush(_c);
        lock (_gate) { _bound = bound; _wildcard = wildcard; }
        Interlocked.Increment(ref _hold);   // a held key of the old bindings stops
        return refused;
    }

    private bool _wildcard;

    /// <summary>Every mix of the modifiers a key does not name itself (none included).</summary>
    public static IEnumerable<ushort> ExtraModifiers(ushort own)
    {
        ushort free = (ushort)(Wanted & ~own);
        for (int m = 0; m <= Wanted; m++)
            if ((m & ~free) == 0) yield return (ushort)m;
    }

    /// <summary>The binding a press finds: its own combination; with the wildcard on, else the binding of that key whose
    /// modifiers are the most of those held (all of them held).</summary>
    public static (Action Run, bool Repeatable)? Find(IReadOnlyDictionary<(byte Key, ushort Mods), (Action Run, bool Repeatable)> bound, byte key, ushort mods, bool wildcard)
    {
        if (bound.TryGetValue((key, mods), out var exact)) return exact;
        if (!wildcard) return null;
        (Action, bool)? best = null;
        int bestCount = -1;
        foreach (var ((k, m), binding) in bound)
            if (k == key && (m & ~mods) == 0 && System.Numerics.BitOperations.PopCount(m) > bestCount)
                (best, bestCount) = (binding, System.Numerics.BitOperations.PopCount(m));
        return best;
    }

    /// <summary>X sends a held key as KeyRelease and KeyPress pairs with one timestamp (or, with detectable repeat, presses
    /// without a release): a press is a repeat when the key was let go at that very moment, or is still down from a press
    /// at most a second before (a release that never came, say to another grab, must not make every later press a repeat).</summary>
    public static bool IsRepeat(uint pressTime, uint? releasedAt, uint? downSince) =>
        releasedAt is { } r && pressTime - r <= 1 || downSince is { } d && pressTime - d <= 1000;

    private readonly Dictionary<byte, uint> _down = new();   // key → the time of its last press, while not let go
    private readonly Dictionary<byte, uint> _releasedAt = new();

    /// <summary>How long a cycle key must be held before it starts repeating: the Windows keyboard's default repeat delay,
    /// which paces the Windows app's held key, and long enough that a slow tap never switches twice.</summary>
    public const int HoldStartMs = 250;

    /// <summary>The time between switches of a held cycle key: CycleDelayMs, in the settings card's range.</summary>
    public static int HoldStepMs(int cycleDelayMs) => Math.Clamp(cycleDelayMs, 1, 2000);

    /// <summary>Which hold runs: each new press (and each Bind) moves it on, and an older hold's thread sees that and stops.</summary>
    private int _hold;

    /// <summary>A held cycle key repeats on MultiPreview's own clock, not X's key repeat: X waits ~600 ms before the first
    /// repeat (Windows 250 ms) and then repeats at the keyboard's rate, so a held key paused, then switched no faster than the
    /// keyboard repeats. Each switch waits until the previous one has run on the UI thread, so a short delay never piles up
    /// switches behind a busy UI. It stops when the key is up (the keyboard's real state, which X's repeat does not change).</summary>
    private void StartHold(byte code, Action run)
    {
        int hold = Interlocked.Increment(ref _hold);
        new Thread(() =>
        {
            Thread.Sleep(HoldStartMs);
            while (Volatile.Read(ref _hold) == hold && CycleWhileHeld() && IsDown(code))
            {
                try { Dispatcher.UIThread.Invoke(run); }
                catch (Exception) { return; }   // the app is ending
                Thread.Sleep(HoldStepMs(CycleDelayMs()));
            }
        }) { IsBackground = true, Name = "Held cycle key" }.Start();
    }

    /// <summary>Whether the key is down on the keyboard now (QueryKeymap).</summary>
    private bool IsDown(byte code)
    {
        var reply = xcb_query_keymap_reply(_c, xcb_query_keymap(_c), IntPtr.Zero);
        if (reply == IntPtr.Zero) return false;
        try { return (((byte*)reply)[8 + code / 8] & (1 << (code % 8))) != 0; }
        finally { NativeMemory.Free((void*)reply); }
    }

    private void Listen()
    {
        while (true)
        {
            var e = xcb_wait_for_event(_c);
            if (e == IntPtr.Zero) return;   // the connection is gone
            try
            {
                byte* p = (byte*)e;
                int type = p[0] & 0x7f;
                byte code = p[1];
                uint time = *(uint*)(p + 4);
                if (type == KeyRelease)
                {
                    _down.Remove(code);
                    _releasedAt[code] = time;
                    continue;
                }
                if (type != KeyPress) continue;
                bool repeat = IsRepeat(time, _releasedAt.TryGetValue(code, out var r) ? r : null, _down.TryGetValue(code, out var d) ? d : null);
                _down[code] = time;
                var mods = (ushort)(*(ushort*)(p + 28) & Wanted);
                (Action Run, bool Repeatable)? hit;
                lock (_gate) hit = Find(_bound, code, mods, _wildcard);
                if (Environment.GetEnvironmentVariable("EVEMP_DEBUG_KEYS") == "1")
                    Console.WriteLine($"[keys] keycode {code} mods {mods}{(repeat ? " (repeat)" : "")}: {(hit != null ? "bound" : "not bound")}");
                if (hit is not { } binding) continue;
                // X's own repeats are never used: a held cycle key repeats by StartHold (CycleWhileHeld, CycleDelayMs), every
                // other key runs once per press (HotkeyService's MOD_NOREPEAT).
                if (repeat) continue;
                Dispatcher.UIThread.Post(binding.Run);   // a fresh press always fires at once (#60)
                if (binding.Repeatable && CycleWhileHeld()) StartHold(code, binding.Run);
                else Interlocked.Increment(ref _hold);   // another key pressed: a held cycle key stops, as with the OS repeat
            }
            finally { NativeMemory.Free((void*)e); }
        }
    }

    /// <summary>(keycode, modifier mask) for an AHK-style hotkey, or null.</summary>
    private (byte Key, ushort Mods)? Parse(string ahk)
    {
        if (string.IsNullOrWhiteSpace(ahk)) return null;
        ushort mods = 0;
        int i = 0;
        for (; i < ahk.Length - 1 && "^!+#".Contains(ahk[i]); i++)
            mods |= ahk[i] switch { '^' => Control, '!' => Mod1, '+' => Shift, _ => Mod4 };
        var name = ahk[i..];
        return Keysym(name) is uint sym && _keycodeOf.TryGetValue(sym, out byte code) ? (code, mods) : null;
    }

    /// <summary>The X keysym for an AHK key name, or null for one X has no key for here.</summary>
    internal static uint? Keysym(string name)
    {
        if (name.Length == 1)
        {
            char ch = char.ToLowerInvariant(name[0]);
            return ch is >= ' ' and <= '~' ? ch : null;   // Latin-1 keysyms equal the character for printable ASCII
        }
        var n = name.ToLowerInvariant();
        if (n.StartsWith('f') && int.TryParse(n[1..], out int f) && f is >= 1 and <= 24) return (uint)(0xffbe + f - 1);
        if (n.StartsWith("numpad") && n.Length == 7 && char.IsDigit(n[6])) return (uint)(0xffb0 + (n[6] - '0'));
        return n switch
        {
            "escape" or "esc" => 0xff1b, "space" => 0x20, "enter" or "return" => 0xff0d, "tab" => 0xff09,
            "backspace" or "bs" => 0xff08, "delete" or "del" => 0xffff, "insert" or "ins" => 0xff63,
            "home" => 0xff50, "end" => 0xff57, "pgup" => 0xff55, "pgdn" => 0xff56,
            "up" => 0xff52, "down" => 0xff54, "left" => 0xff51, "right" => 0xff53,
            "pause" => 0xff13, "scrolllock" => 0xff14, "capslock" => 0xffe5, "numlock" => 0xff7f, "printscreen" => 0xff61,
            "numpadmult" => 0xffaa, "numpadadd" => 0xffab, "numpadsub" => 0xffad, "numpaddiv" => 0xffaf,
            "numpaddot" => 0xffae, "numpadenter" => 0xff8d,
            "oem102" => 0x3c,
            _ => null,
        };
    }

    /// <summary>Which keycode carries each keysym on this keyboard (the first one, as X itself picks).</summary>
    private void ReadKeyboardMap()
    {
        byte* setup = (byte*)xcb_get_setup(_c);
        byte min = setup[34], max = setup[35];
        var reply = xcb_get_keyboard_mapping_reply(_c, xcb_get_keyboard_mapping(_c, min, (byte)(max - min + 1)), IntPtr.Zero);
        if (reply == IntPtr.Zero) return;
        try
        {
            int perCode = ((byte*)reply)[1];
            uint* syms = (uint*)xcb_get_keyboard_mapping_keysyms(reply);
            int count = xcb_get_keyboard_mapping_keysyms_length(reply);
            for (int i = 0; i < count; i++)
                if (syms[i] != 0) _keycodeOf.TryAdd(syms[i], (byte)(min + i / perCode));
        }
        finally { NativeMemory.Free((void*)reply); }
    }
}
