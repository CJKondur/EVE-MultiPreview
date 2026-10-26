using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #39, #15, P127, P129, P126 and Plans 38 and 39: what another program may ask of the running MultiPreview. One
/// of thirty-eight, nothing else: the four of #39 keep their values, the nine of #15 follow, then the four explicit on/off verbs
/// of P127, the two alert-mute verbs of P129, the four query verbs of P126 (pipe-only: no switch, and the reply carries data),
/// the eleven of Plan 38 (three that open a window, the name bar's three, mark-alerts-seen, a 30-minute mute, minimize and
/// restore, and live-state, which is pipe-only) and the four pipe-only words of Plan 39 (hello and pair, which the server hands
/// to its pairing seam, and the two lists of commands and alert types).</summary>
public enum RemoteVerb
{
    Focus, Profile, ToggleThumbnails, FocusMode,
    CycleGroup, CycleGroupBack, CycleAll, CycleAllBack, PreviousClient, LastAlert, NextProfile, PreviousProfile, DoNotDisturb,
    DoNotDisturbOn, DoNotDisturbOff, FocusModeOn, FocusModeOff,
    Mute, Unmute,
    ListCharacters, ListGroups, ListProfiles, State,
    OpenSettings, OpenFleetBoard, OpenAlertHistory, NameBar, NameBarOn, NameBarOff, MarkAlertsSeen, Mute30, MinimizeClients, RestoreClients,
    LiveState,
    Hello, Pair, ListCommands, ListAlertTypes,
}

/// <summary>What came of a command. The running MultiPreview answers the first three as one byte on the pipe; a second
/// start of MultiPreview ends with the number as its exit code, and with NotReachable when no MultiPreview took it.</summary>
public enum RemoteOutcome : byte { Done = 0, Refused = 1, NotFound = 2, NotReachable = 3 }

/// <summary>IDEAS #39 and #15: one command from a command line or the pipe, on one strict allow-list. Its name is
/// only ever compared with names that exist (a running character, a profile): it is never a path, a format, shell input
/// or a key to look anything else up by.</summary>
public sealed record RemoteCommand(RemoteVerb Verb, string Argument)
{
    public const int MaxArgumentLength = 64;
    /// <summary>The longest message the pipe takes: its length is one byte.</summary>
    public const int MaxMessageBytes = 255;

