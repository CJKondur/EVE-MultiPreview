using System;
using System.Collections.Generic;
using System.Globalization;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #45: the numbers of the client-memory event, each clamped to its range. The limit is the working set (RAM)
/// in MB; the growth is how far above the lowest per-minute sample of the last <see cref="Minutes"/> minutes a client may be.</summary>
public readonly record struct MemoryOptions(int LimitMb, int GrowthMb, int Minutes)
{
    public const int MinLimitMb = 512, MaxLimitMb = 65_536, DefaultLimitMb = 6144;
    public const int MinGrowthMb = 128, MaxGrowthMb = 32_768, DefaultGrowthMb = 1024;
    public const int MinMinutes = 15, MaxMinutes = 720, DefaultMinutes = 120;

    public static MemoryOptions Default { get; } = new(DefaultLimitMb, DefaultGrowthMb, DefaultMinutes);

    public static MemoryOptions Held(int limitMb, int growthMb, int minutes) => new(
        Math.Clamp(limitMb, MinLimitMb, MaxLimitMb),
        Math.Clamp(growthMb, MinGrowthMb, MaxGrowthMb),
        Math.Clamp(minutes, MinMinutes, MaxMinutes));
}

/// <summary>One client to tell the player about; <see cref="Text"/> is numbers and units only, no language.</summary>
public sealed record MemoryAlert(string Character, string Text);

/// <summary>IDEAS #45: decides which client to tell about its memory. Two rules: the working set at or above the limit (told
/// once, again only after it came to 90 % of the limit or below, or after a relog), and growth over a window (the working
/// set now is the growth amount above the lowest per-minute sample, the samples covering the whole window). The time is
/// handed in, so nothing here reads a clock. Per-minute lows bound the state to <see cref="MemoryOptions.Minutes"/> entries
/// per client; the first <see cref="LoadingMinutes"/> minutes of a client are not sampled because a client loads 1-2 GB
/// right after it starts, which is no leak. A client no longer handed in is forgotten.</summary>
public sealed class MemoryWatch
{
    public const string EventKey = "client_memory", Severity = "warning";
    public const int LoadingMinutes = 10;

    private sealed class State
    {
        public int Pid, Seen;
        public DateTime FirstSeen, LastSample;
        public bool LimitTold;
        /// <summary>The low of each minute, oldest first, the minutes counted from the first sample kept.</summary>
        public readonly List<(DateTime Minute, long LowMb)> Lows = new();
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _gone = new();
    private int _look;

    /// <summary>Judges every client handed in at <paramref name="utcNow"/> and returns the ones to tell, at most one each.</summary>
    public IReadOnlyList<MemoryAlert> Check(DateTime utcNow, IEnumerable<(string Character, int Pid, long RamMb)> clients, MemoryOptions options)
    {
        _look++;
        List<MemoryAlert>? told = null;
        foreach (var (character, pid, ram) in clients)
        {
            if (!_states.TryGetValue(character, out var s) || s.Pid != pid)
                _states[character] = s = new State { Pid = pid, FirstSeen = utcNow };
            else if (utcNow < s.LastSample)
                s.Lows.Clear();                                           // the clock was set back: the window starts again
            s.LastSample = utcNow;
            s.Seen = _look;

            string? text = null;
            if (ram >= options.LimitMb && !s.LimitTold)
            {
                s.LimitTold = true;
                text = string.Create(CultureInfo.InvariantCulture, $"{ram} MB ≥ {options.LimitMb} MB");
            }
            else if (s.LimitTold && ram * 10 <= (long)options.LimitMb * 9)
                s.LimitTold = false;

            long growth = Sample(s, utcNow, ram, options);
            if (growth >= options.GrowthMb && text == null)
            {
                text = string.Create(CultureInfo.InvariantCulture, $"+{growth} MB / {options.Minutes} min");
                s.Lows.Clear();
            }
            if (text != null) (told ??= new List<MemoryAlert>()).Add(new MemoryAlert(character, text));
        }

        _gone.Clear();
        foreach (var kv in _states)
            if (kv.Value.Seen != _look) _gone.Add(kv.Key);
        foreach (var name in _gone) _states.Remove(name);
        return told ?? (IReadOnlyList<MemoryAlert>)Array.Empty<MemoryAlert>();
    }

    /// <summary>Forgets every client (the event was switched off).</summary>
    public void Forget() => _states.Clear();

    /// <summary>Keeps the low of the current minute, drops minutes more than a minute older than the window (so at most Minutes + 2 are kept), and returns the growth over the
    /// lowest kept minute when the oldest kept minute is at least the window old; otherwise 0.</summary>
    private static long Sample(State s, DateTime now, long ram, MemoryOptions o)
    {
        if (now - s.FirstSeen < TimeSpan.FromMinutes(LoadingMinutes)) return 0;
        var lows = s.Lows;
        int last = lows.Count - 1;
        if (last < 0) lows.Add((now, ram));
        else if (now - lows[last].Minute < OneMinute)
        {
            if (ram < lows[last].LowMb) lows[last] = (lows[last].Minute, ram);
        }
        else lows.Add((lows[last].Minute + OneMinute * Math.Floor((now - lows[last].Minute) / OneMinute), ram));

        var window = TimeSpan.FromMinutes(o.Minutes);
        int old = 0;
        while (old < lows.Count && now - lows[old].Minute > window + OneMinute) old++;   // a minute's grace: real timestamps never land on the grid
        if (old > 0) lows.RemoveRange(0, old);
        if (lows.Count == 0 || now - lows[0].Minute < window) return 0;

        long low = long.MaxValue;
        foreach (var e in lows) if (e.LowMb < low) low = e.LowMb;
        return ram - low;
    }
}
