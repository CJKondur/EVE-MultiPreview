using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>What AutoProfile.Look asks App to do: switch to Profile, or ask the player first.</summary>
public sealed record AutoProfileChoice(string Profile, bool Ask);

/// <summary>IDEAS #11: which profile the characters logged in point to, and when to switch to it. Each profile may name
/// its characters (a comma text: EVE names have no comma). The pick is the profile with the most of its characters
/// logged in; a tie, or the current profile among the best, changes nothing; nobody logged in changes nothing. It is
/// decided once, 10 s after the set of logged-in characters last changed, so a crew that logs in one by one is one
/// switch. For 10 minutes after a switch the player made himself, the switch is a question. App asks it on its
/// half-second timer, on the window's thread; it switches nothing itself.</summary>
public sealed class AutoProfile
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RecentManual = TimeSpan.FromMinutes(10);
    public const char Separator = ',';

    private HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _changedAt;
    private bool _decided = true;
    private DateTime? _manualAt;

    /// <summary>The names a profile's text holds: trimmed, blanks dropped, each once (ignoring case, first spelling kept).</summary>
    public static IReadOnlyList<string> Characters(string? stored) =>
        (stored ?? "").Split(Separator).Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Names as a profile keeps them: trimmed, blanks and names with a comma dropped, each once.</summary>
    public static string Join(IEnumerable<string> names) => string.Join(Separator,
        names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0 && n.IndexOf(Separator) < 0).Distinct(StringComparer.OrdinalIgnoreCase));

    public static string With(string? stored, string name) => Join(Characters(stored).Append(name));

    public static string Without(string? stored, string name) =>
        Join(Characters(stored).Where(n => !string.Equals(n, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>A name a profile can keep: not blank, no comma, no control character.</summary>
    public static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOf(Separator) < 0 && !name.Any(char.IsControl);

    /// <summary>The profile to switch to, or null: none has a character logged in, two share the most, the current one is
    /// among the most, or it is the current one.</summary>
    public static string? Best(IReadOnlyList<(string Profile, IReadOnlyList<string> Characters)> profiles,
        IEnumerable<string> loggedIn, string current)
    {
        var on = new HashSet<string>(loggedIn, StringComparer.OrdinalIgnoreCase);
        string? best = null;
        int top = 0;
        bool tie = false, currentAmongTop = false;
        foreach (var (profile, characters) in profiles)
        {
            int score = characters.Count(on.Contains);
            if (score == 0) continue;
            if (score > top) { top = score; best = profile; tie = false; currentAmongTop = profile == current; }
            else if (score == top) { tie = true; if (profile == current) currentAmongTop = true; }
        }
        if (best == null || currentAmongTop || tie) return null;
        return best;
    }

    /// <summary>The player switched profiles himself (the tray, a hotkey, the settings window, a command).</summary>
    public void ManualSwitch(DateTime utc) => _manualAt = utc;

    /// <summary>Called twice a second while the switch is on: a choice once the logged-in characters have been the same for
    /// Settle and point to another profile, else null. Asked once per change of who is logged in.</summary>
    public AutoProfileChoice? Look(DateTime utcNow, IEnumerable<string> loggedIn,
        IReadOnlyList<(string Profile, IReadOnlyList<string> Characters)> profiles, string current)
    {
        var now = new HashSet<string>(loggedIn.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        if (!now.SetEquals(_seen)) { _seen = now; _changedAt = utcNow; _decided = false; }
        if (_decided || _changedAt is not DateTime since) return null;
        if (utcNow < since) { _changedAt = utcNow; return null; }   // the clock was set back: the wait is counted again
        if (utcNow - since < Settle) return null;
        _decided = true;
        if (now.Count == 0) return null;
        var best = Best(profiles, now, current);
        if (best == null) return null;
        bool ask = _manualAt is DateTime manual && utcNow >= manual && utcNow - manual < RecentManual;
        return new AutoProfileChoice(best, ask);
    }

    /// <summary>The switch went off: who was seen is forgotten (the next look starts afresh); the player's last own switch is kept.</summary>
    public void Forget()
    {
        _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _changedAt = null;
        _decided = true;
    }
}
