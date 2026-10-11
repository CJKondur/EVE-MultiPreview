using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>A hotkey Windows refused to register (IDEAS #42).</summary>
/// <param name="Hotkey">The hotkey as the settings hold it ("^F5", "F13").</param>
/// <param name="Error">What Windows said (Marshal.GetLastWin32Error): 1409 when another program has the combination.</param>
public sealed record RefusedHotkey(string Hotkey, int Error)
{
    /// <summary>Whether the reason is that another program registered the combination first.</summary>
    public bool Taken => Error == HotkeyRefusals.AlreadyRegistered;
}

/// <summary>The rules of the refused-hotkey report (IDEAS #42), kept apart from HotkeyService so they can be tested:
/// which refusal is worth telling, which programs are the usual owners, and what the tray says.</summary>
public static class HotkeyRefusals
{
    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED.</summary>
    public const int AlreadyRegistered = 1409;

    /// <summary>How many keys the tray's note names before it ends in "…". A balloon holds 255 characters.</summary>
    public const int MaxNamed = 3;

    /// <summary>Whether a slot Windows refused is the combination the player bound. HotkeyService asks for every
    /// hotkey 16 times, once with each mix of Ctrl, Alt, Shift and Win held as well, so the key works while a modifier
    /// is down. Windows keeps many of those mixes for itself (Win + 1 …): their refusal is routine and takes nothing
    /// the player asked for. Only the exact combination is told.</summary>
    public static bool IsThePlayersOwn(uint slotModifiers, uint boundModifiers) => (slotModifiers & 0xF) == (boundModifiers & 0xF);

    /// <summary>Programs known to register global hotkeys: the process name (without ".exe") and the name to show,
    /// in the order shown. EVE's own client is never in it.</summary>
    public static IReadOnlyList<(string Process, string Name)> Known { get; } = new[]
    {
        ("Discord", "Discord"), ("DiscordPTB", "Discord"), ("DiscordCanary", "Discord"),
        ("NVIDIA Share", "NVIDIA overlay"), ("NVIDIA Overlay", "NVIDIA overlay"),
        ("RadeonSoftware", "AMD Software"), ("AMDRSServ", "AMD Software"),
        ("obs64", "OBS Studio"), ("obs32", "OBS Studio"),
        ("steam", "Steam"), ("GameOverlayUI", "Steam"),
        ("GameBar", "Xbox Game Bar"),
        ("ts3client_win64", "TeamSpeak"), ("TeamSpeak", "TeamSpeak"),
        ("mumble", "Mumble"),
        ("AutoHotkey", "AutoHotkey"), ("AutoHotkey64", "AutoHotkey"), ("AutoHotkey32", "AutoHotkey"),
        ("AutoHotkeyU64", "AutoHotkey"), ("AutoHotkeyU32", "AutoHotkey"),
        ("PowerToys", "PowerToys"),
        ("EVE-O Preview", "EVE-O Preview"), ("EVE-X-Preview", "EVE-X-Preview"),
    };

    /// <summary>The known programs among the running ones, each named once, in <see cref="Known"/>'s order. A
    /// process name with or without ".exe", in any case.</summary>
    public static IReadOnlyList<string> Suspects(IEnumerable<string> runningProcessNames)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in runningProcessNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var bare = name.Trim();
            if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) bare = bare[..^4];
            running.Add(bare);
        }
        var found = new List<string>();
        foreach (var (process, shown) in Known)
            if (running.Contains(process) && !found.Contains(shown)) found.Add(shown);
        return found;
    }

    /// <summary>The name of every running process, read once. For a background thread only: it is a snapshot of the
    /// whole system. A process that ends meanwhile is left out. Nothing of it is kept or logged; only names in
    /// <see cref="Known"/> are ever shown.</summary>
    public static IReadOnlyList<string> RunningProcessNames()
    {
        var names = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try { names.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }
        return names;
    }

    /// <summary>What a tray balloon holds.</summary>
    public const int MaxNoteLength = 255;

    /// <summary>What the tray's note says about hotkeys refused for the first time: the first three as the settings
    /// show them, each with what it is bound to ("Ctrl + F5 (Next client)"). Where that is too long for a balloon,
    /// the keys alone.</summary>
    /// <param name="actionOf">The name of what a hotkey is bound to, or null. A character's or a group's name can be
    /// among them: it is only ever an argument of the format.</param>
    public static string Note(IReadOnlyList<RefusedHotkey> fresh, Func<string, string?>? actionOf = null)
    {
        string Keys(bool withActions)
        {
            var names = fresh.Take(MaxNamed).Select(r =>
            {
                var key = HotkeyText.ToDisplay(r.Hotkey);
                var action = withActions ? actionOf?.Invoke(r.Hotkey) : null;
                return string.IsNullOrWhiteSpace(action) ? key : key + " (" + action + ")";
            });
            return string.Join(", ", names) + (fresh.Count > MaxNamed ? ", …" : "");
        }
        var format = LocalizationService.Str("L.Tray.KeysRefused",
            "Windows refused these hotkeys: {0}. Another program may have them. Click for details.");
        var note = string.Format(format, Keys(withActions: true));
        return note.Length <= MaxNoteLength ? note : string.Format(format, Keys(withActions: false));
    }
}