    private static readonly (string Switch, string Word, RemoteVerb Verb, bool TakesName)[] Allowed =
    {
        ("--focus", "focus", RemoteVerb.Focus, true),
        ("--profile", "profile", RemoteVerb.Profile, true),
        ("--toggle-thumbnails", "toggle-thumbnails", RemoteVerb.ToggleThumbnails, false),
        ("--focus-mode", "focus-mode", RemoteVerb.FocusMode, false),
        ("--cycle", "cycle", RemoteVerb.CycleGroup, true),
        ("--cycle-back", "cycle-back", RemoteVerb.CycleGroupBack, true),
        ("--cycle-all", "cycle-all", RemoteVerb.CycleAll, false),
        ("--cycle-all-back", "cycle-all-back", RemoteVerb.CycleAllBack, false),
        ("--previous-client", "previous-client", RemoteVerb.PreviousClient, false),
        ("--last-alert", "last-alert", RemoteVerb.LastAlert, false),
        ("--next-profile", "next-profile", RemoteVerb.NextProfile, false),
        ("--previous-profile", "previous-profile", RemoteVerb.PreviousProfile, false),
        ("--do-not-disturb", "do-not-disturb", RemoteVerb.DoNotDisturb, false),
        ("--do-not-disturb-on", "do-not-disturb-on", RemoteVerb.DoNotDisturbOn, false),
        ("--do-not-disturb-off", "do-not-disturb-off", RemoteVerb.DoNotDisturbOff, false),
        ("--focus-mode-on", "focus-mode-on", RemoteVerb.FocusModeOn, false),
        ("--focus-mode-off", "focus-mode-off", RemoteVerb.FocusModeOff, false),
        ("--mute", "mute", RemoteVerb.Mute, true),
        ("--unmute", "unmute", RemoteVerb.Unmute, true),
        // IDEAS P126: the queries are pipe-only. An empty switch is no switch: FromArgs skips it, CommandLines lists nothing for it.
        ("", "list-characters", RemoteVerb.ListCharacters, false),
        ("", "list-groups", RemoteVerb.ListGroups, false),
        ("", "list-profiles", RemoteVerb.ListProfiles, false),
        ("", "state", RemoteVerb.State, false),
        // Plan 38: buttons for a Stream Deck. The three that open a window take the front (TakesTheFront); live-state is pipe-only
        // like the queries (the command runner refuses it).
        ("--open-settings", "open-settings", RemoteVerb.OpenSettings, false),
        ("--open-fleet-board", "open-fleet-board", RemoteVerb.OpenFleetBoard, false),
        ("--open-alert-history", "open-alert-history", RemoteVerb.OpenAlertHistory, false),
        ("--name-bar", "name-bar", RemoteVerb.NameBar, false),
        ("--name-bar-on", "name-bar-on", RemoteVerb.NameBarOn, false),
        ("--name-bar-off", "name-bar-off", RemoteVerb.NameBarOff, false),
        ("--mark-alerts-seen", "mark-alerts-seen", RemoteVerb.MarkAlertsSeen, false),
        ("--mute-30", "mute-30", RemoteVerb.Mute30, true),
        ("--minimize-clients", "minimize-clients", RemoteVerb.MinimizeClients, false),
        ("--restore-clients", "restore-clients", RemoteVerb.RestoreClients, false),
        ("", "live-state", RemoteVerb.LiveState, false),
        // Plan 39 Ruling 20: pipe-only. hello and pair go to the server's pairing seam, never to a runner.
        ("", "hello", RemoteVerb.Hello, false),
        ("", "pair", RemoteVerb.Pair, false),
        ("", "list-commands", RemoteVerb.ListCommands, false),
        ("", "list-alert-types", RemoteVerb.ListAlertTypes, false),
    };

    /// <summary>IDEAS P126: is the verb a query, whose reply carries data after the outcome byte? Only these eight (Plan 38 added
    /// live-state, Plan 39 hello and the two lists; pair is not one); every other verb, and any value not on the list, is
    /// answered with one byte.</summary>
    public static bool IsQuery(RemoteVerb verb) =>
        verb is RemoteVerb.ListCharacters or RemoteVerb.ListGroups or RemoteVerb.ListProfiles or RemoteVerb.State or RemoteVerb.LiveState
            or RemoteVerb.Hello or RemoteVerb.ListCommands or RemoteVerb.ListAlertTypes;

    /// <summary>The command-line switch of a verb of the list (<c>--cycle-all</c>); empty for any other, and for a query.</summary>
    public static string SwitchOf(RemoteVerb verb) => Allowed.FirstOrDefault(a => a.Verb == verb).Switch ?? "";

    /// <summary>The word of a verb on the pipe (<c>cycle-all</c>), also the end of its label key <c>L.Cmd.&lt;word&gt;</c>;
    /// empty for a verb not on the list.</summary>
    public static string WordOf(RemoteVerb verb) => Allowed.FirstOrDefault(a => a.Verb == verb).Word ?? "";

    /// <summary>Does the verb carry a name (a character, a profile, a group)? False for the others and for a verb not on the list.</summary>
    public static bool TakesName(RemoteVerb verb) => Allowed.FirstOrDefault(a => a.Verb == verb).TakesName;

    /// <summary>Does the command bring a client's window forward (a switch the player would otherwise make with a hotkey)?</summary>
    public bool SwitchesAClient =>
        Verb is RemoteVerb.Focus or RemoteVerb.CycleGroup or RemoteVerb.CycleGroupBack or RemoteVerb.CycleAll
            or RemoteVerb.CycleAllBack or RemoteVerb.PreviousClient or RemoteVerb.LastAlert;

