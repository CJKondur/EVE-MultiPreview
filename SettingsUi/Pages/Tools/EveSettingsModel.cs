using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>One EVE settings profile (a settings_* folder) and how many real account files it holds.</summary>
public sealed record EveProfile(string Name, string Path, int Accounts);

/// <summary>A character or an account in a profile: its number and what the list shows.</summary>
public sealed record EveEntry(string Id, string Label);

/// <summary>Files copied, targets that took the copy, targets that failed.</summary>
public sealed record EveCopyResult(int Files, int Targets, int Failed);

/// <summary>What Tools › EVE settings copies (spec §5 mode switch). Not saved, as before.</summary>
public enum EveCopyMode { Profile, Character, Account }

/// <summary>Tools › EVE settings without its UI. File work is the old window's EveManagerService calls, unchanged;
/// every question and message goes through ISettingsHost. Settings it writes: EveSettingsDir, EveBackupDir,
/// AccountLabels, AccountCharacterMap, and EveManager (the saved name cache) for names fetched from ESI (IDEAS P33).</summary>
public sealed class EveSettingsModel
{
    private readonly SettingsContext _ctx;
    private readonly Func<bool> _eveRunning;
    private readonly Func<AppSettings, Dictionary<string, string>> _loadNames;
    private readonly Func<Dictionary<string, string>, IEnumerable<string>, Task> _fetchNames;
    private readonly Func<string, string> _findEveDir;
    private Dictionary<string, string>? _names;

    /// <param name="eveRunning">Whether an EVE client runs (default: EveManagerService.IsEveRunning).</param>
    /// <param name="loadNames">Character number → name (default: LoadNames, which reads EVE's chat logs).</param>
    /// <param name="fetchNames">Adds names for character numbers to the map (default: EveManagerService.EnrichWithESI, the
    /// public ESI lookup). Tests give their own: they never reach the network.</param>
    /// <param name="findEveDir">The folder holding the settings_* profiles for a chosen folder ("" = find it), default
    /// EveManagerService.FindEveDir (%LOCALAPPDATA%\CCP\EVE). The Linux app gives its own, which looks in the Proton prefix.</param>
    public EveSettingsModel(SettingsContext ctx, Func<bool>? eveRunning = null, Func<AppSettings, Dictionary<string, string>>? loadNames = null,
        Func<Dictionary<string, string>, IEnumerable<string>, Task>? fetchNames = null, Func<string, string>? findEveDir = null)
    {
        _ctx = ctx;
        _eveRunning = eveRunning ?? EveManagerService.IsEveRunning;
        _loadNames = loadNames ?? LoadNames;
        _fetchNames = fetchNames ?? EveManagerService.EnrichWithESI;
        _findEveDir = findEveDir ?? (path => EveManagerService.FindEveDir(path));
    }

    private AppSettings S => _ctx.S;
    private static string Str(string key) => SettingsContext.Str(key);
    private void Toast(string key) => _ctx.Host.Toast(Str(key));

    // ── Folders and profiles ──

    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE", "EVEMPBackups");

    public string BackupRoot => string.IsNullOrEmpty(S.EveBackupDir) ? DefaultBackupRoot : S.EveBackupDir;

    /// <summary>The folder holding the settings_* profiles: the chosen one (or its parent), else the auto-detected one.</summary>
    public string EveDir => _findEveDir(S.EveSettingsDir);

    public bool EveRunning => _eveRunning();

    public IReadOnlyList<EveProfile> Profiles { get; private set; } = Array.Empty<EveProfile>();

