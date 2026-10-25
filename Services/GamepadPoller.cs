using System;
using System.Collections.Generic;
using EveMultiPreview.Interop;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #43: the buttons of a game pad that can be bound to a switch. The order here is the order two buttons pressed
/// at once are handed on in. The two triggers and the sticks are not buttons and are never read.</summary>
public enum GamepadButton { A, B, X, Y, LeftShoulder, RightShoulder, Back, Start, DPadUp, DPadDown, DPadLeft, DPadRight, LeftThumb, RightThumb }

/// <summary>IDEAS #43: the buttons held right now, one bit each as XInput reports them (<see cref="GamepadButtons.Mask"/>).</summary>
public readonly record struct GamepadState(ushort Buttons);

/// <summary>IDEAS #43: where a pad's buttons come from. Null means no pad on that slot. The real one is
/// <see cref="XInputSource"/>; a test hands in its own.</summary>
public interface IGamepadSource
{
    GamepadState? Read(int user);
}

public static class GamepadButtons
{
    /// <summary>The slots XInput has.</summary>
    public const int Users = 4;

    /// <summary>The bit of a button in XInput's button word.</summary>
    public static ushort Mask(GamepadButton button) => button switch
    {
        GamepadButton.A => 0x1000,
        GamepadButton.B => 0x2000,
        GamepadButton.X => 0x4000,
        GamepadButton.Y => 0x8000,
        GamepadButton.LeftShoulder => 0x0100,
        GamepadButton.RightShoulder => 0x0200,
        GamepadButton.Back => 0x0020,
        GamepadButton.Start => 0x0010,
        GamepadButton.DPadUp => 0x0001,
        GamepadButton.DPadDown => 0x0002,
        GamepadButton.DPadLeft => 0x0004,
        GamepadButton.DPadRight => 0x0008,
        GamepadButton.LeftThumb => 0x0040,
        GamepadButton.RightThumb => 0x0080,
        _ => 0,
    };
}

/// <summary>IDEAS #43: the real pad. It only ever reads (one XInputGetState call per slot asked); nothing is sent to the pad
/// or anywhere else. A PC without the XInput library has no pad: the first failed read ends the reading for good.</summary>
public sealed class XInputSource : IGamepadSource
{
    private bool _unavailable;

    public GamepadState? Read(int user)
    {
        if (_unavailable || user < 0 || user >= GamepadButtons.Users) return null;
        try
        {
            return XInput.XInputGetState((uint)user, out var state) == 0 ? new GamepadState(state.Gamepad.wButtons) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            return null;
        }
    }
}

/// <summary>IDEAS #43: turns a pad's buttons held into the buttons newly pressed since the last poll (an edge, never a level).
/// It uses the first pad that is connected; a held button fires once; a press within 150 ms of the same button's last firing
/// is bounce and is dropped. A pad that goes away, and one that comes back, start from what is held at that moment: a button
/// still down on reconnect does not fire. With no pad the four slots are looked at once in two seconds, not once per poll
/// (a read of an empty slot can be slow); a pad that is there costs one read per poll. The clock is handed in.</summary>
public sealed class GamepadPoller
{
    public const int DebounceMs = 150;
    public const int ScanIntervalMs = 2000;

    private static readonly GamepadButton[] Order = Enum.GetValues<GamepadButton>();
    private static readonly IReadOnlyList<GamepadButton> None = Array.Empty<GamepadButton>();

    private readonly IGamepadSource _source;
    private readonly Func<long> _tickMs;
    private readonly long[] _lastFired = new long[Order.Length];
    private int _active = -1;
    private ushort _held;
    private long _nextScan = long.MinValue;

    public GamepadPoller(IGamepadSource source, Func<long> tickMs)
    {
        _source = source;
        _tickMs = tickMs;
        Array.Fill(_lastFired, long.MinValue / 2);
    }

    /// <summary>The buttons newly pressed since the last poll, in <see cref="GamepadButton"/> order.</summary>
    public IReadOnlyList<GamepadButton> Poll()
    {
        long now = _tickMs();
        if (_active < 0)
        {
            if (now < _nextScan) return None;
            for (int user = 0; user < GamepadButtons.Users; user++)
            {
                if (_source.Read(user) is not { } found) continue;
                _active = user;
                _held = found.Buttons;   // what is down as the pad is found is not a press
                return None;
            }
            _nextScan = now + ScanIntervalMs;
            return None;
        }

        if (_source.Read(_active) is not { } state)
        {
            _active = -1;
            _held = 0;
            _nextScan = now + ScanIntervalMs;
            return None;
        }

        ushort pressed = (ushort)(state.Buttons & ~_held);
        _held = state.Buttons;
        if (pressed == 0) return None;
        List<GamepadButton>? fired = null;
        foreach (var button in Order)
        {
            if ((pressed & GamepadButtons.Mask(button)) == 0 || now - _lastFired[(int)button] < DebounceMs) continue;
            _lastFired[(int)button] = now;
            (fired ??= new List<GamepadButton>()).Add(button);
        }
        return fired ?? None;
    }
}