    /// <summary>Plan 38: must the sender let the running MultiPreview take the front? A client switch does; so does a command
    /// that shows one of MultiPreview's own windows (settings, the fleet board, the alert history), which Windows would otherwise
    /// leave behind the program the player is using.</summary>
    public bool TakesTheFront =>
        SwitchesAClient || Verb is RemoteVerb.OpenSettings or RemoteVerb.OpenFleetBoard or RemoteVerb.OpenAlertHistory;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The command a command line holds: exactly one switch of the list (any case), with its name when it takes
    /// one. Null for anything else.</summary>
    public static RemoteCommand? FromArgs(IReadOnlyList<string> args)
    {
        if (args.Count is < 1 or > 2) return null;
        foreach (var (switchText, _, verb, takesName) in Allowed)
        {
            if (switchText.Length == 0 || !string.Equals(args[0], switchText, StringComparison.OrdinalIgnoreCase)) continue;   // a query has no switch
            if (!takesName) return args.Count == 1 ? new RemoteCommand(verb, "") : null;
            return args.Count == 2 && Clean(args[1]) is { } name ? new RemoteCommand(verb, name) : null;
        }
        return null;
    }

    /// <summary>A name as a command may carry it: trimmed, 1 to 64 characters, no control, format or line-separator
    /// character, no lone surrogate, and not another switch. Null otherwise.</summary>
    public static string? Clean(string? text)
    {
        var name = (text ?? "").Trim();
        if (name.Length is 0 or > MaxArgumentLength || name.StartsWith("--", StringComparison.Ordinal)) return null;
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) return null;   // a lone surrogate reads as the replacement character
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
        }
        return name;
    }

    /// <summary>The message on the pipe: the command's word, a line break, its name (UTF-8, at most 210 bytes). A verb
    /// not on the list gives an empty message, which nobody takes.</summary>
    public byte[] ToMessage() =>
        Allowed.FirstOrDefault(a => a.Verb == Verb).Word is { } word ? Strict.GetBytes(word + "\n" + Argument) : Array.Empty<byte>();

    /// <summary>A message read from the pipe, checked as strictly as a command line: valid UTF-8, a word of the list
    /// (exact), and a name exactly as Clean would keep it. Null for anything else.</summary>
    public static RemoteCommand? FromMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length is 0 or > MaxMessageBytes) return null;
        string text;
        try { text = Strict.GetString(message); }
        catch (DecoderFallbackException) { return null; }
        int cut = text.IndexOf('\n');
        if (cut < 0) return null;
        var word = text[..cut];
        var name = text[(cut + 1)..];
        foreach (var (_, allowedWord, verb, takesName) in Allowed)
        {
            if (!string.Equals(word, allowedWord, StringComparison.Ordinal)) continue;
            if (!takesName) return name.Length == 0 ? new RemoteCommand(verb, "") : null;
            return Clean(name) is { } clean && clean == name ? new RemoteCommand(verb, clean) : null;
        }
        return null;
    }
}