/// <summary>The hotkeys Windows refused the last time they were registered, as HotkeyService keeps them. The window's
/// thread only.</summary>
public sealed class RefusedHotkeyList
{
    private IReadOnlyList<RefusedHotkey> _keys = Array.Empty<RefusedHotkey>();
    private RefusedHotkey? _pauseKey;
    private string _signature = "";

    /// <summary>The refused hotkeys, each once (ignoring case): the pause key first, then the others in the order
    /// they were asked for.</summary>
    public IReadOnlyList<RefusedHotkey> Current { get; private set; } = Array.Empty<RefusedHotkey>();

    /// <summary>Whether the hotkeys have been registered since they were last all released. Until then an empty list
    /// means "not asked", not "all accepted": they are registered only while an EVE client is running.</summary>
    public bool Asked { get; private set; }

    /// <summary>Raised when <see cref="Current"/> is not what it was.</summary>
    public event Action? Changed;

    /// <summary>The hotkeys were registered, and these were refused (none: all accepted).</summary>
    public void Registered(IEnumerable<RefusedHotkey> refused)
    {
        _keys = refused.ToList();
        Asked = true;
        Publish();
    }

    /// <summary>The pause key was registered (null), or refused. It is registered apart from the others, and always.</summary>
    public void PauseKey(RefusedHotkey? refused)
    {
        _pauseKey = refused;
        Publish();
    }

    /// <summary>Every hotkey was released (a settings change, closing): nothing is known until they are registered
    /// again. It raises nothing: the registration that follows says what is so.</summary>
    public void Forget()
    {
        _keys = Array.Empty<RefusedHotkey>();
        _pauseKey = null;
        _signature = "";
        Current = Array.Empty<RefusedHotkey>();
        Asked = false;
    }

    private void Publish()
    {
        var all = new List<RefusedHotkey>();
        if (_pauseKey != null) all.Add(_pauseKey);
        foreach (var key in _keys)
            if (!all.Any(known => string.Equals(known.Hotkey.Trim(), key.Hotkey.Trim(), StringComparison.OrdinalIgnoreCase))) all.Add(key);
        var signature = string.Join("|", all.Select(r => r.Hotkey.Trim().ToUpperInvariant() + ":" + r.Error));
        if (signature == _signature) return;
        _signature = signature;
        Current = all;
        Changed?.Invoke();
    }
}

/// <summary>Tells each refused hotkey once a session: a key that stays refused is not brought up at every settings
/// change, and one that comes and goes with another program is not told twice. The card always shows what is so.</summary>
public sealed class RefusalAnnouncer
{
    private readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The refused hotkeys that were not told before. They count as told from now on.</summary>
    public IReadOnlyList<RefusedHotkey> New(IReadOnlyList<RefusedHotkey> refused) =>
        refused.Where(r => _told.Add(r.Hotkey.Trim())).ToList();
}
