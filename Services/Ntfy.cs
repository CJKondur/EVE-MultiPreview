using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>ntfy (https://ntfy.sh): the server a message is published to. An empty setting is the public server; a
/// self-hosted one is an http(s) address with no user, query or fragment, kept without its trailing slash.</summary>
public static class NtfyServer
{
    /// <summary>The server used while the setting is empty.</summary>
    public const string Default = "https://ntfy.sh";

    private const int MaxLength = 512;

    /// <summary>The address as it is kept: trimmed, without a trailing slash; "" for the default server. False for a
    /// text that is no http(s) address, or holds a user, a query or a fragment.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.Length > MaxLength) return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        normalized = (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');
        if (string.Equals(normalized, Default, StringComparison.OrdinalIgnoreCase)) normalized = "";
        return true;
    }

    /// <summary>Where a message is published (the server's root: ntfy's JSON publishing), or null for a kept text that
    /// is not a server address.</summary>
    public static Uri? PublishUri(string? kept) =>
        TryNormalize(kept, out var n) && Uri.TryCreate((n.Length == 0 ? Default : n) + "/", UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>The server as the card shows it.</summary>
    public static string Shown(string? kept) => TryNormalize(kept, out var n) && n.Length > 0 ? n : Default;
}

/// <summary>ntfy's topic and access token. Both are secrets: on the public server anybody who knows a topic can read
/// it and post to it, and the token opens the player's account on a server of their own. Neither is ever logged or
/// put in an exception, a status or a report.</summary>
public static class NtfyTopic
{
    // ntfy's own rule for a topic name.
    private static readonly Regex Valid = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    public static bool IsValid(string? topic) => topic != null && Valid.IsMatch(topic);

    /// <summary>A token goes into a header: printable ASCII only, no space, at most 256 characters.</summary>
    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 256 && token.All(c => c > ' ' && c < 0x7F);

    /// <summary>What the card shows of a topic: its last four characters (of a topic of 12 or more), never the whole.</summary>
    public static string Masked(string topic) =>
        topic.Length >= 12 ? "…" + topic.Substring(topic.Length - 4) : "…";

    /// <summary>A topic nobody guesses: "emp-" and 20 random letters and digits.</summary>
    public static string Random()
    {
        const string chars = "abcdefghijkmnopqrstuvwxyz23456789";
        var sb = new StringBuilder("emp-");
        for (int i = 0; i < 20; i++) sb.Append(chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }
}

/// <summary>Where one ntfy message goes. Its text is never the topic or the token: an exception or a log line that
/// prints it shows nothing secret.</summary>
public sealed class NtfyDestination
{
    public NtfyDestination(Uri publish, string topic, string? token)
    {
        Publish = publish;
        Topic = topic;
        Token = token;
    }

    public Uri Publish { get; }
    public string Topic { get; }
    public string? Token { get; }

    public override string ToString() => "ntfy destination";
}

/// <summary>One ntfy notification: a title, a message, the priority (1 to 5) and ntfy's tags (an emoji short code
/// shows as the emoji in front of the title).</summary>
public sealed record NtfyNote(string Title, string Message, int Priority, IReadOnlyList<string> Tags);

/// <summary>The notification of a batch, as the hub toast tells an alert: "Character · Event" over the place (the system,
/// or where the others went for "Left behind"), both masked in streamer mode (IDEAS #19). A batch of several lines is one
/// notification: the most severe line's title with "(+N more)", and every line in the message.</summary>
public static class NtfyMessage
{
    /// <summary>The most one line holds.</summary>
    public const int MaxLineChars = 200;

    /// <summary>critical 5 (max), warning 4 (high), info 3 (default).</summary>
    public static int Priority(string? severity) => severity switch
    {
        "critical" => 5,
        "warning" => 4,
        _ => 3,
    };

    /// <summary>The emoji short code for an event, else for its severity.</summary>
    public static string Tag(string? eventKey, string? severity) => eventKey switch
    {
        "attack" => "crossed_swords",
        "warp_scramble" => "lock",
        "decloak" => "eyes",
        "fleet_invite" => "envelope",
        "convo_request" => "speech_balloon",
        "system_change" => "rocket",
        "mine_cargo_full" => "package",
        "mine_asteroid_depleted" => "pick",
        "mine_crystal_broken" => "gem",
        "mine_module_stopped" => "stop_sign",
        "follow_warp" => "dash",
        "regroup" => "busts_in_silhouette",
        "straggler" => "footprints",
        "neutralized" => "battery",
        "jammed" => "satellite",
        "dps_high" => "boom",
        "reps_stopped" => "adhesive_bandage",
        "drones_returning" => "arrows_counterclockwise",
        "logi_idle" => "zzz",
        "client_memory" => "floppy_disk",
        "client_hung" => "hourglass",
        "client_closed" => "x",
        "char_idle" => "sleeping",
        "client_ready" => "white_check_mark",
        _ => severity switch
        {
            "critical" => "rotating_light",
            "warning" => "warning",
            _ => "information_source",
        },
    };

    /// <summary>Player text on one line: a line break or other control character becomes a space.</summary>
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    /// <summary>"Character · Event", and "×count" from 2 (streamer mode sends "Client N" in place of the name).</summary>
    public static string Title(PushLine line, Func<string, string> eventName)
    {
        var text = Plain(StreamerMask.Name(StreamerKind.Character, line.Character)) + " · " + Plain(eventName(line.EventKey));
        if (line.Count >= 2) text += " " + (char)0xD7 + line.Count;
        return Cut(text);
    }

    /// <summary>The place of a line, masked in streamer mode; null when not known.</summary>
    public static string? Place(PushLine line, Func<string, string, string?> placeOf)
    {
        var place = placeOf(line.Character, line.EventKey);
        if (string.IsNullOrEmpty(place)) return null;
        var shown = Plain(StreamerMask.Name(StreamerKind.System, place));
        return shown.Length > 0 ? shown : null;
    }

    /// <summary>The notification of a batch (its lines come most severe first, as the ledger hands them). One line: its
    /// title, and its place as the message (its severity's name when no place is known). Several: the first line's title
    /// with "(+N more)", and one row per line as "Character · Event · place".</summary>
    public static NtfyNote Build(PushBatch batch, Func<string, string> eventName, Func<string, string, string?> placeOf,
        Func<string, string, string> str)
    {
        var lines = batch.Lines;
        if (lines.Count == 0) return new NtfyNote("EVE MultiPreview", "", 3, Array.Empty<string>());
        var top = lines[0];
        int priority = lines.Max(l => Priority(l.Severity));
        var tags = new[] { Tag(top.EventKey, top.Severity) };
        if (lines.Count == 1)
        {
            var message = Place(top, placeOf) ?? str("L.S.Sev." + top.Severity, SeverityName(top.Severity));
            return new NtfyNote(Title(top, eventName), message, priority, tags);
        }
        var title = Title(top, eventName) + " " + string.Format(str("L.Ntfy.More", "(+{0} more)"), lines.Count - 1);
        var rows = lines.Select(l => Place(l, placeOf) is { } where ? Cut(Title(l, eventName) + " · " + where) : Title(l, eventName));
        return new NtfyNote(title, string.Join("\n", rows), priority, tags);
    }

    private static string SeverityName(string? severity) => severity switch
    {
        "critical" => "Critical",
        "warning" => "Warning",
        _ => "Info",
    };

    private static string Cut(string text)
    {
        if (text.Length <= MaxLineChars) return text;
        int cut = MaxLineChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut);
    }
}

/// <summary>Publishes one notification to ntfy and says what came of it.</summary>
public interface INtfySender
{
    Task<PushResult> SendAsync(NtfyDestination to, NtfyNote note, CancellationToken cancel);
}

/// <summary>The only class of the app that talks to ntfy. It sends one JSON <c>POST</c> to the server's root
/// (<c>topic</c>, <c>title</c>, <c>message</c>, <c>priority</c>, <c>tags</c>: JSON, so any character's name arrives as
/// it is), with <c>Authorization: Bearer</c> only when a token is saved. It reads no response body, follows no redirect
/// (a 3xx is a failure), and keeps nothing of an exception: its message can name the server. Failures are returned,
/// never thrown.</summary>
public sealed class NtfySender : INtfySender, IDisposable
{
    /// <summary>The longest a request may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;

    /// <summary>Takes the handler the requests go through (a test hands in a fake; the app hands in
    /// <see cref="DiscordSender.RealHandler"/>: no redirect, no cookies). The sender disposes it.</summary>
    public NtfySender(HttpMessageHandler handler) => _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };

    public async Task<PushResult> SendAsync(NtfyDestination to, NtfyNote note, CancellationToken cancel)
    {
        if (to == null || !NtfyTopic.IsValid(to.Topic) || (to.Token != null && !NtfyTopic.IsValidToken(to.Token)))
            return new PushResult(PushOutcome.Refused);
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                topic = to.Topic,
                title = note.Title,
                message = note.Message,
                priority = Math.Clamp(note.Priority, 1, 5),
                tags = note.Tags,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, to.Publish)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.TryParseAdd("EVE-MultiPreview/" + (typeof(NtfySender).Assembly.GetName().Version?.ToString(3) ?? "0"));
            if (to.Token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", to.Token);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            return Map(response);
        }
        catch (Exception)
        {
            // Any failure (network, timeout, or anything unforeseen) is out of reach; nothing of it is kept.
            return new PushResult(PushOutcome.Unreachable);
        }
    }

    private static PushResult Map(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status >= 200 && status < 300) return new PushResult(PushOutcome.Sent, status);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return new PushResult(PushOutcome.RateLimited, status, RetryAfter(response));
        // A wrong token, a topic reserved by somebody else, or no ntfy at that address: nothing more until the settings change.
        if (status == 401 || status == 403 || status == 404) return new PushResult(PushOutcome.Gone, status);
        if (status >= 500) return new PushResult(PushOutcome.Unreachable, status);
        return new PushResult(PushOutcome.Refused, status);
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : null);
        return wait is { } w && w > TimeSpan.Zero ? w : DefaultRetry;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>ntfy as a <see cref="PushWay"/>'s channel, with settings of its own (NtfyAlerts, NtfyAlertsFrom,