/// <summary>IDEAS #39 and #15: does one command on the window's thread with App's parts handed in, and says what came of
/// it. A character is found among the running ones ignoring case; a profile or a group by its exact name, else the one
/// whose name matches ignoring case.</summary>
public sealed class RemoteCommandRunner
{
    public required Func<IEnumerable<string>> Running { get; init; }
    public required Func<IEnumerable<string>> Profiles { get; init; }
    public required Action<string> Focus { get; init; }
    public required Action<string> SwitchProfile { get; init; }
    public required Action ToggleThumbnails { get; init; }
    public required Action ToggleFocusMode { get; init; }
    public required Func<IEnumerable<string>> Groups { get; init; }
    /// <summary>The group's real name, and whether to go forward.</summary>
    public required Action<string, bool> CycleGroup { get; init; }
    public required Action<bool> CycleAll { get; init; }
    public required Action PreviousClient { get; init; }
    public required Action LastAlert { get; init; }
    public required Action<bool> CycleProfile { get; init; }
    public required Action DoNotDisturb { get; init; }
    /// <summary>IDEAS P127: whether do-not-disturb is on right now (by the hotkey, the tray or the quiet hours); the four
    /// explicit verbs press the toggle only when this differs from what they ask for.</summary>
    public required Func<bool> IsDoNotDisturbOn { get; init; }
    /// <summary>IDEAS P127: whether focus mode is on right now.</summary>
    public required Func<bool> IsFocusModeOn { get; init; }
    /// <summary>IDEAS P129: the running character's real name, and true to mute its alerts (for the longest timed length the
    /// thumbnail menu offers) or false to end the mute. Same state as the menu's Mute alerts › item.</summary>
    public required Action<string, bool> SetAlertMute { get; init; }

    // Plan 38: the parts of the ten new verbs. Not required: a runner built without one answers Refused for the verbs that need
    // it, and nothing else about it changes (an older caller of the runner keeps compiling).
    public Action? OpenSettings { get; init; }
    public Action? OpenFleetBoard { get; init; }
    public Action? OpenAlertHistory { get; init; }
    /// <summary>Whether the name bar is showing right now; the three name-bar verbs read it before they act.</summary>
    public Func<bool>? IsNameBarOn { get; init; }
    /// <summary>Shows (true) or hides (false) the name bar.</summary>
    public Action<bool>? SetNameBar { get; init; }
    /// <summary>Marks every alert seen; answers how many it marked.</summary>
    public Func<int>? MarkAlertsSeen { get; init; }
    /// <summary>The running character's real name, and the minutes to mute its alerts for.</summary>
    public Action<string, int>? MuteFor { get; init; }
    public Action? MinimizeClients { get; init; }
    public Action? RestoreClients { get; init; }

    /// <summary>The minutes of <see cref="RemoteVerb.Mute30"/>.</summary>
    public const int ShortMuteMinutes = 30;

