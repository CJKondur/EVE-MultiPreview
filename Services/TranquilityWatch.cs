using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>What the server status said (IDEAS #34). <see cref="Allowed"/> is false when the status was not asked at all
/// (<see cref="StatusResult.NotAllowed"/>); an answer that could not be read, or a server that is down, is
/// <see cref="Down"/>, which is allowed but not up.</summary>
public readonly record struct StatusResult(bool Allowed, int Players, bool Vip)
{
    /// <summary>The status was not asked: the live check waits for the owner (IDEAS P120).</summary>
    public static readonly StatusResult NotAllowed = new(false, 0, false);

    /// <summary>Asked, and the server is not up (or the answer was not readable).</summary>
    public static readonly StatusResult Down = new(true, 0, false);

    /// <summary>Open to everyone: asked, players on, and not in VIP mode.</summary>
    public bool IsUp => Allowed && Players > 0 && !Vip;
}

/// <summary>IDEAS #34: one tray note when Tranquility accepts logins again after the daily downtime (11:00 UTC,
/// <see cref="Downtime"/>), with the number of players. It asks the server status once a minute inside the window that
/// starts with the downtime and lasts <see cref="Cap"/>, and stops at the first answer that is up, or when the window
/// ends, or when the status is not allowed. The first poll comes one interval after the window opens: a status that is
/// cached can still say "up" at 11:00:00. Nothing is asked outside the window. The clock and the fetch are handed in; a
/// clock set back inside a window asks again within a minute and never tells the same downtime twice.
/// The live check is NOT wired (IDEAS P120): the fetch App passes is <see cref="NotAllowedFetch"/>. This type holds no
/// network code.</summary>
public sealed class TranquilityWatch
{
    /// <summary>The time between two polls.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(60);

    /// <summary>How long after the downtime began the watch gives up.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(60);

    /// <summary>Whether the owner has approved the live status check (IDEAS P120: it needs a direct ESI request from the
    /// desktop app, the open question of P33). False until then: App passes <see cref="NotAllowedFetch"/>, and the
    /// settings card says the check waits. Turning this on is a code change together with a real fetch.</summary>
    public static readonly bool LiveCheckApproved = false;

    /// <summary>The fetch App passes while <see cref="LiveCheckApproved"/> is false: it answers "not allowed" at once and
    /// reaches nothing.</summary>
    public static readonly Func<CancellationToken, Task<StatusResult>> NotAllowedFetch = _ => Task.FromResult(StatusResult.NotAllowed);

    private readonly Func<CancellationToken, Task<StatusResult>> _fetch;
    private DateTime _window;                // the downtime the polls below belong to
    private DateTime _next;                  // when the next poll is due
    private DateTime _finished = DateTime.MinValue;   // the last downtime told, or given up on (not allowed)
    private bool _fetching;

    public TranquilityWatch(Func<CancellationToken, Task<StatusResult>> fetch) => _fetch = fetch;

    /// <summary>The server status JSON ({"players":12345,"server_version":"…","start_time":"…","vip":false}). Anything
    /// else, a missing or non-integer player count, or a vip of the wrong type, is <see cref="StatusResult.Down"/>.</summary>
    public static StatusResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return StatusResult.Down;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return StatusResult.Down;
            if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Number
                || !players.TryGetInt32(out int count) || count < 0) return StatusResult.Down;
            bool vip = false;
            if (root.TryGetProperty("vip", out var v))
            {
                if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False) return StatusResult.Down;
                vip = v.GetBoolean();
            }
            return new StatusResult(true, count, vip);
        }
        catch (JsonException) { return StatusResult.Down; }
    }

    /// <summary>Asked on every tick of App's half-second timer. Returns the number of players when the server is up for the
    /// first time in this downtime (once), else null. A fetch that throws counts as down; only a cancelled token is raised.</summary>
    public async Task<int?> PollAsync(DateTime utcNow, CancellationToken ct)
    {
        var now = Downtime.Normalize(utcNow);
        var window = Downtime.LastUtc(now);
        if (now - window >= Cap || window <= _finished || _fetching) return null;
        if (window != _window)
        {
            _window = window;
            _next = window + PollEvery;
        }
        else if (_next - now > PollEvery) _next = now + PollEvery;   // the clock was set back
        if (now < _next) return null;

        _fetching = true;
        StatusResult status;
        try { status = await _fetch(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { status = StatusResult.Down; }
        finally { _fetching = false; }

        _next = now + PollEvery;
        if (!status.Allowed) { _finished = window; return null; }
        if (!status.IsUp) return null;
        _finished = window;
        return status.Players;
    }

    /// <summary>The tray note: the catalog's text with the players, written with the thousands separator of the player's culture.</summary>
    public static string BalloonText(int players) =>
        string.Format(LocalizationService.Str("L.Tray.TranquilityBackUp", "Tranquility is back up. Players online: {0}"),
            players.ToString("N0", CultureInfo.CurrentCulture));
}
