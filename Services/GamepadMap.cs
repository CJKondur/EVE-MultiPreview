using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #43: what each pad button does, read from the text the player wrote: one <c>Button = command</c> per line
/// (<c>A = focus:Fixture Alpha</c>, <c>RightShoulder = cycle-all</c>). Only the ten commands below are taken, each built
/// through <see cref="RemoteCommand.FromMessage"/>, the same allow-list and the same name rules the pipe and the command line
/// use, so a name is only ever compared with the names that exist. Anything else is an error line: it is left out, never run,
/// and never repeated back (an error names a line number and, at most, a button of the list, not what the player typed).</summary>
public static class GamepadMap
{
    /// <summary>The longest text taken: fourteen buttons need far less.</summary>
    public const int MaxTextLength = 4000;

    private static readonly RemoteVerb[] Verbs =
    {
        RemoteVerb.CycleAll, RemoteVerb.CycleAllBack, RemoteVerb.PreviousClient, RemoteVerb.LastAlert,
        RemoteVerb.NextProfile, RemoteVerb.PreviousProfile, RemoteVerb.Focus, RemoteVerb.Profile,
        RemoteVerb.CycleGroup, RemoteVerb.CycleGroupBack,
    };

    private static readonly HashSet<string> Words = Verbs.Select(RemoteCommand.WordOf).ToHashSet(StringComparer.Ordinal);

    private static readonly Dictionary<string, GamepadButton> Buttons =
        Enum.GetValues<GamepadButton>().ToDictionary(b => b.ToString(), b => b, StringComparer.OrdinalIgnoreCase);

    /// <summary>The command words a button may be given (<c>cycle-all</c>, <c>focus</c>, ...), for the help and the tests.</summary>
    public static IReadOnlyCollection<string> AllowedWords => Words;

    /// <summary>The bindings the text holds, and one error line for each line that was left out. A button given twice keeps its
    /// last line and reports the first. Empty text binds nothing and has no error.</summary>
    public static (IReadOnlyDictionary<GamepadButton, RemoteCommand> Bindings, IReadOnlyList<string> Errors) Parse(string? text)
    {
        var bindings = new Dictionary<GamepadButton, RemoteCommand>();
        var errors = new List<string>();
        text ??= "";
        if (text.Length > MaxTextLength)
        {
            errors.Add("the text is too long");
            return (bindings, errors);
        }

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            int number = i + 1;
            int equals = line.IndexOf('=');
            if (equals < 0) { errors.Add($"line {number}: write Button = command"); continue; }
            if (!Buttons.TryGetValue(line[..equals].Trim(), out var button)) { errors.Add($"line {number}: not a button of the list"); continue; }

            var command = line[(equals + 1)..].Trim();
            int colon = command.IndexOf(':');
            var word = (colon < 0 ? command : command[..colon]).Trim().ToLowerInvariant();
            var name = colon < 0 ? "" : command[(colon + 1)..].Trim();
            if (!Words.Contains(word)) { errors.Add($"line {number}: not a command of the list"); continue; }
            // Markup characters in a name are refused outright; a character, group or profile name never needs them.
            if (name.IndexOfAny(new[] { '<', '>' }) >= 0) { errors.Add($"line {number}: the name holds markup"); continue; }
            var built = RemoteCommand.FromMessage(Encoding.UTF8.GetBytes(word + "\n" + name));
            if (built == null || Array.IndexOf(Verbs, built.Verb) < 0) { errors.Add($"line {number}: the command or its name is not valid"); continue; }

            if (bindings.ContainsKey(button)) errors.Add($"line {number}: {button} is bound twice, the last line is kept");
            bindings[button] = built;
        }
        return (bindings, errors);
    }
}