    /// <summary>The name a player typed, as one that exists: the exact one, else the one whose name matches ignoring case
    /// (two such: none). Null when there is none.</summary>
    private static string? Resolve(IEnumerable<string> names, string wanted)
    {
        var all = names.ToList();
        var exact = all.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.Ordinal));
        if (exact != null) return exact;
        var loose = all.Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }

    private static RemoteOutcome Call(Action? part)
    {
        if (part == null) return RemoteOutcome.Refused;
        part();
        return RemoteOutcome.Done;
    }

    public RemoteOutcome Run(RemoteCommand command)
    {
        switch (command.Verb)
        {
            case RemoteVerb.Focus:
                var who = Running().FirstOrDefault(n => string.Equals(n, command.Argument, StringComparison.OrdinalIgnoreCase));
                if (who == null) return RemoteOutcome.NotFound;
                Focus(who);
                return RemoteOutcome.Done;
            case RemoteVerb.Profile:
                var profile = Resolve(Profiles(), command.Argument);
                if (profile == null) return RemoteOutcome.NotFound;
                SwitchProfile(profile);
                return RemoteOutcome.Done;
            case RemoteVerb.ToggleThumbnails:
                ToggleThumbnails();
                return RemoteOutcome.Done;
            case RemoteVerb.FocusMode:
                ToggleFocusMode();
                return RemoteOutcome.Done;
            case RemoteVerb.CycleGroup:
            case RemoteVerb.CycleGroupBack:
                var group = Resolve(Groups(), command.Argument);
                if (group == null) return RemoteOutcome.NotFound;
                CycleGroup(group, command.Verb == RemoteVerb.CycleGroup);
                return RemoteOutcome.Done;
            case RemoteVerb.CycleAll:
                CycleAll(true);
                return RemoteOutcome.Done;
            case RemoteVerb.CycleAllBack:
                CycleAll(false);
                return RemoteOutcome.Done;
            case RemoteVerb.PreviousClient:
                PreviousClient();
                return RemoteOutcome.Done;
            case RemoteVerb.LastAlert:
                LastAlert();
                return RemoteOutcome.Done;
            case RemoteVerb.NextProfile:
                CycleProfile(true);
                return RemoteOutcome.Done;
            case RemoteVerb.PreviousProfile:
                CycleProfile(false);
                return RemoteOutcome.Done;
            case RemoteVerb.DoNotDisturb:
                DoNotDisturb();
                return RemoteOutcome.Done;
            // IDEAS P127: idempotent. The toggle is pressed only when the state differs; either way the answer is Done. A
            // focus-mode "on" with no group to show still presses it: the press says why it stays off (P124).
            case RemoteVerb.DoNotDisturbOn:
                if (!IsDoNotDisturbOn()) DoNotDisturb();
                return RemoteOutcome.Done;
            case RemoteVerb.DoNotDisturbOff:
                if (IsDoNotDisturbOn()) DoNotDisturb();
                return RemoteOutcome.Done;
            case RemoteVerb.FocusModeOn:
                if (!IsFocusModeOn()) ToggleFocusMode();
                return RemoteOutcome.Done;
            case RemoteVerb.FocusModeOff:
                if (IsFocusModeOn()) ToggleFocusMode();
                return RemoteOutcome.Done;
            // IDEAS P129: the whole-character alert mute of the thumbnail menu. A character that is not running is NotFound,
            // never a silent Done; the name handed on is the running character's own, so its case is the thumbnail's.
            case RemoteVerb.Mute:
            case RemoteVerb.Unmute:
                var pilot = Running().FirstOrDefault(n => string.Equals(n, command.Argument, StringComparison.OrdinalIgnoreCase));
                if (pilot == null) return RemoteOutcome.NotFound;
                SetAlertMute(pilot, command.Verb == RemoteVerb.Mute);
                return RemoteOutcome.Done;
            // Plan 38: a verb whose part was not handed in is Refused, whatever its name says (the check comes first).
            case RemoteVerb.OpenSettings:
                return Call(OpenSettings);
            case RemoteVerb.OpenFleetBoard:
                return Call(OpenFleetBoard);
            case RemoteVerb.OpenAlertHistory:
                return Call(OpenAlertHistory);
            case RemoteVerb.MarkAlertsSeen:
                if (MarkAlertsSeen is not { } markSeen) return RemoteOutcome.Refused;
                markSeen();
                return RemoteOutcome.Done;
            case RemoteVerb.MinimizeClients:
                return Call(MinimizeClients);
            case RemoteVerb.RestoreClients:
                return Call(RestoreClients);
            // the toggle flips the bar; the explicit forms (as P127's) act only when the state differs, and answer Done either way
            case RemoteVerb.NameBar:
            case RemoteVerb.NameBarOn:
            case RemoteVerb.NameBarOff:
                if (IsNameBarOn is not { } isBarOn || SetNameBar is not { } setBar) return RemoteOutcome.Refused;
                bool bar = isBarOn();
                bool want = command.Verb switch { RemoteVerb.NameBarOn => true, RemoteVerb.NameBarOff => false, _ => !bar };
                if (want != bar) setBar(want);
                return RemoteOutcome.Done;
            case RemoteVerb.Mute30:
                if (MuteFor is not { } muteFor) return RemoteOutcome.Refused;
                var shortPilot = Running().FirstOrDefault(n => string.Equals(n, command.Argument, StringComparison.OrdinalIgnoreCase));
                if (shortPilot == null) return RemoteOutcome.NotFound;
                muteFor(shortPilot, ShortMuteMinutes);
                return RemoteOutcome.Done;
            // a verb not on the list, the pipe-only queries (P126), which RemoteQueryRunner answers, and Plan 39's hello and pair,
            // which the server hands to its pairing seam: nothing here runs them
            default:
                return RemoteOutcome.Refused;
        }
    }
}