/// NtfyOnlyWhenAway, NtfyServer, NtfyTopic, NtfyToken), so it runs beside Discord with its own rules. The topic and the
/// token are read through the handed-in <c>secretOf</c> (NtfySecret.Unprotect on Windows, NtfyFile on Linux). The place
/// of an alert is asked when the alert is taken, on the thread that raised it, and kept until it is sent.</summary>
public sealed class NtfyChannel : IPushChannel
{
    private const int MostPlaces = 200;

    private readonly INtfySender _sender;
    private readonly Func<string?, string?> _topicOf;
    private readonly Func<string?, string?> _tokenOf;
    private readonly Func<string, string> _eventName;
    private readonly Func<string, string, string?> _placeOf;
    private readonly Func<string, string, string> _str;
    private readonly ConcurrentDictionary<string, string> _places = new();

    /// <param name="topicOf">The topic behind its kept text, or null when this PC can't read it.</param>
    /// <param name="tokenOf">The token behind its kept text, or null when this PC can't read it.</param>
    /// <param name="placeOf">(character, event): the place the hub toast shows (the system, or where the others went).</param>
    public NtfyChannel(INtfySender sender, Func<string?, string?> topicOf, Func<string?, string?> tokenOf, Func<string, string> eventName,
        Func<string, string, string?> placeOf, Func<string, string, string> str)
    {
        _sender = sender;
        _topicOf = topicOf;
        _tokenOf = tokenOf;
        _eventName = eventName;
        _placeOf = placeOf;
        _str = str;
    }

