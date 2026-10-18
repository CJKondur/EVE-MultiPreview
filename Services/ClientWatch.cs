using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #22, #47: the mark a thumbnail shows for its client.</summary>
public enum ClientMark { None, NotResponding, Disconnected }

/// <summary>The words a mark shows on the thumbnail, in the language of the moment it changes (ruling 21).</summary>
public static class ClientMarks
{
    /// <summary>The mark's text, or null for no mark.</summary>
    public static string? Text(ClientMark mark) => mark switch
    {
        ClientMark.NotResponding => LocalizationService.Str("L.Thumb.NotResponding", "Not responding"),
        ClientMark.Disconnected => LocalizationService.Str("L.Thumb.Disconnected", "Disconnected"),
        _ => null,
    };
}

/// <summary>What the process monitor's pool thread has to ask Windows on a look.</summary>
[Flags]
public enum ClientProbeWants { None = 0, Hung = 1, Links = 2, Exits = 4 }

/// <summary>What the probe saw of one client: its window, the character (empty at the login screen), its process, whether
/// Windows says the window is hung (null: not asked or unknown) and the TCP table (null: not asked, or Windows gave none).</summary>
public readonly record struct ClientSample(IntPtr Window, string Character, int Pid, bool? Hung, IReadOnlyList<TcpLink>? Links);

/// <summary>The switches and the number the decisions read.</summary>
public readonly record struct ClientWatchOptions(bool HungBadge, bool HungEvent, bool LinkBadge, int DisconnectSeconds)
{
    public const int MinSeconds = 20, MaxSeconds = 600, DefaultSeconds = 60;

    /// <summary>The marks and the frozen alert as the settings have them; nothing at all without settings. The time is held
    /// to its range; a stored number outside it is not rewritten.</summary>
    public static ClientWatchOptions Of(Models.AppSettings? s, Func<string, bool> eventOn) => s == null
        ? new(false, false, false, DefaultSeconds)
        : new(s.ClientNotRespondingBadge, eventOn(ClientWatch.HungEvent), s.ClientDisconnectedBadge,
            Math.Clamp(s.DisconnectedSeconds, MinSeconds, MaxSeconds));

    /// <summary>What a look has to ask Windows for these switches; nothing is asked that no switch is on for.</summary>
    public ClientProbeWants Wants(bool closedOn) =>
        (HungBadge || HungEvent ? ClientProbeWants.Hung : ClientProbeWants.None)
        | (LinkBadge ? ClientProbeWants.Links : ClientProbeWants.None)
        | (closedOn ? ClientProbeWants.Exits : ClientProbeWants.None);
}

/// <summary>A thumbnail whose mark changed.</summary>
public sealed record ClientChange(IntPtr Window, ClientMark Mark);

/// <summary>An alert to raise.</summary>
public sealed record ClientAlert(string Character, string EventKey, string Severity, string Text);

/// <summary>The outcome of one look.</summary>
public sealed record ClientLook(IReadOnlyList<ClientChange> Changes, IReadOnlyList<ClientAlert> Alerts);

/// <summary>IDEAS #22, #47: decides, from what the probe saw, which thumbnail says Not responding or Disconnected and when
/// "Client not responding" is raised. Runs on the window's thread only and asks Windows nothing: the time and the samples
/// are handed in.</summary>
public sealed class ClientWatch
{
    public const string HungEvent = "client_hung", ClosedEvent = "client_closed", Severity = "critical";
    public const int HungAlertSeconds = 30;

    /// <summary>Whether the event is switched on; a missing key is off.</summary>
    public static bool IsOn(AppSettings s, string key) =>
        s.EnabledAlertTypes is { } t && t.TryGetValue(key, out var on) && on;

    /// <summary>Whether this watch raises the event.</summary>
    public static bool Owns(string key) => key == HungEvent || key == ClosedEvent;

    private sealed class State
    {
        public int Pid, Seen;
        public DateTime Last;
        public DateTime? HungSince, NoLinkSince;
        public bool HungTold, Armed, Proxied, SawServerPort;
        public ClientMark Shown;
    }

