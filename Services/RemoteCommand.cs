using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #39, #15 and P127: what another program may ask of the running MultiPreview. One of seventeen, nothing
/// else: the four of #39 keep their values, the nine of #15 follow, then the four explicit on/off verbs of P127.</summary>
public enum RemoteVerb
{
    Focus, Profile, ToggleThumbnails, FocusMode,
    CycleGroup, CycleGroupBack, CycleAll, CycleAllBack, PreviousClient, LastAlert, NextProfile, PreviousProfile, DoNotDisturb,
    DoNotDisturbOn, DoNotDisturbOff, FocusModeOn, FocusModeOff,
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
    };

    /// <summary>The command-line switch of a verb of the list (<c>--cycle-all</c>); empty for any other.</summary>
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

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The command a command line holds: exactly one switch of the list (any case), with its name when it takes
    /// one. Null for anything else.</summary>
    public static RemoteCommand? FromArgs(IReadOnlyList<string> args)
    {
        if (args.Count is < 1 or > 2) return null;
        foreach (var (switchText, _, verb, takesName) in Allowed)
        {
            if (!string.Equals(args[0], switchText, StringComparison.OrdinalIgnoreCase)) continue;
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
            default:
                return RemoteOutcome.Refused;
        }
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
}
