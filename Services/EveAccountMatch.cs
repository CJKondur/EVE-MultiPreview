using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Which account a character is on, from when EVE saved its files. EVE names a character's file
/// (core_char_&lt;character&gt;.dat) and its account's file (core_user_&lt;account&gt;.dat) by number only, and neither file
/// names the other (measured 2026-11-06), but EVE saves both at the same moment when the character logs in or out.
/// A match counts only when that moment holds exactly one character file and exactly one account file: EVE also rewrites
/// the account file of every other logged-in account (issue #46), so anything less strict pairs the wrong files.</summary>
public static class EveAccountMatch
{
    /// <summary>How close the two saves must be. On the owner's settings, a character's file and its account's were
    /// written within this of each other.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static readonly Regex CharRx = new(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase);
    private static readonly Regex UserRx = new(@"^core_user_(\d+)\.dat$", RegexOptions.IgnoreCase);

    /// <summary>A profile's character and account files with when each was last written (UTC).</summary>
    public sealed record Saves(IReadOnlyDictionary<string, DateTime> Characters, IReadOnlyDictionary<string, DateTime> Accounts);

    /// <summary>Reads one settings_* folder; files it cannot read are left out.</summary>
    public static Saves Read(string profilePath)
    {
        var chars = new Dictionary<string, DateTime>();
        var users = new Dictionary<string, DateTime>();
        if (!Directory.Exists(profilePath)) return new Saves(chars, users);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(profilePath, "core_*.dat").ToList(); }
        catch { return new Saves(chars, users); }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            DateTime time;
            try { time = File.GetLastWriteTimeUtc(file); }
            catch { continue; }
            if (CharRx.Match(name) is { Success: true } c) chars[c.Groups[1].Value] = time;
            else if (UserRx.Match(name) is { Success: true } u) users[u.Groups[1].Value] = time;
        }
        return new Saves(chars, users);
    }

    /// <summary>The account saved together with this character, or null when the character has no file or the moment
    /// isn't clear (no account file, or more than one character or account file saved then).</summary>
    public static string? Of(Saves saves, string charId)
    {
        if (!saves.Characters.TryGetValue(charId, out var at)) return null;
        bool Near(DateTime t) => (t - at).Duration() <= Window;
        if (saves.Characters.Count(kv => Near(kv.Value)) != 1) return null;
        var users = saves.Accounts.Where(kv => Near(kv.Value)).Select(kv => kv.Key).Take(2).ToList();
        return users.Count == 1 ? users[0] : null;
    }

    /// <summary><see cref="Of(Saves, string)"/> on one folder.</summary>
    public static string? Of(string profilePath, string charId) => Of(Read(profilePath), charId);
}

/// <summary>Links characters to their accounts while MultiPreview runs, so One character can bring the account's overview
/// and windows without Detect accounts…. Every <see cref="Every"/> it looks for character files saved since its last look;
/// a pair <see cref="EveAccountMatch"/> finds is linked only when the same pair is seen again at a later save (a login
/// and the logout after it, say), so a one-off coincidence never links. Characters already linked are left alone: Detect
/// accounts… is the player's own answer. Runs on the caller's thread; only reads EVE's files.</summary>
public sealed class EveAccountLearner
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly Func<string> _eveDir;
    private readonly Dictionary<string, (string Account, DateTime Saved)> _seen = new();
    private DateTime _lastLook = DateTime.MinValue;
    private DateTime _since = DateTime.MinValue;

    /// <param name="eveDir">The folder holding the settings_* profiles; "" when there is none.</param>
    public EveAccountLearner(Func<string> eveDir) => _eveDir = eveDir;

    /// <summary>Looks when <see cref="Every"/> has passed. True when it linked a character (the caller saves).
    /// The first look only notes the time: files saved before MultiPreview started say nothing it can check twice.</summary>
    public bool Tick(AppSettings s, DateTime nowUtc)
    {
        if (nowUtc - _lastLook < Every) return false;
        _lastLook = nowUtc;
        if (_since == DateTime.MinValue) { _since = nowUtc; return false; }
        var since = _since - EveAccountMatch.Window;   // a save just before the last look may not have had its pair yet
        _since = nowUtc;
        string dir;
        try { dir = _eveDir(); } catch { return false; }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        List<string> profiles;
        try { profiles = Directory.EnumerateDirectories(dir, "settings*").ToList(); }
        catch { return false; }
        var linked = s.AccountCharacterMap.Values.SelectMany(v => v).ToHashSet();
        bool changed = false;
        foreach (var profile in profiles)
        {
            var saves = EveAccountMatch.Read(profile);
            foreach (var (charId, saved) in saves.Characters)
            {
                if (saved < since || linked.Contains(charId)) continue;
                if (EveAccountMatch.Of(saves, charId) is not { } account) continue;
                if (_seen.TryGetValue(charId, out var before) && before.Account == account
                    && (saved - before.Saved).Duration() > EveAccountMatch.Window)
                {
                    if (!s.AccountCharacterMap.TryGetValue(account, out var list)) s.AccountCharacterMap[account] = list = new List<string>();
                    list.Add(charId);
                    linked.Add(charId);
                    _seen.Remove(charId);
                    changed = true;
                }
                else if (!_seen.ContainsKey(charId) || _seen[charId].Account != account)
                    _seen[charId] = (account, saved);   // the same save seen again by an overlapping look changes nothing
            }
        }
        return changed;
    }
}
