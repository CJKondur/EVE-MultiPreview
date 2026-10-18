using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What Discord last answered, for the card's last line. Holds no link and no name.</summary>
/// <param name="Status">The HTTP status (0 when none came).</param>
public sealed record PushStatus(PushOutcome Outcome, int Status, DateTime AtUtc);

/// <summary>Alerts to a Discord webhook as a way an alert reaches the player (IDEAS #2): the Discord app on the
/// player's phone shows them. An alert that got past the gates, and past this way's own (its severity, do-not-disturb,
/// "only while away"), goes to a <see cref="PushLedger"/>, which merges, paces and bounds what is sent; a
/// <see cref="WorkPump"/> sends it through an <see cref="IPushSender"/>, so an alert never waits for Discord. The link
/// is read through the handed-in <c>webhookOf</c> (WebhookSecret.Unprotect in the app) and never appears in a status,
/// a report or an exception text of this class. It is handed everything it uses (the sender, the clock, the waiting,
/// the idle time), so a test hands it fakes and no request leaves the PC.</summary>
public sealed class PushWay : IAlertWay
{
    /// <summary>With "Only while you're away from the PC" on, nothing is sent while Windows saw keyboard or mouse
    /// input less than this long ago.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(2);

    private readonly IPushSender _sender;
    private readonly Func<string?, Uri?> _webhookOf;
    private readonly Func<AppSettings, string, bool> _silenced;
    private readonly Func<TimeSpan?> _idle;
    private readonly Func<string, string> _eventName;
    private readonly Func<string, string, string> _str;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Action<Exception> _failed;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _followLock = new();

    private volatile AppSettings? _settings;    // the settings the drain reads: the last Raised's or Follow's
    private volatile PushStatus? _status;
    private volatile bool _stopped;
    private string? _followed;                  // the stored link text at the last Follow (under _followLock)
    // The drain's own (the pump runs one drain at a time): the link, read again only when the stored text changed.
    private string? _linkFor;
    private Uri? _link;
    private bool _linkRead;
    // Each kind of failure is reported once a session.
    private int _toldGone, _toldUnreachable, _toldRefused, _toldThrew;

    /// <param name="sender">Sends one message. App hands it one DiscordSender for the whole session; Stop disposes it.</param>
    /// <param name="webhookOf">The link behind the stored text (WebhookSecret.Unprotect), or null for none this PC can read.</param>
    /// <param name="silenced">(settings, severity): do-not-disturb holds an alert of that severity now.</param>
    /// <param name="idle">How long since Windows last saw input; null when Windows won't say (counts as away).</param>
    /// <param name="eventName">An event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="str">LocalizationService.Str.</param>
    /// <param name="wait">Waits a time, or until the way stops (Task.Delay; a test's moves its clock and ends at once).</param>
    /// <param name="failed">Is told, once a session for each kind, that Discord refused, could not be reached or says
    /// the webhook is gone (a fixed text with no link and no name), or that the sender threw (AlertWays.Failure: the
    /// type and error code, never the message, which can hold the link).</param>
    public PushWay(IPushSender sender, Func<string?, Uri?> webhookOf, Func<AppSettings, string, bool> silenced, Func<TimeSpan?> idle,
        Func<string, string> eventName, Func<string, string, string> str, Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null, Action<Exception>? failed = null)
    {
        _sender = sender;
        _webhookOf = webhookOf;
        _silenced = silenced;
        _idle = idle;
        _eventName = eventName;
        _str = str;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _wait = wait ?? Task.Delay;
        _failed = failed ?? (_ => { });
        Pump = new WorkPump(Drain, _failed);
    }

    /// <summary>What waits to be sent, and when it may go (tests look at it).</summary>
    public PushLedger Ledger { get; } = new();

    /// <summary>The pump that sends (tests wait for it to be idle).</summary>
    public WorkPump Pump { get; }

    /// <summary>What Discord last answered, an alert's message or the test's; null before the first.</summary>
    public PushStatus? Status => _status;

    public bool IsOn(AppSettings s) => s.PushAlerts && !string.IsNullOrEmpty(s.PushDiscordWebhook);

    public void Raised(AlertNotice notice, AppSettings s)
    {
        if (_stopped) return;
        if (!AlertWays.Reaches(s.PushAlertsFrom, notice.Severity)) return;
        if (_silenced(s, notice.Severity)) return;
        // A player at the PC sees the alert there; a Windows that won't say how long it is idle counts as away.
        if (s.PushOnlyWhenAway && _idle() is { } idle && idle < AwayAfter) return;
        _settings = s;
        if (Ledger.Raised(notice)) Pump.Wake();
    }

    /// <summary>Keeps the settings for the drain. A stored link text other than at the last call (a new link was
    /// saved, or the link removed) starts the sending again: not gone, no back-off. Switched off: nothing waits any
    /// more. App calls it at startup and after every settings change.</summary>
    public void Follow(AppSettings s)
    {
        if (_stopped) return;
        _settings = s;
        var stored = s.PushDiscordWebhook ?? "";
        lock (_followLock)
        {
            if (stored != _followed) Ledger.Reset();
            _followed = stored;
        }
        if (!IsOn(s)) Ledger.Clear();
    }