    private enum Link { Unknown, Connected, None }
    private static readonly TimeSpan HungAlertAfter = TimeSpan.FromSeconds(HungAlertSeconds);
    private readonly Dictionary<IntPtr, State> _states = new();
    private int _look;

    public ClientLook Look(DateTime utcNow, IReadOnlyList<ClientSample> clients, ClientWatchOptions o)
    {
        var changes = new List<ClientChange>();
        var alerts = new List<ClientAlert>();
        _look++;
        foreach (var c in clients)
        {
            if (!_states.TryGetValue(c.Window, out var st) || st.Pid != c.Pid)
                _states[c.Window] = st = new State { Pid = c.Pid, Last = utcNow, Shown = st?.Shown ?? ClientMark.None };
            else if (utcNow < st.Last)
            {
                // The clock was set back: what was being counted is counted from now.
                if (st.HungSince != null) st.HungSince = utcNow;
                if (st.NoLinkSince != null) st.NoLinkSince = utcNow;
            }
            st.Last = utcNow;
            st.Seen = _look;

            if (!(o.HungBadge || o.HungEvent) || c.Hung != true) { st.HungSince = null; st.HungTold = false; }
            else st.HungSince ??= utcNow;

            var link = c.Links == null || !o.LinkBadge ? Link.Unknown : Classify(st, c.Pid, c.Links);
            if (link == Link.Connected) { st.Armed = true; st.NoLinkSince = null; }
            else if (link == Link.None && st.Armed && c.Character.Length > 0) st.NoLinkSince ??= utcNow;
            else st.NoLinkSince = null;

            var mark = o.HungBadge && st.HungSince != null ? ClientMark.NotResponding
                : st.NoLinkSince is { } since && utcNow - since >= TimeSpan.FromSeconds(o.DisconnectSeconds) ? ClientMark.Disconnected
                : ClientMark.None;
            if (mark != st.Shown) { st.Shown = mark; changes.Add(new ClientChange(c.Window, mark)); }

            if (o.HungEvent && st.HungSince is { } hung && !st.HungTold && c.Character.Length > 0 && utcNow - hung >= HungAlertAfter)
            {
                st.HungTold = true;
                alerts.Add(new ClientAlert(c.Character, HungEvent, Severity,
                    string.Create(CultureInfo.InvariantCulture, $"{(int)(utcNow - hung).TotalSeconds} s")));
            }
        }
        foreach (var (window, gone) in _states.Where(kv => kv.Value.Seen != _look).ToList())
        {
            if (gone.Shown != ClientMark.None) changes.Add(new ClientChange(window, ClientMark.None));
            _states.Remove(window);
        }
        return new ClientLook(changes, alerts);
    }

    /// <summary>Whether Windows said this window's client is frozen at the last look.</summary>
    public bool IsHung(IntPtr window) => _states.TryGetValue(window, out var st) && st.HungSince != null;

    /// <summary>The process the window's client was last seen with, or null for a window not handed in at the last look.</summary>
    public int? PidOf(IntPtr window) => _states.TryGetValue(window, out var st) ? st.Pid : null;

    /// <summary>The conservative rule (ruling 8): a link of this process, established, to another PC; a loopback link
    /// means a local proxy and the client is never judged; once port 26000 was seen, only it counts.</summary>
    private static Link Classify(State st, int pid, IReadOnlyList<TcpLink> links)
    {
        bool remote = false, server = false;
        foreach (var l in links)
        {
            if (l.Pid != pid || !l.Established) continue;
            var to = l.Remote.IsIPv4MappedToIPv6 ? l.Remote.MapToIPv4() : l.Remote;
            if (IPAddress.IsLoopback(to)) { st.Proxied = true; continue; }
            if (to.Equals(IPAddress.Any) || to.Equals(IPAddress.IPv6Any)) continue;
            remote = true;
            if (l.RemotePort == TcpTable.ServerPort) server = true;
        }
        if (server) st.SawServerPort = true;
        if (st.Proxied) return Link.Unknown;
        return (st.SawServerPort ? server : remote) ? Link.Connected : Link.None;
    }
}
