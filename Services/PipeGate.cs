using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>Plan 39 Ruling 4: who is at the other end of the command pipe, read from the client's own token by impersonation
/// (never from anything it sent). <see cref="ThisAccount"/>: its user is this Windows account and it runs in this Windows
/// session; <see cref="Elevated"/>: its token is elevated. <see cref="ProcessId"/> only names the program in a prompt and is
/// never a reason to trust it. <see cref="Unknown"/> is a client that could not be read: it is admitted to nothing.</summary>
public sealed record PipeClient(bool ThisAccount, bool Elevated, uint ProcessId)
{
    public static readonly PipeClient Unknown = new(false, false, 0);
}

/// <summary>Plan 39 Ruling 1: what the running pipe lets through right now. <see cref="Commands"/> is "Take commands from other
/// programs", <see cref="Unelevated"/> its second switch, <see cref="ServerElevated"/> whether this MultiPreview runs elevated.
/// <see cref="Closed"/> (the server's start value) admits only hello and pair.</summary>
public sealed record PipeAccess(bool Commands, bool Unelevated, bool ServerElevated)
{
    public static readonly PipeAccess Closed = new(false, false, false);
}

/// <summary>Plan 39 Ruling 2: the one rule of who may say what on the pipe. The pipe's list admits this account (for an
/// elevated MultiPreview its ordinary programs too, Ruling 3); this gate decides what each connection may ask.</summary>
public static class PipeGate
{
    /// <summary>A client of another account, or one that could not be read, gets nothing. This account always gets hello and
    /// pair. Anything else needs "Take commands from other programs", and an unelevated client of an elevated MultiPreview
    /// needs the second switch as well. Queries (live-state, the lists) are no exception: nothing about characters leaves
    /// before the owner allowed it.</summary>
    public static bool Admits(RemoteVerb verb, PipeClient client, PipeAccess access)
    {
        if (!client.ThisAccount) return false;
        if (IsPairing(verb)) return true;
        if (!access.Commands) return false;
        if (access.ServerElevated && !client.Elevated && !access.Unelevated) return false;
        return true;
    }

    /// <summary>The two words a program may say before it is allowed: hello and pair.</summary>
    public static bool IsPairing(RemoteVerb verb) => verb is RemoteVerb.Hello or RemoteVerb.Pair;

    /// <summary>Ruling 1: the pipe exists while programs may ask to connect or commands are on.</summary>
    public static bool Listens(bool allowPairing, bool acceptCommands) => allowPairing || acceptCommands;
}

/// <summary>Plan 39 Ruling 9: the reply to hello. Exactly three lines, protocol words, no names: <c>v=1</c>,
/// <c>commands=on|off</c> (whether this connection is admitted for normal verbs) and <c>pairing=ready|asking|wait|blocked</c>.</summary>
public static class PairingHello
{
    private static readonly string[] States = { "ready", "asking", "wait", "blocked" };

    /// <summary>The lines of the reply. A pairing state not in the list is no line (left out, never sent).</summary>
    public static IReadOnlyList<string> Lines(bool commands, string pairing) =>
        new[] { "v=1", "commands=" + (commands ? "on" : "off"), "pairing=" + pairing }.Where(IsLine).ToArray();

    /// <summary>True only for one of the exact lines of <see cref="Lines"/>; the server's framing leaves out any other.</summary>
    public static bool IsLine(string? line) =>
        line is "v=1" or "commands=on" or "commands=off"
        || (line != null && line.StartsWith("pairing=", StringComparison.Ordinal) && States.Contains(line["pairing=".Length..], StringComparer.Ordinal));
}
