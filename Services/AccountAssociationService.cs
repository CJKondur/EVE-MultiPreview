using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Timers;

namespace EveMultiPreview.Services;

/// <summary>
/// Learns which EVE characters belong to which EVE account by watching the
/// settings folder for co-writes. When you log out (or switch characters),
/// EVE flushes that character's <c>core_char_&lt;charId&gt;.dat</c> and the
/// account's <c>core_user_&lt;userId&gt;.dat</c> within the same brief window.
/// A cluster that contains exactly one of each — in the same settings_*
/// folder — is an unambiguous character→account pairing.
///
/// There is no public ESI endpoint or filename link for account IDs, so this
/// observational approach is the only way to give the Account Copy panel a
/// human-recognisable label. Ambiguous clusters (multiple simultaneous
/// logouts) are skipped; they resolve on a later, non-simultaneous logout.
///
/// Pure local file-system monitoring — no EVE ESI / SSO / zKillboard access.
/// </summary>
public sealed class AccountAssociationService : IDisposable
{
    private static readonly Regex CharRx = new(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UserRx = new(@"^core_user_(\d+)\.dat$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly List<(string Dir, bool IsUser, string Id)> _buffer = new();
    private readonly System.Timers.Timer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised when a new character→account pairing is observed.
    /// Fires on a background thread — marshal to the UI thread if needed.</summary>
    public event Action<string /*charId*/, string /*accountId*/>? PairLearned;

    public AccountAssociationService()
    {
        // 2.5s after the last .dat write settles, correlate the cluster.
        // EVE's char + user flush land within well under a second of each
        // other; the generous window just lets a slow disk settle.
        _debounce = new System.Timers.Timer(2500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => ProcessBuffer();
    }

    /// <summary>Begin watching the EVE settings parent directory (the folder
    /// that contains the settings_* sub-folders). Safe to call with a missing
    /// or empty path — the service simply stays idle.</summary>
    public void Start(string eveSettingsParentDir)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(eveSettingsParentDir) || !Directory.Exists(eveSettingsParentDir))
        {
            Debug.WriteLine($"[AccountAssoc] ⏭ No EVE settings dir — watcher idle ('{eveSettingsParentDir}')");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(eveSettingsParentDir)
            {
                Filter = "core_*.dat",
                IncludeSubdirectories = true,           // covers every settings_* sub-folder
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watcher.Changed += OnDatFileEvent;
            _watcher.Created += OnDatFileEvent;
            _watcher.EnableRaisingEvents = true;
            Debug.WriteLine($"[AccountAssoc] 👁 Watching {eveSettingsParentDir}");
        }
        catch (Exception ex)
        {
            // A watcher failure must never break the app — Account Copy still
            // works, it just won't auto-learn labels.
            Debug.WriteLine($"[AccountAssoc] ❌ Watcher start failed: {ex.GetType().Name}: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnDatFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        try
        {
            var name = Path.GetFileName(e.FullPath);
            var dir = Path.GetDirectoryName(e.FullPath) ?? "";

            var cm = CharRx.Match(name);
            var um = UserRx.Match(name);
            if (!cm.Success && !um.Success) return;

            lock (_lock)
            {
                _buffer.Add((dir, um.Success, um.Success ? um.Groups[1].Value : cm.Groups[1].Value));
            }
            // Restart the debounce — process once writes go quiet.
            _debounce.Stop();
            _debounce.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AccountAssoc] ⚠ Event error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ProcessBuffer()
    {
        List<(string Dir, bool IsUser, string Id)> snapshot;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            snapshot = new List<(string, bool, string)>(_buffer);
            _buffer.Clear();
        }

        // Correlate per settings folder: EVE always writes a character's
        // core_char and its account's core_user into the same settings_*
        // folder. Only an unambiguous 1-user + 1-char cluster yields a
        // pairing — anything else (two accounts logging out together) is
        // skipped and will resolve on a later non-simultaneous logout.
        foreach (var dirGroup in snapshot.GroupBy(x => x.Dir))
        {
            var users = dirGroup.Where(x => x.IsUser).Select(x => x.Id).Distinct().ToList();
            var chars = dirGroup.Where(x => !x.IsUser).Select(x => x.Id).Distinct().ToList();

            if (users.Count == 1 && chars.Count == 1)
            {
                Debug.WriteLine($"[AccountAssoc] 🔗 Learned: char {chars[0]} → account {users[0]}");
                try { PairLearned?.Invoke(chars[0], users[0]); }
                catch (Exception ex) { Debug.WriteLine($"[AccountAssoc] ⚠ PairLearned handler: {ex.Message}"); }
            }
            else if (users.Count > 0 || chars.Count > 0)
            {
                Debug.WriteLine($"[AccountAssoc] ⏭ Ambiguous cluster skipped: {users.Count} user / {chars.Count} char writes in {Path.GetFileName(dirGroup.Key)}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnDatFileEvent;
            _watcher.Created -= OnDatFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce.Stop();
        _debounce.Dispose();
    }
}
