using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace EveMultiPreview.Linux;

/// <summary>System-wide hotkeys on X11: grabs each key combination on the root window (with and without Caps Lock and
/// Num Lock, which X counts as modifiers) and runs its action on the UI thread. Keys are the Windows app's AHK-style
/// strings ("^!F5", "F1", "+a"). Its own X connection, read by one background thread.</summary>
public sealed unsafe class GlobalHotkeys
{
    private const string Lib = "libxcb.so.1";
    private const byte KeyPress = 2, GrabModeAsync = 1;
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

    private readonly IntPtr _c;
    private readonly uint _root;
    private readonly Dictionary<uint, byte> _keycodeOf = new();
    private readonly object _gate = new();
    private Dictionary<(byte Key, ushort Mods), Action> _bound = new();

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
    public IReadOnlyList<string> Bind(IEnumerable<(string Key, Action Run)> keys)
    {
        var refused = new List<string>();
        var bound = new Dictionary<(byte, ushort), Action>();
        xcb_ungrab_key(_c, 0, _root, AnyModifier);
        foreach (var (key, run) in keys)
        {
            if (Parse(key) is not { } k) { refused.Add(key); continue; }
            bound[k] = run;
            foreach (ushort locks in new ushort[] { 0, Lock, Mod2, Lock | Mod2 })
                xcb_grab_key(_c, 1, _root, (ushort)(k.Mods | locks), k.Key, GrabModeAsync, GrabModeAsync);
        }
        Xcb.xcb_flush(_c);
        lock (_gate) _bound = bound;
        return refused;
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
                if ((p[0] & 0x7f) != KeyPress) continue;
                var k = (p[1], (ushort)(*(ushort*)(p + 28) & Wanted));
                Action? run;
                lock (_gate) _bound.TryGetValue(k, out run);
                if (Environment.GetEnvironmentVariable("EVEMP_DEBUG_KEYS") == "1")
                    Console.WriteLine($"[keys] keycode {k.Item1} mods {k.Item2}: {(run != null ? "bound" : "not bound")}");
                if (run != null) Dispatcher.UIThread.Post(run);
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
