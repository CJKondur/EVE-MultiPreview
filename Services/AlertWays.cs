using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>One alert that got past the gates, as a further way of telling the player gets it (IDEAS batch 6):
/// spoken, as a Windows notification, by an enlarged thumbnail. It has passed every gate an alert passes (the
/// event's switch, the character's client, the mute, the snooze), whether a toast showed it or not, and nothing in
/// it changes afterwards.</summary>
/// <param name="TimeUtc">When the ways were told of the alert: after the border, the badge, the toast and the sound,
/// and the toast and the sound wait for the window's thread. It is "now" for a way, not the alert's own time.</param>
/// <param name="ClientInFront">The character's client was the window in front at that moment.</param>
public sealed record AlertNotice(DateTime TimeUtc, string Character, string EventKey, string Severity, bool ClientInFront)
{
    /// <summary>When the alert was raised. For an alert raised from the log that is the time its history entry
    /// keeps, earlier than <see cref="TimeUtc"/> by as long as the alert waited for the window's thread on its way
    /// here. Where the raiser handed no time on, it is TimeUtc: "Left behind" hands none (it is raised and told on
    /// the window's thread in one go), so there it is not the history entry's time to the tick. Never later than
    /// TimeUtc: AlertWays holds it to that.</summary>
    public DateTime RaisedUtc { get; init; } = TimeUtc;
}

/// <summary>A further way an alert reaches the player. Both members are called on the thread that raised the alert:
/// the log reader's, or the window's for "Left behind". Neither may wait for anything: a way that drives a device
/// (a voice, Windows' notifications) hands the work to a <see cref="WorkPump"/>.</summary>
public interface IAlertWay
{
    /// <summary>Whether the player switched this way on. Asked for every alert that got past the gates: reads of
    /// settings and nothing else.</summary>
    bool IsOn(AppSettings s);

    /// <summary>An alert got past the gates. The way decides for itself whether it tells of this one (its severity,
    /// the client in front, its own quiet times).</summary>
    void Raised(AlertNotice notice, AppSettings s);
}

/// <summary>The further ways an alert reaches the player, told of an alert in one place: AlertDispatcher calls
/// <see cref="Raised"/> last, for an alert that got past the gates and for no other, so a character without a
/// client, a muted character and a snoozed event never get here. While no way is switched on, an alert costs one
/// read of an array here and nothing else. No window in it; any thread.</summary>
public sealed class AlertWays
{
    // Replaced whole by Add and never changed in place: Raised reads it on the log reader's thread and the window's.
    private IAlertWay[] _ways = Array.Empty<IAlertWay>();
    private readonly Action<Exception> _failed;
    private readonly Func<DateTime> _utcNow;

