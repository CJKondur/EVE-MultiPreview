using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #46: keeps Windows from going to sleep while a character is logged in and the switch is on. Only the
/// system is held (ES_SYSTEM_REQUIRED); the display is never asked for, so the screen still turns off on its own timer.
/// The call it makes is handed in (no default), so no test reaches the real one.
/// Windows keeps the execution state per thread: App calls <see cref="Follow"/> on the window's thread only. Windows
/// drops the state when that thread ends, so a crash lets go of it as well.</summary>
public sealed class KeepAwake
{
    public const uint EsContinuous = 0x80000000;
    public const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);

    private readonly Func<uint, uint> _setState;
    private readonly Action<Exception> _failed;
    private bool _wanted;
    private bool _refused;
    private bool _reported;

    public KeepAwake(Func<uint, uint> setState, Action<Exception> failed)
    {
        _setState = setState;
        _failed = failed;
    }

    /// <summary>True while Windows has said yes to the hold and it has not been let go.</summary>
    public bool Holding { get; private set; }

    /// <summary>Follows the wanted state: Windows is asked only when it changes. A refusal (the call answers 0) is
    /// reported once per session and asked again only after the wanted state went false and true again.</summary>
    public void Follow(bool wanted)
    {
        if (wanted != _wanted)
        {
            _wanted = wanted;
            _refused = false;
        }
        if (wanted)
        {
            if (Holding || _refused) return;
            if (_setState(EsContinuous | EsSystemRequired) != 0)
            {
                Holding = true;
                return;
            }
            _refused = true;
            if (_reported) return;
            _reported = true;
            _failed(new InvalidOperationException("Keep the PC awake: Windows refused the request"));
        }
        else if (Holding)
        {
            Holding = false;
            _setState(EsContinuous);
        }
    }

    /// <summary>The switch is on and at least one client has a character (a client at the login screen has none). The
    /// switch is read first, so the characters are not looked at while it is off.</summary>
    public static bool Wanted(AppSettings? s, IEnumerable<string> characters) =>
        s?.KeepPcAwake == true && characters.Any(n => !string.IsNullOrWhiteSpace(n));
}
