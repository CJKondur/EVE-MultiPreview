using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #39: what another program may ask of the running MultiPreview. One of four, nothing else.</summary>
public enum RemoteVerb { Focus, Profile, ToggleThumbnails, FocusMode }

/// <summary>What came of a command. The running MultiPreview answers the first three as one byte on the pipe; a second
/// start of MultiPreview ends with the number as its exit code, and with NotReachable when no MultiPreview took it.</summary>
public enum RemoteOutcome : byte { Done = 0, Refused = 1, NotFound = 2, NotReachable = 3 }

/// <summary>IDEAS #39 (and #15 later): one command from a command line or the pipe, on a strict allow-list. Its name is
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
    };

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

/// <summary>IDEAS #39: does one command on the window's thread with App's parts handed in, and says what came of it. A
/// character is found among the running ones ignoring case; a profile by its exact name, else the one profile whose name
/// matches ignoring case.</summary>
public sealed class RemoteCommandRunner
{
    public required Func<IEnumerable<string>> Running { get; init; }
    public required Func<IEnumerable<string>> Profiles { get; init; }
    public required Action<string> Focus { get; init; }
    public required Action<string> SwitchProfile { get; init; }
    public required Action ToggleThumbnails { get; init; }
    public required Action ToggleFocusMode { get; init; }

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
                var names = Profiles().ToList();
                var profile = names.FirstOrDefault(n => string.Equals(n, command.Argument, StringComparison.Ordinal));
                if (profile == null)
                {
                    var loose = names.Where(n => string.Equals(n, command.Argument, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (loose.Count == 1) profile = loose[0];
                }
                if (profile == null) return RemoteOutcome.NotFound;
                SwitchProfile(profile);
                return RemoteOutcome.Done;
            case RemoteVerb.ToggleThumbnails:
                ToggleThumbnails();
                return RemoteOutcome.Done;
            case RemoteVerb.FocusMode:
                ToggleFocusMode();
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
