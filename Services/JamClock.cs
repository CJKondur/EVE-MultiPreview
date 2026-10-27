using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>Plan 40: when each character was last jammed, so the live-state reply can say how much of a 20 s jam is left (its
/// <c>j=</c> line). The log reader notes a jam at the moment its line arrives and every new line restarts the 20 s; the reply asks
/// how many milliseconds are left. The reader and the window's thread both use it, so every member locks. The time is handed in by
/// the caller (the reader's UTC <c>AlertClock</c>), never read here, so a test uses a fake clock. A moment that lies ahead of now
/// (the PC's clock was set back) counts as over: the player is never shown a jam that cannot end.</summary>
public sealed class JamClock
{
    /// <summary>How long a jam lasts, in milliseconds.</summary>
    public const int DurationMs = 20_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _since = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character was jammed at <paramref name="now"/>: its 20 s start again. Characters whose jam is over are forgotten.</summary>
    public void Jammed(string character, DateTime now)
    {
        if (string.IsNullOrEmpty(character)) return;
        lock (_gate)
        {
            if (_since.Count >= 16) Forget(now);
            _since[character] = now;
        }
    }

    /// <summary>The milliseconds left of the character's jam at <paramref name="now"/>: 1 to <see cref="DurationMs"/>, or 0 when it
    /// was never jammed or the jam is over.</summary>
    public long RemainingMs(string character, DateTime now)
    {
        if (string.IsNullOrEmpty(character)) return 0;
        lock (_gate)
        {
            return _since.TryGetValue(character, out var since) ? Left(since, now) : 0;
        }
    }

    private static long Left(DateTime since, DateTime now)
    {
        var passed = now - since;
        if (passed < TimeSpan.Zero) return 0;
        long left = DurationMs - (long)passed.TotalMilliseconds;
        return left > 0 ? left : 0;
    }

    private void Forget(DateTime now)
    {
        var over = new List<string>();
        foreach (var (name, since) in _since)
            if (Left(since, now) == 0) over.Add(name);
        foreach (var name in over) _since.Remove(name);
    }
}
