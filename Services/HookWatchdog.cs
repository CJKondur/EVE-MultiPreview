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
    /// <param name="BoundButtonDown">A mouse button the hook has a binding for is physically down. A new hook would
    /// swallow its release and strand the button down in the client, so nothing is reinstalled until it is up.</param>
    /// <param name="CursorWindowVisible">The window under the cursor passes the same test as the one in front: hovering
    /// an elevated window on another monitor legitimately produces no hook calls.</param>
    public readonly record struct MouseLook(int Now, bool Installed, int Generation, int LastCallback, int? LastInput,
        (int X, int Y)? Cursor, bool WindowInFront, bool BoundButtonDown, bool CursorWindowVisible);

    /// <summary>One look at the keyboard hook, taken on the UI thread when a hotkey arrived without the hook's mark.</summary>
    /// <param name="MessageTime">When the system posted the WM_HOTKEY (GetMessageTime), not when it was handled: a UI
    /// thread that was busy must not make a healthy hook look silent. A live hook stamps before the OS posts it.</param>
    /// <param name="LastCallback">The hook's last stamp.</param>
    /// <param name="InstalledAt">When the hook was installed (TickCount).</param>
    /// <param name="Generation">Bumps every time the service installs or removes the keyboard hook.</param>
    /// <param name="Installed">A keyboard hook thread is supposed to be running.</param>
    /// <param name="SlotIsHidden">The hotkey is one whose keystroke the hook should have hidden.</param>
    /// <param name="HeldKeyHidden">A key hidden after a switch is still down (removing the hook would strand it).</param>
    /// <param name="HooksSeeForeground">The window in front is one whose input the hooks can see.</param>
    /// <param name="NoForeground">No window is in front at all (GetForegroundWindow gave zero): Windows is between two
    /// windows in a switch, or the screen is locked. Not the same as a window the hooks can't see: it says nothing
    /// about the hook, in either direction.</param>
    public readonly record struct KeyLook(int MessageTime, int LastCallback, int InstalledAt, int Generation, bool Installed,
        bool Suspended, bool Capturing, bool Disposed, bool SlotIsHidden, bool HeldKeyHidden, bool HooksSeeForeground,
        bool NoForeground);

    private (int Generation, int Stamp, int At)? _keyStrike;

    private MouseLook? _previous;
    private int _suspicious;
    private int? _lastReinstall;

    /// <summary>Reinstalls allowed so far this session.</summary>
    public int Reinstalls { get; private set; }

    /// <summary>The session's reinstall allowance is used up.</summary>
    public bool LimitReached => Reinstalls >= MaxReinstallsPerSession;

    /// <summary>Forget a keyboard strike: the hook is gone, or was heard from.</summary>
    public void ForgetKeyStrike() => _keyStrike = null;

    /// <summary>The service may still reinstall the mouse hook after a dead verdict: it was not disposed, no hotkey capture
    /// started, and the hook is the same one the look saw (not removed, not installed again) since.</summary>
    public static bool MouseMayReinstall(bool disposed, bool capturing, bool installedNow, int generationNow, int generationLooked) =>
        !disposed && !capturing && installedNow && generationNow == generationLooked;

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
        bool calledSince = !After(p.Now, now.LastCallback);   // a stamp in the same millisecond as the last look counts as a call
        _suspicious = now.WindowInFront && now.CursorWindowVisible && cursorMoved && inputSince && !calledSince ? _suspicious + 1 : 0;
        if (_suspicious < LooksToAct) return false;
        if (now.BoundButtonDown) return false;   // keeps the count: the next look tries again, with the button up
        _suspicious = 0;
        return true;
    }

    /// <summary>True when a hotkey the keyboard hook should have hidden came without it, twice: the second unmarked hotkey
    /// that arrives at least <see cref="KeyQuietMs"/> after the first, with the hook's stamp unchanged in between and no
    /// install or removal in between. One strike alone proves nothing (hotkeys queued behind an elevated window reach a
    /// healthy hook late). Nothing counts while the hook is removed on purpose (<c>Suspended</c>, <c>Capturing</c>,
    /// <c>Disposed</c>, not <c>Installed</c>), while a hidden key is held, while the window in front is one the hooks
    /// can't see (that also clears a strike), or while there is no window in front at all (that keeps it).</summary>
    public bool KeyHookLooksDead(KeyLook k)
    {
        if (k.Disposed || k.Suspended || k.Capturing || !k.Installed)
        {
            _keyStrike = null;
            return false;
        }
        if (!KeyHookMissedPress(k.MessageTime, k.LastCallback, k.InstalledAt))
        {
            _keyStrike = null;
            return false;
        }
        // A hotkey the hook doesn't hide (the app's own vk0xE8 activation bridge) is no evidence either way: it leaves
        // a strike from a real one alone. A live hook stamps on it, and a stamp change resets the strike anyway.
        if (!k.SlotIsHidden) return false;
        // No window in front for a moment (mid-switch): no evidence either way, so a strike from before stays.
        if (k.NoForeground) return false;
        if (!k.HooksSeeForeground)
        {
            _keyStrike = null;   // an elevated window in front: the hooks are expected to be silent, and what came before proves nothing
            return false;
        }
        if (k.HeldKeyHidden) return false;
        if (_keyStrike is { } s && s.Generation == k.Generation && s.Stamp == k.LastCallback)
        {
            if (!After(k.MessageTime, unchecked(s.At + KeyQuietMs))) return false;   // too soon after the first: keep that one
            _keyStrike = null;
            return true;
        }
        _keyStrike = (k.Generation, k.LastCallback, k.MessageTime);
        return false;
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
