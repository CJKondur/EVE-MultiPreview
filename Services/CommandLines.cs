using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>One command a button can run, with the command line that runs it.</summary>
public sealed record CommandChoice(RemoteCommand Command, string Line);

/// <summary>IDEAS #15: the command line a button (a Stream Deck key, a shortcut file) runs for a command: the program, a
/// switch of the list and, for a verb that takes one, the name in quotes. A name that Windows' command-line rules could not
/// carry back exactly (a quote, a trailing backslash) or that <see cref="RemoteCommand.Clean"/> would change gives no line,
/// so a line that exists always reads back as the command it was made from.</summary>
public static class CommandLines
{
    /// <summary>What follows the program: <c>--focus "Name"</c>, or only the switch for a verb without a name. Null when the
    /// verb is not on the list, or its name is not one a quoted argument can carry.</summary>
    public static string? Arguments(RemoteCommand command)
    {
        var switchText = RemoteCommand.SwitchOf(command.Verb);
        if (switchText.Length == 0) return null;
        if (!RemoteCommand.TakesName(command.Verb)) return switchText;
        var name = command.Argument;
        if (RemoteCommand.Clean(name) != name || name.Contains('"') || name.EndsWith('\\')) return null;
        return switchText + " \"" + name + "\"";
    }

    /// <summary>IDEAS P128: the thin sender's file name, as built and as the releases name it. It sits beside the full exe
    /// when the player put it there.</summary>
    public static readonly string[] SenderFileNames = { "EVE MultiPreview Send.exe", "EVE.MultiPreview.Send.exe" };

    /// <summary>The program a button runs: the thin sender beside <paramref name="exePath"/> when there is one (it answers in
    /// about a tenth of the time: the full exe is a whole WPF start), else <paramref name="exePath"/> itself. The same switches
    /// and exit codes either way. <paramref name="exists"/> is File.Exists unless a test hands one in.</summary>
    public static string? ButtonProgram(string? exePath, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrEmpty(exePath)) return exePath;
        var folder = System.IO.Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(folder)) return exePath;
        var exist = exists ?? System.IO.File.Exists;
        return SenderFileNames.Select(name => System.IO.Path.Combine(folder, name)).FirstOrDefault(exist) ?? exePath;
    }

    /// <summary>The whole line: the quoted program, then <see cref="Arguments"/>. Null when either has none.</summary>
    public static string? Line(string? exePath, RemoteCommand command)
    {
        if (string.IsNullOrEmpty(exePath) || exePath.Contains('"')) return null;
        return Arguments(command) is { } arguments ? "\"" + exePath + "\" " + arguments : null;
    }

    /// <summary>Every command a player can put on a button: the verbs without a name first, then a focus line per running
    /// character, a mute and an unmute line per running character, both cycle lines per group and a profile line per profile. Each in the order given, a name once (ignoring
    /// case), a name with no line left out. No program path: nothing.</summary>
    public static IReadOnlyList<CommandChoice> Choices(string? exePath, IEnumerable<string> running, IEnumerable<string> profiles, IEnumerable<string> groups)
    {
        var choices = new List<CommandChoice>();
        var seen = new HashSet<(RemoteVerb, string)>();

        void Add(RemoteCommand command)
        {
            if (Line(exePath, command) is not { } line) return;
            if (seen.Add((command.Verb, command.Argument.ToUpperInvariant()))) choices.Add(new CommandChoice(command, line));
        }

        foreach (var verb in Enum.GetValues<RemoteVerb>().Where(v => RemoteCommand.SwitchOf(v).Length > 0 && !RemoteCommand.TakesName(v)))
            Add(new RemoteCommand(verb, ""));
        var runningNames = running.ToList();
        foreach (var name in runningNames) Add(new RemoteCommand(RemoteVerb.Focus, name));
        foreach (var name in runningNames)   // IDEAS P129: the alert mute of a running character, both ways
        {
            Add(new RemoteCommand(RemoteVerb.Mute, name));
            Add(new RemoteCommand(RemoteVerb.Unmute, name));
        }
        foreach (var name in groups)
        {
            Add(new RemoteCommand(RemoteVerb.CycleGroup, name));
            Add(new RemoteCommand(RemoteVerb.CycleGroupBack, name));
        }
        foreach (var name in profiles) Add(new RemoteCommand(RemoteVerb.Profile, name));
        return choices;
    }
}