    public string Name => "ntfy";

    public bool IsOn(AppSettings s) => s.NtfyAlerts && !string.IsNullOrEmpty(s.NtfyTopic);

    public int From(AppSettings s) => s.NtfyAlertsFrom;

    public bool OnlyWhenAway(AppSettings s) => s.NtfyOnlyWhenAway;

    public bool Sends(AppSettings s, string eventKey) => PushEvents.Sends(s.NtfyAlertTypes, eventKey);

    public string Stored(AppSettings s) => (s.NtfyServer ?? "") + "\u0001" + (s.NtfyTopic ?? "") + "\u0001" + (s.NtfyToken ?? "");

    /// <summary>Null for a server that is no address, a topic this PC can't read, or a token saved but unreadable here
    /// (sending without it would be refused, or reach a topic the player did not mean).</summary>
    public object? Destination(AppSettings s)
    {
        try
        {
            var publish = NtfyServer.PublishUri(s.NtfyServer);
            if (publish == null) return null;
            var topic = _topicOf(s.NtfyTopic);
            if (!NtfyTopic.IsValid(topic)) return null;
            string? token = null;
            if (!string.IsNullOrEmpty(s.NtfyToken))
            {
                token = _tokenOf(s.NtfyToken);
                if (!NtfyTopic.IsValidToken(token)) return null;
            }
            return new NtfyDestination(publish, topic!, token);
        }
        catch (Exception) { return null; }
    }

    public void Taken(AlertNotice notice)
    {
        if (_places.Count >= MostPlaces) _places.Clear();
        var key = KeyOf(notice.Character, notice.EventKey);
        if (_placeOf(notice.Character, notice.EventKey) is { Length: > 0 } place) _places[key] = place;
        else _places.TryRemove(key, out _);
    }

    public Task<PushResult> SendAsync(object destination, PushBatch batch, AppSettings s, CancellationToken cancel)
    {
        var note = NtfyMessage.Build(batch, _eventName, (who, type) => _places.TryGetValue(KeyOf(who, type), out var p) ? p : null, _str);
        return _sender.SendAsync((NtfyDestination)destination, note, cancel);
    }

    public Task<PushResult> TestAsync(object destination, CancellationToken cancel) =>
        _sender.SendAsync((NtfyDestination)destination, new NtfyNote("EVE MultiPreview",
            _str("L.Push.Test", "Test message from EVE MultiPreview: alerts will arrive here."), 3, new[] { "white_check_mark" }), cancel);

    public string GoneText(string code) =>
        "ntfy push: the server refused the topic or the token (" + code + "); nothing more is sent until the ntfy settings change.";

    public void Dispose() => (_sender as IDisposable)?.Dispose();

    private static string KeyOf(string character, string eventKey) =>
        (character ?? "").ToUpperInvariant() + "\u0001" + (eventKey ?? "").ToUpperInvariant();
}