    public void Refresh()
    {
        var dir = EveDir;
        Profiles = dir.Length == 0
            ? Array.Empty<EveProfile>()
            : EveManagerService.ListProfiles(dir).Select(p => new EveProfile(p.Name, p.Path, p.CharCount))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The status line: nothing found, how many profiles, and a warning while EVE runs.</summary>
    public string StatusText()
    {
        if (Profiles.Count == 0) return NoProfilesText();
        var found = string.Format(Str("L.S.Eve.Found"), Profiles.Count);
        return EveRunning ? Str("L.S.Eve.Running") + " " + found : found;
    }

    /// <summary>Nothing to list: no EVE folder found, or no settings_ profile in the one chosen.</summary>
    private string NoProfilesText() =>
        EveDir is { Length: > 0 } dir ? string.Format(Str("L.S.Eve.NoProfiles"), dir) : Str("L.S.Eve.NotFound");

    public void SetSettingsFolder(string path)
    {
        S.EveSettingsDir = path.Trim();
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }

    /// <summary>Old OnAutoDetectEveDir: the newest install under %LOCALAPPDATA%\CCP\EVE, Tranquility first. False when none.
    /// It scans the real folder: never call it in a test.</summary>
    public bool FindSettingsFolder()
    {
        var dir = _findEveDir("");
        if (dir.Length == 0) return false;
        SetSettingsFolder(dir);
        return true;
    }

    public void SetBackupFolder(string path)
    {
        S.EveBackupDir = path.Trim();
        _ctx.Applier.Request(ApplyScope.Save);
    }

    /// <summary>Old OnResetBackupDir: the standard folder is written out.</summary>
    public void ResetBackupFolder() => SetBackupFolder(DefaultBackupRoot);

    // ── Names and lists ──

    /// <summary>Character number → name, loaded the first time a list needs a name, plus names fetched this session.
    /// Fetched names are kept in the settings too, so the next session doesn't fetch them again (IDEAS P33).</summary>
    public Dictionary<string, string> Names => _names ??= _loadNames(S);

    /// <summary>Old LoadEveManagerPanel: the saved cache (EveManager.CharNameCache), then the first lines of EVE's chat
    /// logs (the Alerts chat log folder, then Documents\EVE\logs\Chatlogs). Writes nothing to the settings.</summary>
    public static Dictionary<string, string> LoadNames(AppSettings s) =>
        EveManagerService.LoadCharNameCache(s.ChatLogDirectory, CachedNames(s));

    /// <summary>The saved name cache (EveManager.CharNameCache) in the shape LoadCharNameCache seeds from; null when there
    /// is none.</summary>
    public static Dictionary<string, Dictionary<string, string>>? CachedNames(AppSettings s)
    {
        if (s.EveManager?.CharNameCache == null) return null;
        var cache = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (id, entry) in s.EveManager.CharNameCache)
        {
            var d = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(entry.Name)) d["name"] = entry.Name;
            if (!string.IsNullOrEmpty(entry.Fetched)) d["fetched"] = entry.Fetched;
            if (!string.IsNullOrEmpty(entry.Method)) d["method"] = entry.Method;
            cache[id] = d;
        }
        return cache;
    }

    public IReadOnlyList<EveEntry> Characters(EveProfile profile) =>
        EveManagerService.ListCharacters(profile.Path, Names).Select(c => new EveEntry(c.Id, c.Label))
            .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<EveEntry> Accounts(EveProfile profile) =>
        EveManagerService.ListAccounts(profile.Path).Select(a => new EveEntry(a.Id, AccountLabel(a.Id)))
            .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Old AccountLabel: the player's name for the account, else up to three known character names, else the number.</summary>
    public string AccountLabel(string id)
    {
        if (S.AccountLabels.TryGetValue(id, out var own) && !string.IsNullOrWhiteSpace(own))
            return string.Format(Str("L.S.Eve.AccountNamed"), own, id);
        if (S.AccountCharacterMap.TryGetValue(id, out var ids) && ids.Count > 0)
        {
            var names = ids.Select(c => Names.TryGetValue(c, out var n) && !string.IsNullOrEmpty(n) ? n : null).OfType<string>().ToList();
            if (names.Count > 0)
            {
                var shown = string.Join(", ", names.Take(3));
                if (names.Count > 3) shown = string.Format(Str("L.S.Eve.AccountMore"), shown, names.Count - 3);
                return string.Format(Str("L.S.Eve.AccountNamed"), shown, id);
            }
        }
        return string.Format(Str("L.S.Eve.Account"), id);
    }

    /// <summary>A character as the lists show it: "Name (number)", or the number.</summary>
    public string CharacterLabel(string id) => Names.TryGetValue(id, out var n) && n.Length > 0 ? $"{n} ({id})" : id;

    /// <summary>The old search boxes: the label or the number contains the text, ignoring case.</summary>
    public static IReadOnlyList<EveEntry> Filter(IReadOnlyList<EveEntry> entries, string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? entries
            : entries.Where(e => e.Label.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Id.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ── Copies ──

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Old OnBackupCheckedTargets: each profile copied whole into the backup folder. Returns how many worked.</summary>
    public int BackUp(IEnumerable<EveProfile> targets) =>
        targets.Count(t => EveManagerService.BackupProfile(t.Path, BackupRoot).Length > 0);

    /// <summary>Old OnCopyToCheckedTargets: every core_*.dat of the source onto each target, each backed up first.
    /// The source is never a target.</summary>
    public EveCopyResult CopyProfile(EveProfile source, IEnumerable<EveProfile> targets)
    {
        var list = targets.Where(t => !SamePath(t.Path, source.Path)).ToList();
        int files = 0, failed = 0;
        foreach (var t in list)
        {
            // Never replace a profile that wasn't backed up first.
            if (EveManagerService.BackupProfile(t.Path, BackupRoot).Length == 0) { failed++; continue; }
            var n = EveManagerService.CopyProfile(source.Path, t.Path);
            if (n >= 0) files += n;
            else failed++;
        }
        return new EveCopyResult(files, list.Count - failed, failed);
    }

    /// <summary>Old OnCharCopyExecute: one character's file onto each target character, each replaced file backed up.</summary>
    public EveCopyResult CopyCharacter(EveProfile source, string sourceId, EveProfile target, IEnumerable<string> targetIds) =>
        CopyEach(sourceId, targetIds, id => EveManagerService.CopyCharacterSettings(source.Path, sourceId, target.Path, id, BackupRoot));

    /// <summary>Old OnAcctCopyExecute: one account's file onto each target account, each replaced file backed up.</summary>
    public EveCopyResult CopyAccount(EveProfile source, string sourceId, EveProfile target, IEnumerable<string> targetIds) =>
        CopyEach(sourceId, targetIds, id => EveManagerService.CopyAccountSettings(source.Path, sourceId, target.Path, id, BackupRoot));

    /// <summary>Ids go into file names (core_char_{id}.dat), so only digits are accepted: anything else is counted failed
    /// and never reaches the file system. A bad source id copies nothing.</summary>
    private static bool IsNumericId(string id) => id.Length > 0 && id.All(c => c is >= '0' and <= '9');

    private static EveCopyResult CopyEach(string sourceId, IEnumerable<string> ids, Func<string, int> copy)
    {
        var list = ids.ToList();
        if (!IsNumericId(sourceId)) return new EveCopyResult(0, 0, list.Count);
        int files = 0, ok = 0, failed = 0;
        foreach (var id in list)
        {
            if (!IsNumericId(id)) { failed++; continue; }
            var n = copy(id);
            if (n >= 0) { files += n; ok++; }
            else failed++;
        }
        return new EveCopyResult(files, ok, failed);
    }

    public const int MaxListedTargets = 10;

    /// <summary>One label per line, the first <see cref="MaxListedTargets"/>, then "and {n} more".</summary>
    public static string TargetList(IReadOnlyList<string> labels)
    {
        var lines = labels.Take(MaxListedTargets).ToList();
        if (labels.Count > MaxListedTargets) lines.Add(string.Format(Str("L.S.Eve.AndMore"), labels.Count - MaxListedTargets));
        return string.Join('\n', lines);
    }

    public static string Report(EveCopyResult r) => r.Failed > 0
        ? string.Format(Str("L.S.Eve.CopiedWithFailures"), r.Files, r.Targets, r.Failed)
        : string.Format(Str("L.S.Eve.Copied"), r.Files, r.Targets);

    /// <summary>Copying while a client runs can be undone when EVE saves on logout: all three copies ask first.</summary>
    private bool ConfirmIfEveRunning() => !EveRunning || _ctx.Host.Confirm(Str("L.S.Eve.RunningTitle"), Str("L.S.Eve.RunningBody"));

    public void BackUpInteractive(IReadOnlyList<EveProfile> targets)
    {
        if (targets.Count == 0) { Toast("L.S.Eve.NeedBackupTargets"); return; }
        var n = BackUp(targets);
        _ctx.Host.Toast(string.Format(Str("L.S.Eve.BackedUp"), n, BackupRoot));
    }

    public void CopyProfileInteractive(EveProfile? source, IReadOnlyList<EveProfile> targets)
    {
        if (source == null) { Toast("L.S.Eve.NeedSource"); return; }
        if (targets.Count == 0) { Toast("L.S.Eve.NeedTargets"); return; }
        var list = targets.Where(t => !SamePath(t.Path, source.Path)).ToList();
        if (list.Count == 0) { Toast("L.S.Eve.SameProfile"); return; }
        if (!ConfirmIfEveRunning()) return;
        var body = string.Format(Str("L.S.Eve.CopyProfileConfirm"), source.Name, string.Join(", ", list.Select(t => t.Name)));
        if (!_ctx.Host.Confirm(Str("L.S.Eve.CopyTitle"), body)) return;
        _ctx.Host.Toast(Report(CopyProfile(source, list)));
        Refresh();
    }

    public void CopyCharacterInteractive(EveProfile? source, string? sourceId, EveProfile? target, IReadOnlyList<string> targetIds, bool toAll) =>
        CopyEntriesInteractive(false, source, sourceId, target, targetIds, toAll);

    public void CopyAccountInteractive(EveProfile? source, string? sourceId, EveProfile? target, IReadOnlyList<string> targetIds, bool toAll) =>
        CopyEntriesInteractive(true, source, sourceId, target, targetIds, toAll);

    private void CopyEntriesInteractive(bool accounts, EveProfile? source, string? sourceId, EveProfile? target,
        IReadOnlyList<string> targetIds, bool toAll)
    {
        if (source == null || target == null) { Toast("L.S.Eve.NeedProfiles"); return; }
        if (sourceId == null) { Toast(accounts ? "L.S.Eve.NeedSourceAccount" : "L.S.Eve.NeedSourceCharacter"); return; }
        var all = toAll ? (accounts ? Accounts(target) : Characters(target)).Select(e => e.Id) : targetIds;
        // Copying an entry onto itself does nothing; leave it out so the count is honest.
        var ids = all.Where(id => !(SamePath(source.Path, target.Path) && id == sourceId)).Distinct().ToList();
        if (ids.Count == 0)
        {
            Toast(toAll ? "L.S.Eve.TargetEmpty" : accounts ? "L.S.Eve.NeedTargetAccount" : "L.S.Eve.NeedTargetCharacter");
            return;
        }
        if (!ConfirmIfEveRunning()) return;
        // Ticks survive a search redraw, so targets the search hides are copied onto too: the question names them all.
        var body = string.Format(Str(accounts ? "L.S.Eve.CopyAccountsConfirm" : "L.S.Eve.CopyCharactersConfirm"),
                accounts ? AccountLabel(sourceId) : CharacterLabel(sourceId), ids.Count, target.Name)
            + "\n\n" + TargetList(ids.Select(id => accounts ? AccountLabel(id) : CharacterLabel(id)).ToList());
        if (!_ctx.Host.Confirm(Str("L.S.Eve.CopyTitle"), body)) return;
        var result = accounts ? CopyAccount(source, sourceId, target, ids) : CopyCharacter(source, sourceId, target, ids);
        _ctx.Host.Toast(Report(result));
    }

    // ── Accounts ──

    public void SetAccountLabel(string accountId, string? text)
    {
        var label = text?.Trim() ?? "";
        if (label.Length == 0) S.AccountLabels.Remove(accountId);
        else S.AccountLabels[accountId] = label;
        _ctx.Applier.Request(ApplyScope.Save);
    }

    /// <summary>Old OnAcctSetLabel: names the chosen "Copy from" account; empty removes its name.</summary>
    public bool SetLabelInteractive(string? accountId, string? text)
    {
        if (accountId == null) { Toast("L.S.Eve.NeedAccount"); return false; }
        SetAccountLabel(accountId, text);
        var label = text?.Trim() ?? "";
        _ctx.Host.Toast(label.Length == 0
            ? string.Format(Str("L.S.Eve.LabelCleared"), accountId)
            : string.Format(Str("L.S.Eve.LabelSet"), accountId, label));
        return true;
    }

    /// <summary>One detection round: the newest character and account files, and whether the character's file was
    /// written at or after <paramref name="sinceUtc"/> (a real login, not an old file).</summary>
    public (string? CharId, string? UserId, bool Fresh) DetectSince(DateTime sinceUtc)
    {
        var dir = EveDir;
        if (dir.Length == 0) return (null, null, false);
        var (charId, userId, _) = EveManagerService.DetectNewestCharAccountPair(dir);
        if (charId == null || userId == null) return (charId, userId, false);
        bool fresh = Directory.EnumerateDirectories(dir, "settings*").Any(d =>
        {
            var file = Path.Combine(d, $"core_char_{charId}.dat");
            return File.Exists(file) && File.GetLastWriteTimeUtc(file) >= sinceUtc;
        });
        return (charId, userId, fresh);
    }

    /// <summary>The character belongs to this account only (removed from any other, as the old wizard did).</summary>
    public void Link(string charId, string userId)
    {
        foreach (var list in S.AccountCharacterMap.Values) list.RemoveAll(c => c == charId);
        if (!S.AccountCharacterMap.TryGetValue(userId, out var chars)) S.AccountCharacterMap[userId] = chars = new List<string>();
        if (!chars.Contains(charId)) chars.Add(charId);
        _ctx.Applier.Request(ApplyScope.Save);
    }

    /// <summary>Old OnAcctDetectWizard, as questions: log everyone out, log one character in, confirm the pair, again.</summary>
    public void DetectInteractive()
    {
        if (EveDir.Length == 0) { Toast("L.S.Eve.NoFolder"); return; }
        var title = Str("L.S.Eve.DetectTitle");
        if (!_ctx.Host.Confirm(title, Str("L.S.Eve.DetectStep1"), SafeAnswer.First)) return;
        while (true)
        {
            var since = DateTime.UtcNow;   // only a character file written after this counts as the new login
            if (!_ctx.Host.Confirm(title, Str("L.S.Eve.DetectStep2"), SafeAnswer.First)) break;
            var (charId, userId, fresh) = DetectSince(since);
            if (charId == null || userId == null)
            {
                if (_ctx.Host.Confirm(title, Str("L.S.Eve.DetectNone"), SafeAnswer.First)) continue;
                break;
            }
            if (!fresh)
            {
                if (_ctx.Host.Confirm(title, Str("L.S.Eve.DetectNotFresh"), SafeAnswer.First)) continue;
                break;
            }
            var name = Names.TryGetValue(charId, out var n) && n.Length > 0 ? n : string.Format(Str("L.S.Eve.CharacterId"), charId);
            if (_ctx.Host.Confirm(title, string.Format(Str("L.S.Eve.DetectFound"), name, userId), SafeAnswer.First))
            {
                Link(charId, userId);
                _ctx.Host.Toast(string.Format(Str("L.S.Eve.Linked"), name, userId));
            }
            if (!_ctx.Host.Confirm(title, Str("L.S.Eve.DetectAnother"), SafeAnswer.First)) break;
        }
        Toast("L.S.Eve.DetectDone");
    }

    // ── Names from ESI ──

    /// <summary>Character numbers in any profile that have no name yet, each once.</summary>
    public IReadOnlyList<string> MissingNameIds() =>
        Profiles.SelectMany(p => EveManagerService.ListCharacters(p.Path, Names)).Select(c => c.Id)
            .Distinct().Where(id => !Names.ContainsKey(id)).ToList();

    /// <summary>Old OnFetchESI: EveManagerService.EnrichWithESI, unchanged (a POST to ESI's public /universe/names with
    /// character numbers only, no login; the owner keeps it direct: a player's PC can't use the server proxy, IDEAS P33).
    /// The names it finds are kept in the settings' name cache. Returns how many names were added, or null when none
    /// was missing. In a test, give the model its own fetch.</summary>
    public async Task<int?> FetchMissingNamesAsync()
    {
        var missing = MissingNameIds();
        if (missing.Count == 0) return null;
        int before = Names.Count;
        try
        {
            await _fetchNames(Names, missing);
        }
        finally
        {
            // Names found before a failure are kept too, so the next try doesn't ask for them again.
            var found = missing.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]);
            if (EveManagerService.RememberNames(S, found, DateTime.Now) > 0) _ctx.Applier.Request(ApplyScope.Save);
        }
        return Names.Count - before;
    }

    /// <summary>Fetch missing names with toasts. Offline or refused: a plain message, not the exception text.</summary>
    public async Task FetchNamesInteractive()
    {
        if (Profiles.Count == 0) { _ctx.Host.Toast(NoProfilesText()); return; }   // nothing to look up: no network
        try
        {
            var missing = MissingNameIds();
            if (missing.Count == 0) { Toast("L.S.Eve.NamesComplete"); return; }
            _ctx.Host.Toast(string.Format(Str("L.S.Eve.Fetching"), missing.Count));
            var added = await FetchMissingNamesAsync();
            _ctx.Host.Toast(string.Format(Str("L.S.Eve.Fetched"), added ?? 0));
        }
        catch (Exception)
        {
            Toast("L.S.Eve.FetchFailed");
        }
    }
}
