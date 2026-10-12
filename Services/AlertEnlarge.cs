using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What the player chose for enlarging a thumbnail on an alert (IDEAS #13): on or off, from which severity
/// (<see cref="AlertWays.Reaches"/>), how large in percent of the thumbnail's own size, and for how many seconds.
/// The two numbers are held to their ranges whatever the settings file says.</summary>
public readonly record struct EnlargeOptions(bool On, int From, int Percent, int Seconds)
{
    public const int MinPercent = 110, MaxPercent = 400, MinSeconds = 1, MaxSeconds = 60;

    public static EnlargeOptions Of(AppSettings s) => new(s.EnlargeOnAlert, s.EnlargeOnAlertFrom,
        Math.Clamp(s.EnlargeOnAlertPercent, MinPercent, MaxPercent), Math.Clamp(s.EnlargeOnAlertSeconds, MinSeconds, MaxSeconds));

    public double Scale => Percent / 100.0;
}

/// <summary>Enlarging a thumbnail on an alert (IDEAS #13), as a way an alert reaches the player: which alerts
/// enlarge their client's thumbnail, until when, and when it is over. It owns no window and no timer:
/// <see cref="Raised"/> notes an alert on the thread that raised it, and the thumbnail manager asks
/// <see cref="Tick"/> on a timer it runs anyway and tells the windows what Tick answers. The three rules the window
/// itself needs are here too, as functions without a window. Any thread.</summary>
public sealed class AlertEnlarge : IAlertWay
{
    /// <summary>What a tick asks of one character's thumbnail: this scale; 1.0 is "back to its own size".</summary>
    public readonly record struct Change(string Character, double Scale);

    private sealed class Held
    {
        public DateTime UntilUtc;
        public double Scale;
        public bool Told;   // its thumbnail was told to grow
    }

    private static readonly IReadOnlyList<Change> Nothing = Array.Empty<Change>();

    private readonly object _lock = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private int _count;   // how many characters are held; read without the lock

    public bool IsOn(AppSettings s) => s.EnlargeOnAlert;

    /// <summary>An alert was shown. Its client's thumbnail is to be enlarged until the set seconds after it; a
    /// thumbnail that is being enlarged already stays so until then, at the size it has. Not for the client in
    /// front: the player is looking at the real thing.</summary>
    public void Raised(AlertNotice notice, AppSettings s)
    {
        var options = EnlargeOptions.Of(s);
        if (!options.On || notice.ClientInFront || string.IsNullOrEmpty(notice.Character) || !AlertWays.Reaches(options.From, notice.Severity)) return;
        var until = notice.TimeUtc.AddSeconds(options.Seconds);
        lock (_lock)
        {
            if (_held.TryGetValue(notice.Character, out var held))
            {
                if (until > held.UntilUtc) held.UntilUtc = until;
                return;
            }
            _held[notice.Character] = new Held { UntilUtc = until, Scale = options.Scale };
            Volatile.Write(ref _count, _held.Count);
        }
    }

    /// <summary>Some thumbnail is enlarged or about to be. While this is false a tick has nothing to do.</summary>
    public bool Any => Volatile.Read(ref _count) > 0;

    /// <summary>One look, on the window's thread: which thumbnails to enlarge now, and which to put back. A
    /// thumbnail is put back when its time is over, when its client is in front, and when the feature was switched
    /// off; one whose thumbnail is gone is forgotten without a word. Each thumbnail is told to grow once and to go
    /// back once.</summary>
    /// <param name="clientInFront">Whether a character's client is the window in front.</param>
    /// <param name="hasThumbnail">Whether a character has a thumbnail (its client is running).</param>
    public IReadOnlyList<Change> Tick(DateTime utcNow, AppSettings s, Func<string, bool> clientInFront, Func<string, bool> hasThumbnail)
    {
        if (!Any) return Nothing;
        bool on = s.EnlargeOnAlert;
        List<Change>? changes = null;
        List<string>? over = null;
        lock (_lock)
        {
            foreach (var (character, held) in _held)
            {
                bool gone = !hasThumbnail(character);
                // The time is over. Or the clock was set back: an end further ahead than an enlargement can last is no end.
                bool ended = !on || utcNow >= held.UntilUtc || held.UntilUtc - utcNow > TimeSpan.FromSeconds(EnlargeOptions.MaxSeconds + 1);
                if (gone || ended || clientInFront(character))
                {
                    if (held.Told && !gone) (changes ??= new List<Change>()).Add(new Change(character, 1.0));
                    (over ??= new List<string>()).Add(character);
                }
                else if (!held.Told)
                {
                    held.Told = true;
                    (changes ??= new List<Change>()).Add(new Change(character, held.Scale));
                }
            }
            if (over != null)
            {
                foreach (var character in over) _held.Remove(character);
                Volatile.Write(ref _count, _held.Count);
            }
        }
        return changes ?? Nothing;
    }

    /// <summary>Where an enlarged thumbnail stands: its own rectangle grown by <paramref name="scale"/> around its
    /// middle, never larger than the monitor's work area (the scale is cut to what fits), and moved back inside the
    /// work area where it would stand out of it. A thumbnail with no room to grow is left as it is.</summary>
    public static Rectangle Place(Rectangle own, double scale, Rectangle workArea)
    {
        if (own.Width <= 0 || own.Height <= 0 || !(scale > 1.0)) return own;
        double fits = Math.Min(scale, Math.Min(workArea.Width / (double)own.Width, workArea.Height / (double)own.Height));
        if (!(fits > 1.0)) return own;
        int width = (int)(own.Width * fits), height = (int)(own.Height * fits);
        if (width <= own.Width && height <= own.Height) return own;
        int x = own.Left - (width - own.Width) / 2, y = own.Top - (height - own.Height) / 2;
        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Whether a point of the screen is on the thumbnail's own place. While a thumbnail is enlarged only
    /// that place takes the mouse: a click on the part that grew is not a click on this thumbnail, so a thumbnail
    /// that grows under the pointer never switches clients by accident.</summary>
    public static bool InOwnPlace(Rectangle own, int screenX, int screenY) => own.Contains(screenX, screenY);

    /// <summary>Whether a thumbnail that was asked for <paramref name="scale"/> is enlarged right now. Not while it
    /// is dragged or resized, and not on top of the hover zoom. And never changing under the pointer: with the
    /// pointer on it, it does not grow, and one that is enlarged stays so until it is asked back.</summary>
    public static bool Wanted(double scale, bool dragging, bool hoverZoomed, bool pointerOnIt, bool enlargedNow) =>
        scale > 1.0 && !dragging && !hoverZoomed && (enlargedNow || !pointerOnIt);
}