/// <summary>IDEAS P126: what a query came to: the outcome, and for Done the lines of its answer (one name or one fact each).</summary>
public sealed record QueryReply(RemoteOutcome Outcome, IReadOnlyList<string> Lines)
{
    public static readonly QueryReply Refused = new(RemoteOutcome.Refused, Array.Empty<string>());
    public static readonly QueryReply NotReachable = new(RemoteOutcome.NotReachable, Array.Empty<string>());
}

/// <summary>IDEAS P126: answers a query verb on the window's thread with App's parts handed in. It lists names that exist and
/// reports three switches; it changes nothing. Anything that is not a query is Refused and no part is read. The names are
/// given as they are: <see cref="QueryPayload.Frame"/> leaves out what a command could not carry.</summary>
public sealed class RemoteQueryRunner
{
    public required Func<IEnumerable<string>> Running { get; init; }
    public required Func<IEnumerable<string>> Profiles { get; init; }
    public required Func<IEnumerable<string>> Groups { get; init; }
    public required Func<bool> IsDoNotDisturbOn { get; init; }
    public required Func<bool> IsFocusModeOn { get; init; }
    public required Func<bool> AreThumbnailsHidden { get; init; }
    /// <summary>Plan 38: the lines of the live-state reply (<see cref="Services.LiveState"/>), read on the window's thread. Not
    /// required: a runner built without it answers Refused to live-state.</summary>
    public Func<IReadOnlyList<string>>? LiveState { get; init; }

    public QueryReply Query(RemoteCommand command)
    {
        static QueryReply Done(IEnumerable<string> lines) => new(RemoteOutcome.Done, lines.ToList());
        static string OnOff(bool on) => on ? "on" : "off";
        return command.Verb switch
        {
            RemoteVerb.ListCharacters => Done(Running()),
            RemoteVerb.ListGroups => Done(Groups()),
            RemoteVerb.ListProfiles => Done(Profiles()),
            // the words of the state are protocol, not UI text: English literals, the same in every language
            RemoteVerb.State => Done(new[]
            {
                "do-not-disturb=" + OnOff(IsDoNotDisturbOn()),
                "focus-mode=" + OnOff(IsFocusModeOn()),
                "thumbnails=" + (AreThumbnailsHidden() ? "hidden" : "shown"),
            }),
            RemoteVerb.LiveState => LiveState is { } live ? Done(live()) : QueryReply.Refused,
            _ => QueryReply.Refused,
        };
    }
}

/// <summary>IDEAS P126: the data after the outcome byte of a query's reply: a two-byte big-endian length (at most
/// <see cref="MaxBytes"/>), then that many bytes of UTF-8 text, a line break after every line. Both ends are bounded by the cap.</summary>
public static class QueryPayload
{
    public const int MaxBytes = 4096;
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The length and the text of the lines that go out. A line that <see cref="RemoteCommand.Clean"/> would change or
    /// refuse (missing, empty, over 64 characters, a control or line-break character, a lone surrogate, starting with --) is left out; the text
    /// is cut at the last line that fits whole, the lines before it kept in order. Plan 38: <paramref name="accept"/> replaces
    /// that rule for a reply with a format of its own (live-state): a line it refuses is left out the same way.</summary>
    public static byte[] Frame(IEnumerable<string> lines, Func<string, bool>? accept = null)
    {
        var text = new List<byte>();
        foreach (var line in lines)
        {
            if (line == null || !(accept != null ? accept(line) : RemoteCommand.Clean(line) == line)) continue;
            var bytes = Strict.GetBytes(line + "\n");
            if (text.Count + bytes.Length > MaxBytes) break;
            text.AddRange(bytes);
        }
        var frame = new byte[2 + text.Count];
        frame[0] = (byte)(text.Count >> 8);
        frame[1] = (byte)(text.Count & 0xFF);
        text.CopyTo(frame, 2);
        return frame;
    }

