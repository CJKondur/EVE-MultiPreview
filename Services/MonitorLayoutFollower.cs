using System;
using System.Collections.Generic;
using System.Drawing;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #10: when the monitors change and settle, applies the layout saved for the arrangement that is there
/// now. Windows raises DisplaySettingsChanged several times for one plug or unplug, so a change only starts a wait
/// (<see cref="Settle"/>); each further change moves the end of the wait back. When the wait is over the monitors are
/// read once and, if the arrangement differs from the one last handled and a layout was saved for it, that layout is
/// applied. With the switch off nothing is read, scheduled or applied. Every outside thing is handed in (the switch, the
/// monitors, the clock, the timer, the layouts, the apply), so tests need no screen: ThumbnailManager wires the real ones.</summary>
public sealed class MonitorLayoutFollower
{
    /// <summary>How long the monitors must stay unchanged before the layout is applied.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<Rectangle>> _screens;
    private readonly Func<DateTime> _clock;
    private readonly Action<TimeSpan> _schedule;
    private readonly Func<string, LayoutPreset?> _find;
    private readonly Action<LayoutPreset> _apply;
    private DateTime? _due;
    private string? _handled;

    /// <param name="enabled">The switch (AutoLayoutOnMonitorChange), asked each time.</param>
    /// <param name="screens">The monitors' bounds now (Screen.AllScreens' Bounds).</param>
    /// <param name="clock">Now. Any steady clock; only differences are used.</param>
    /// <param name="schedule">Calls <see cref="Tick"/> once after this long, replacing a call asked for before.</param>
    /// <param name="find">The layout saved for an arrangement (<see cref="LayoutPresetService.FindForFingerprint"/>), or null.</param>
    /// <param name="apply">Applies a layout (ThumbnailManager.ApplyLayoutPreset).</param>
    public MonitorLayoutFollower(Func<bool> enabled, Func<IReadOnlyList<Rectangle>> screens, Func<DateTime> clock,
        Action<TimeSpan> schedule, Func<string, LayoutPreset?> find, Action<LayoutPreset> apply)
    {
        _enabled = enabled;
        _screens = screens;
        _clock = clock;
        _schedule = schedule;
        _find = find;
        _apply = apply;
    }

    /// <summary>True while a change is waiting to settle.</summary>
    public bool Waiting => _due != null;

    /// <summary>The arrangement the monitors are in now becomes the one last handled: a change that ends up in the same
    /// arrangement (a refresh rate, a colour depth) then applies nothing. Called once at start.</summary>
    public void Start() => _handled = MonitorFingerprint.Of(_screens());

    /// <summary>The monitors changed (DisplaySettingsChanged, on the UI thread). Off: forgets the arrangement last handled
    /// (the switch may have been off while the monitors changed), so the first change after the switch is on applies.</summary>
    public void Changed()
    {
        if (!_enabled()) { _handled = null; _due = null; return; }
        _due = _clock() + Settle;
        _schedule(Settle);
    }

    /// <summary>The timer's tick. Too early (a later change moved the end of the wait back): waits for the rest.</summary>
    public void Tick()
    {
        if (_due is not { } due) return;
        var left = due - _clock();
        if (left > TimeSpan.Zero) { _schedule(left); return; }
        _due = null;
        if (!_enabled()) return;
        var now = MonitorFingerprint.Of(_screens());
        if (now == _handled) return;
        _handled = now;
        var preset = _find(now);
        if (preset != null) _apply(preset);
    }
}
