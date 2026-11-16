using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What a <see cref="PushWay"/> needs to know of the service it sends to (Discord, ntfy): which settings switch
/// it on and gate it, where a message goes, and how one is sent. The way keeps everything else (the gates, the
/// <see cref="PushLedger"/>, the <see cref="WorkPump"/>, the status and the reports), so each service gets the same
/// merging, pacing and back-off. A channel never puts its destination (a link, a topic, a token) in an exception or a
/// text it returns.</summary>
public interface IPushChannel : IDisposable
{
    /// <summary>The service's name in the error log's fixed texts ("Discord", "ntfy").</summary>
    string Name { get; }

    /// <summary>Switched on, with a destination saved. Reads of settings and nothing else.</summary>
    bool IsOn(AppSettings s);

    /// <summary>The "from which severity" setting (0 critical only, 1 critical and warning, 2 all).</summary>
    int From(AppSettings s);

    /// <summary>The "only while you're away from the PC" setting.</summary>
    bool OnlyWhenAway(AppSettings s);

    /// <summary>The player left this event's switch on in the way's column of Alerts › Events (PushEvents).</summary>
    bool Sends(AppSettings s, string eventKey);

    /// <summary>The stored texts that make up the destination, as one text. When it changes, the way starts the
    /// sending again (not gone, no back-off) and reads the destination anew. It goes nowhere else.</summary>
    string Stored(AppSettings s);

    /// <summary>Where messages go, read from the stored texts; null when there is none this PC can read. Never
    /// throws. What it returns is handed back to <see cref="SendAsync"/> and <see cref="TestAsync"/> unchanged.</summary>
    object? Destination(AppSettings s);

    /// <summary>An alert was taken (any thread): the channel may note what it needs of it now, such as the place.</summary>
    void Taken(AlertNotice notice);

    /// <summary>Sends one message of the batch's lines. Failures are returned; a throw counts as out of reach.</summary>
    Task<PushResult> SendAsync(object destination, PushBatch batch, AppSettings s, CancellationToken cancel);

    /// <summary>Sends the test message. Failures are returned; a throw counts as out of reach.</summary>
    Task<PushResult> TestAsync(object destination, CancellationToken cancel);

    /// <summary>The fixed text for "the service says the destination does not exist", with the HTTP status.</summary>
    string GoneText(string code);
}

/// <summary>IDEAS #2: the Discord webhook as a <see cref="PushWay"/>'s channel. The link is read through the handed-in
/// <c>webhookOf</c> (WebhookSecret.Unprotect, WebhookFile.Read on Linux); a message is <see cref="PushMessage"/>'s text
/// with the owner's mention on one that holds a critical line, sent by an <see cref="IPushSender"/>.</summary>
public sealed class DiscordChannel : IPushChannel
{
    private readonly IPushSender _sender;
    private readonly Func<string?, Uri?> _webhookOf;
    private readonly Func<string, string> _eventName;
    private readonly Func<string, string, string> _str;

    public DiscordChannel(IPushSender sender, Func<string?, Uri?> webhookOf, Func<string, string> eventName, Func<string, string, string> str)
    {
        _sender = sender;
        _webhookOf = webhookOf;
        _eventName = eventName;
        _str = str;
    }

    public string Name => "Discord";

    public bool IsOn(AppSettings s) => s.PushAlerts && !string.IsNullOrEmpty(s.PushDiscordWebhook);

    public int From(AppSettings s) => s.PushAlertsFrom;

    public bool OnlyWhenAway(AppSettings s) => s.PushOnlyWhenAway;

    public bool Sends(AppSettings s, string eventKey) => PushEvents.Sends(s.PushAlertTypes, eventKey);

    public string Stored(AppSettings s) => s.PushDiscordWebhook ?? "";

    public object? Destination(AppSettings s)
    {
        try { return _webhookOf(s.PushDiscordWebhook); }
        catch (Exception) { return null; }
    }

    public void Taken(AlertNotice notice) { }

    public Task<PushResult> SendAsync(object destination, PushBatch batch, AppSettings s, CancellationToken cancel)
    {
        // The owner's @here / @everyone goes only on a message that holds a critical line (IDEAS #2, batch 30); the token
        // is put there by PushMessage.WithMention, the text of a name never makes one. A stored number outside 1 and 2
        // is no mention.
        var mention = (s.PushMention is 1 or 2) && batch.Lines.Any(l => l.Severity == "critical") ? (PushMention)s.PushMention : PushMention.None;
        var text = PushMessage.WithMention(PushMessage.Build(batch, _eventName, _str), mention);
        return _sender.SendAsync((Uri)destination, text, cancel, mention);
    }

    // The test message never mentions anybody, whatever the setting is.
    public Task<PushResult> TestAsync(object destination, CancellationToken cancel) =>
        _sender.SendAsync((Uri)destination, _str("L.Push.Test", "Test message from EVE MultiPreview: alerts will arrive here."),
            cancel, PushMention.None);

    public string GoneText(string code) =>
        "Discord push: Discord says the webhook does not exist (" + code + "); nothing more is sent until a new link is saved.";

    public void Dispose() => (_sender as IDisposable)?.Dispose();
}