    /// <param name="failed">Is told that a way threw, or the question "is that client in front": what failed and the
    /// TYPE of what was thrown (with the type at its root, its error code and its stack: <see cref="Failure"/>),
    /// never its message. A message can hold a character's name (a table keyed by one says which key it missed),
    /// App writes what it is told to error_log.txt, and players post that file. It may throw itself: that is
    /// swallowed.</param>
    public AlertWays(Action<Exception>? failed = null, Func<DateTime>? utcNow = null)
    {
        _failed = failed ?? (_ => { });
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Adds a way. Called at startup, before the log monitor reads its first line.</summary>
    public void Add(IAlertWay way)
    {
        var old = Volatile.Read(ref _ways);
        var grown = new IAlertWay[old.Length + 1];
        Array.Copy(old, grown, old.Length);
        grown[old.Length] = way;
        Volatile.Write(ref _ways, grown);
    }

    public int Count => Volatile.Read(ref _ways).Length;

    /// <summary>Whether any way is switched on. A way whose answer throws counts as off, as in <see cref="Raised"/>:
    /// it is reported, and the ways behind it are still asked.</summary>
    public bool AnyOn(AppSettings s)
    {
        foreach (var way in Volatile.Read(ref _ways))
        {
            try
            {
                if (way.IsOn(s)) return true;
            }
            catch (Exception ex)
            {
                Report(NameOf(way), ex);
            }
        }
        return false;
    }

    /// <summary>An alert got past the gates. Each way that is switched on is told once. Which window is in front is
    /// asked once for all of them, and only when a way is on. What a way throws costs that way this alert, and
    /// nothing leaves here as an exception.</summary>
    /// <param name="clientInFront">Whether the character's client is the window in front (ThumbnailManager.InFront).</param>
    /// <param name="raisedUtc">When the alert was raised, where the raiser knows it: the notice's RaisedUtc. Without
    /// it that is the time the ways are told, as TimeUtc is. A time later than that (the PC's clock stepped back
    /// between the raise and the tell, or a caller handed a local time) is held to it: the earlier of the two is
    /// handed on, so no alert is younger than the moment the ways were told of it.</param>
    public void Raised(AppSettings s, string character, string eventKey, string severity, Func<bool> clientInFront, DateTime? raisedUtc = null)
    {
        var ways = Volatile.Read(ref _ways);
        AlertNotice? notice = null;
        foreach (var way in ways)
        {
            try
            {
                if (!way.IsOn(s)) continue;
                if (notice == null)
                {
                    var told = _utcNow();
                    var raised = raisedUtc is { } handed && handed < told ? handed : told;
                    notice = new AlertNotice(told, character, eventKey, severity, InFront(clientInFront)) { RaisedUtc = raised };
                }
                way.Raised(notice, s);
            }
            catch (Exception ex)
            {
                Report(NameOf(way), ex);
            }
        }
    }

    /// <summary>Asks "is that client in front" in a try of its own. When the question throws, it is reported and
    /// the answer is "no", so that every way is still told: an alert told once too often is better than one that
    /// reaches nobody. Every way reads the answer. With this "no" a notification is shown, and a larger thumbnail
    /// asked for, although the client may be in front. The voice reads the answer when a sentence's turn comes (App
    /// asks: was the alert on the client in front, or is it still unseen?), so with this "no" an alert on a client
    /// that really is in front, which nobody holds as unseen, is not spoken.</summary>
    private bool InFront(Func<bool> clientInFront)
    {
        try { return clientInFront(); }
        catch (Exception ex)
        {
            Report("asking whether the client is in front", ex);
            return false;
        }
    }

    /// <summary>A way's class name, for the report: with no message in it, that is what says which way failed. A
    /// way that is not there (null) has none, and naming it must not throw out of the catch that reports it.</summary>
    private static string NameOf(IAlertWay? way) => way?.GetType().Name ?? "a way";

    /// <summary>Tells the reporter what failed and the type of what was thrown. Never the exception itself: its
    /// message is not for the error log (see the constructor).</summary>
    private void Report(string what, Exception ex)
    {
        try { _failed(Failure("Alert ways: " + what, ex)); }
        catch { /* reporting must never cost the alert */ }
    }

    /// <summary>What is reported in place of something that was thrown, by AlertWays, by a WorkPump and by a way:
    /// what failed, the TYPE of what was thrown, the type at its root where that is another one (a wrapper such as
    /// AggregateException or TargetInvocationException says nothing by itself), and the root's error code (for a
    /// COMException it is what says which error of Windows it was). It carries the stack of the original, which
    /// names methods and no player. Never the original itself and never a message of it or of anything it wraps:
    /// neither may reach the error log (see the constructor). Never throws.</summary>
    /// <param name="what">What failed, as a fixed text of the caller's: "Alert ways: SpokenWay". " failed" and the
    /// types are put behind it.</param>
    public static Exception Failure(string what, Exception ex)
    {
        string types = "?", code = "?";
        string? stack = null;
        try
        {
            var root = ex.GetBaseException();
            types = ex.GetType().Name;
            if (!ReferenceEquals(root, ex)) types += " from " + root.GetType().Name;
            code = "0x" + root.HResult.ToString("X8", CultureInfo.InvariantCulture);
            stack = root.StackTrace is { Length: > 0 } deepest ? deepest : ex.StackTrace;
        }
        catch (Exception) { /* an exception that can't be asked is reported with what was learnt of it */ }
        var report = new InvalidOperationException(what + " failed (" + types + ", " + code + ")");
        try
        {
            if (!string.IsNullOrEmpty(stack)) ExceptionDispatchInfo.SetRemoteStackTrace(report, stack);
        }
        catch (Exception) { /* a report without a stack is still a report */ }
        return report;
    }

    /// <summary>Whether a way set to tell "from this severity" tells of an alert of <paramref name="severity"/>.
    /// 0: critical alerts only. 1: critical and warning. 2: every severity. Another number is held to that range. A
    /// severity nobody knows is told by none.</summary>
    public static bool Reaches(int from, string? severity) =>
        AlertAttention.Rank(severity) >= 3 - Math.Clamp(from, 0, 2);
}