    /// <summary>The card's "Send a test message": one message now, outside the ledger (no pacing, no merging), and
    /// the status follows Discord's answer. Without a link this PC can read, or once stopped: Refused, and nothing is
    /// sent. Never throws.</summary>
    public async Task<PushResult> TestAsync(AppSettings s)
    {
        if (_stopped) return new PushResult(PushOutcome.Refused);
        Uri? link;
        try { link = _webhookOf(s.PushDiscordWebhook); }
        catch (Exception) { link = null; }
        if (link == null) return new PushResult(PushOutcome.Refused);
        PushResult result;
        try
        {
            result = await _sender.SendAsync(link, _str("L.Push.Test", "Test message from EVE MultiPreview: alerts will arrive here."),
                _cancel.Token).ConfigureAwait(false) ?? new PushResult(PushOutcome.Unreachable);
        }
        catch (Exception ex)
        {
            result = new PushResult(PushOutcome.Unreachable);
            if (!_stopped) Once(ref _toldThrew, () => AlertWays.Failure("Discord push: sending", ex));
            _status = new PushStatus(result.Outcome, result.Status, _utcNow());
            return result;
        }
        Answered(result);
        return result;
    }

    /// <summary>MultiPreview is closing: what is being sent is cancelled, the pump stops and the sender is disposed.
    /// Twice is once. Never throws: App calls it at exit, and everything below it there must still run.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            Pump.Stop();
            _cancel.Cancel();
            Ledger.Clear();
            (_sender as IDisposable)?.Dispose();
        }
        catch (Exception ex) { Tell(AlertWays.Failure("Discord push: stopping", ex)); }
    }

    /// <summary>How long since Windows last saw input: the difference of two Environment.TickCount values, across
    /// the counter's wrap. Null when Windows won't say; a negative difference (input after "now") is zero.</summary>
    public static TimeSpan? Idle(int nowTick, int? lastInputTick)
    {
        if (lastInputTick is not int last) return null;
        int since = unchecked(nowTick - last);
        return since <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(since);
    }

    /// <summary>The pump's work: sends what the ledger lets go, waiting where it says, until nothing waits.</summary>
    private async Task Drain()
    {
        var token = _cancel.Token;
        while (!_stopped)
        {
            if (_settings is not { } s) return;
            var stored = s.PushDiscordWebhook ?? "";
            if (!_linkRead || stored != _linkFor)
            {
                _link = _webhookOf(stored);
                _linkFor = stored;
                _linkRead = true;
            }
            if (_link is not { } link)
            {
                Ledger.Clear();     // no link this PC can read: nothing that waits can go
                return;
            }

            var batch = Ledger.Next(_utcNow(), out var wait);
            if (batch == null)
            {
                if (wait <= TimeSpan.Zero) return;
                try { await _wait(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                continue;
            }

            // Next took the lines out: whatever happens, the ledger is told how it went (Sent or Failed).
            var result = new PushResult(PushOutcome.Unreachable);
            Exception? thrown = null;
            try
            {
                result = await _sender.SendAsync(link, PushMessage.Build(batch, _eventName, _str), token).ConfigureAwait(false)
                         ?? new PushResult(PushOutcome.Unreachable);
            }
            catch (Exception ex)
            {
                result = new PushResult(PushOutcome.Unreachable);     // a sender that throws counts as out of reach
                thrown = ex;
            }
            finally
            {
                var now = _utcNow();
                if (result.Outcome == PushOutcome.Sent) Ledger.Sent(batch, now);
                else Ledger.Failed(batch, result, now);
            }
            if (_stopped) return;
            if (thrown != null)
            {
                _status = new PushStatus(result.Outcome, result.Status, _utcNow());
                Once(ref _toldThrew, () => AlertWays.Failure("Discord push: sending", thrown));
            }
            else Answered(result);
        }
    }

    /// <summary>The status follows Discord's answer, and a failure is reported once a session for its kind.</summary>
    private void Answered(PushResult result)
    {
        _status = new PushStatus(result.Outcome, result.Status, _utcNow());
        if (_stopped) return;
        string code = result.Status.ToString(CultureInfo.InvariantCulture);
        switch (result.Outcome)
        {
            case PushOutcome.Gone:
                Once(ref _toldGone, () => new InvalidOperationException(
                    "Discord push: Discord says the webhook does not exist (" + code + "); nothing more is sent until a new link is saved."));
                break;
            case PushOutcome.Unreachable:
                Once(ref _toldUnreachable, () => new InvalidOperationException("Discord push: Discord could not be reached."));
                break;
            case PushOutcome.Refused:
                Once(ref _toldRefused, () => new InvalidOperationException("Discord push: Discord refused a message (" + code + ")."));
                break;
        }
    }

    private void Once(ref int told, Func<Exception> report)
    {
        if (Interlocked.Exchange(ref told, 1) == 0) Tell(report());
    }

    /// <summary>Tells the reporter a fixed text or a Failure, never an exception of the sender's itself.</summary>
    private void Tell(Exception fixedText)
    {
        try { _failed(fixedText); }
        catch (Exception) { /* reporting must never cost the alert */ }
    }
}
