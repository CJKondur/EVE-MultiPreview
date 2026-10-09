namespace EveMultiPreview.Services;

/// <summary>IDEAS #41: decides when Windows has silently removed a low-level hook. Windows drops a hook whose callback
/// runs past LowLevelHooksTimeout and never says so; every feature on it (mouse bindings, hidden hotkey keystrokes,
/// held-key hiding) then stops until a restart. Each hook callback stamps the time (its only extra work), and this
/// compares the stamps with input Windows saw. Pure: HotkeyService passes in what it read, so tests need no hook.
///
/// Each hook is judged by input from its own device. Windows' last-input time alone can't tell a key from a mouse
/// move, so it would reinstall the mouse hook whenever the player typed and the keyboard hook whenever they moved the
/// mouse:
/// - The mouse hook is missing input when the cursor moved and Windows saw input since the last look, yet the hook
///   wasn't called once in that time, on two looks in a row.
/// - The keyboard hook is missing input when a hotkey whose keystroke it should have hidden arrived the ordinary
///   RegisterHotKey way and the hook hasn't been called for 2 seconds.
/// No input means no evidence, so an idle PC never triggers it.</summary>
public sealed class HookWatchdog
{
    /// <summary>How often HotkeyService looks at the mouse hook (a timer on the UI thread).</summary>
    public const int MouseLookMs = 2000;
    /// <summary>Looks in a row that must find the mouse hook missing input.</summary>
    public const int LooksToAct = 2;
    /// <summary>How long the keyboard hook may go without a call before an unhidden hotkey counts against it.</summary>
    public const int KeyQuietMs = 2000;
    /// <summary>At most one reinstall a minute per hook, and 20 a session: a false alarm can't loop.</summary>
    public const int MinMsBetweenReinstalls = 60_000;
    public const int MaxReinstallsPerSession = 20;
    /// <summary>Two looks further apart than this (a sleep, a stalled UI thread) prove nothing about the time between:
    /// the count starts over.</summary>
    public const int MaxLookGapMs = 3 * MouseLookMs;

    /// <summary>One look at the mouse hook, taken on the UI thread. Times are Environment.TickCount milliseconds.</summary>
    /// <param name="Installed">A hook thread is supposed to be running (not paused without a pause button, not capturing).</param>
    /// <param name="Generation">Bumps every time the service installs or removes the hook.</param>
    /// <param name="LastCallback">The callback's last stamp (set at install too).</param>
    /// <param name="LastInput">GetLastInputInfo, or null when Windows didn't say.</param>
    /// <param name="Cursor">GetCursorPos, or null when Windows didn't say (a locked screen).</param>
    /// <param name="WindowInFront">A window is in front whose input this process's hooks can see: not the lock screen or
    /// a UAC prompt (no foreground window), and not an elevated window, where no hook of a normal process sees input.</param>
    public readonly record struct MouseLook(int Now, bool Installed, int Generation, int LastCallback, int? LastInput,
        (int X, int Y)? Cursor, bool WindowInFront);

    private MouseLook? _previous;
    private int _suspicious;
    private int? _lastReinstall;

    /// <summary>Reinstalls allowed so far this session.</summary>
    public int Reinstalls { get; private set; }

    /// <summary>The session's reinstall allowance is used up.</summary>
    public bool LimitReached => Reinstalls >= MaxReinstallsPerSession;

    /// <summary>True when the mouse hook has missed input on <see cref="LooksToAct"/> looks in a row. Any install or
    /// removal by the service (pause, resume, settings, a hotkey capture, a reinstall) starts the count again, and so
    /// does a gap between looks longer than <see cref="MaxLookGapMs"/>.</summary>
    public bool MouseLooksDead(MouseLook now)
    {
        var previous = _previous;
        _previous = now;
        if (!now.Installed || previous is not { } p || !p.Installed || p.Generation != now.Generation
            || After(now.Now, unchecked(p.Now + MaxLookGapMs)))
        {
            _suspicious = 0;
            return false;
        }
        bool cursorMoved = now.Cursor is { } c && p.Cursor is { } pc && c != pc;
        bool inputSince = now.LastInput is int input && After(input, p.Now);
        bool calledSince = After(now.LastCallback, p.Now);
        _suspicious = now.WindowInFront && cursorMoved && inputSince && !calledSince ? _suspicious + 1 : 0;
        if (_suspicious < LooksToAct) return false;
        _suspicious = 0;
        return true;
    }

    /// <summary>True when a hotkey the keyboard hook should have hidden arrived without it, and the hook hasn't been called
    /// for <see cref="KeyQuietMs"/>: the keystroke that sent the hotkey went past a hook that is no longer there. A hook
    /// installed under 2 seconds ago isn't judged (the key may have been down before it).</summary>
    public static bool KeyHookMissedPress(int now, int lastCallback, int installedAt) =>
        After(now, unchecked(installedAt + KeyQuietMs)) && After(now, unchecked(lastCallback + KeyQuietMs));

    /// <summary>Counts a reinstall when the limits allow one (once a minute, 20 a session).</summary>
    public bool MayReinstall(int now)
    {
        if (Reinstalls >= MaxReinstallsPerSession) return false;
        if (_lastReinstall is int last && !After(now, unchecked(last + MinMsBetweenReinstalls))) return false;
        _lastReinstall = now;
        Reinstalls++;
        return true;
    }

    /// <summary>a is later than b on the 32-bit millisecond clock, which wraps every 49.7 days.</summary>
    public static bool After(int a, int b) => unchecked(a - b) > 0;
}