    /// <summary>The lines of a text that came off the pipe; null when it is not valid UTF-8.</summary>
    public static IReadOnlyList<string>? Parse(ReadOnlySpan<byte> text)
    {
        try { return Strict.GetString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries); }
        catch (DecoderFallbackException) { return null; }
    }
}

/// <summary>IDEAS #39: what a second start of MultiPreview does with its command line.</summary>
public static class RemoteCommandLine
{
    /// <summary>No arguments: null (the "already running" note, as before). A command of the list: handed to send, and its
    /// answer is the exit code. Anything else: Refused, and nobody is asked.</summary>
    public static RemoteOutcome? Forward(IReadOnlyList<string> args, Func<RemoteCommand, RemoteOutcome> send)
    {
        if (args.Count == 0) return null;
        return RemoteCommand.FromArgs(args) is { } command ? send(command) : RemoteOutcome.Refused;
    }

    /// <summary>Plan 39: should a second start tell the player that commands are off? Yes when nothing took the command. Also when
    /// a valid command was Refused and the running MultiPreview, asked <c>hello</c>, does not answer Done with
    /// <c>commands=on</c>: since Plan 39 the pipe exists with commands off and its gate refuses the command, and an older or not
    /// yet paired MultiPreview refuses hello too. A command line that is not a command is no such case, and hello is not asked.</summary>
    public static bool SaysCommandsOff(IReadOnlyList<string> args, RemoteOutcome outcome, Func<QueryReply> hello)
    {
        if (outcome == RemoteOutcome.NotReachable) return true;
        if (outcome != RemoteOutcome.Refused || RemoteCommand.FromArgs(args) == null) return false;
        var reply = hello();
        return !(reply.Outcome == RemoteOutcome.Done && reply.Lines.Contains("commands=on"));
    }
}

/// <summary>IDEAS P130 (b): what a first start does with the command it was given, on each look.</summary>
public enum StartupCommandStep { Run, Wait, Drop }

/// <summary>IDEAS P130 (b): a first start's <c>--focus</c>, <c>--mute</c> or <c>--unmute</c> names a character whose thumbnail
/// the first discovery pass may not have made yet. It is asked again on the existing 500 ms tick for <see cref="Limit"/>, and
/// then dropped without a word. Every other command runs on the first look.</summary>
public static class StartupCommandWait
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Run now, look again on the next tick, or give up. A command that names a character runs as soon as that character
    /// is among <paramref name="running"/> (names matched ignoring case, as the runner finds them); one whose character is still
    /// missing after <see cref="Limit"/> is dropped (nobody is told: a first start has no sender).</summary>
    public static StartupCommandStep Decide(RemoteCommand command, IEnumerable<string> running, TimeSpan waited)
    {
        if (command.Verb is not (RemoteVerb.Focus or RemoteVerb.Mute or RemoteVerb.Unmute)) return StartupCommandStep.Run;
        if (running.Any(n => string.Equals(n, command.Argument, StringComparison.OrdinalIgnoreCase))) return StartupCommandStep.Run;
        return waited < Limit ? StartupCommandStep.Wait : StartupCommandStep.Drop;
    }

    /// <summary>One look at the command a first start holds: the command to run now, or null (still waiting, dropped, or none held).
    /// What is handed out, and what is dropped, is cleared from <paramref name="held"/> here, so a later look cannot hand it out
    /// again: the start-up step and every tick after it share the one field and the command runs once.</summary>
    public static RemoteCommand? Take(ref RemoteCommand? held, IEnumerable<string> running, TimeSpan waited)
    {
        if (held is not { } command) return null;
        var step = Decide(command, running, waited);
        if (step != StartupCommandStep.Wait) held = null;
        return step == StartupCommandStep.Run ? command : null;
    }
}
