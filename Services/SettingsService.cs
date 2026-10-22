using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveMultiPreview.Converters;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Outcome of one SettingsService.Save() attempt (spec §8.5 save-failure bar).</summary>
public enum SaveStatus
{
    Saved,
    /// <summary>Writing the file failed (disk full, read-only folder, file locked).</summary>
    WriteFailed,
    /// <summary>The file failed to load at startup, so saving is blocked to protect it.</summary>
    BlockedAfterLoadError,
}

/// <summary>
/// Manages loading, saving, and accessing application settings.
/// Uses atomic file writes (temp + rename) to prevent data loss — same pattern as AHK version.
/// Compatible with "EVE MultiPreview.json" format from the AHK app.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        // Never strip null properties — preserves full AHK config structure on save
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        // Preserve AHK-style property names exactly
        PropertyNamingPolicy = null,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new BoolOrIntConverter(), new StringFallbackConverter() }
    };

    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private readonly object _saveLock = new();
    private DateTime _lastSaveTime = DateTime.MinValue;
    private System.Timers.Timer? _debounceSaveTimer;
    private volatile bool _savePending;   // set by SaveDelayed, cleared by Save: Dispose flushes a change the timer never wrote
    private bool _loadedSuccessfully = false;

    public AppSettings Settings => _settings;

    /// <summary>The setting changes made in the settings window in this run of the app (IDEAS P17), for its Recently changed
    /// card. Lives here because this object outlives the window: closing and reopening it keeps the list. Memory only.</summary>
    public SettingsUi.SettingChanges Changes { get; } = new();

    /// <summary>Directory the config lives in — used to co-locate layout_presets.json.</summary>
    public string ConfigDirectory => Path.GetDirectoryName(_settingsPath) ?? AppContext.BaseDirectory;
    /// <summary>Full path of the settings file (backups and exports copy it).</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>Parses settings-file text in either format (AHK nested, or flat), with the same
    /// options Load uses. Throws JsonException when the text is not a settings file.</summary>
    public static AppSettings ParseJson(string json)
    {
        if (json.Contains("\"global_Settings\"") || json.Contains("\"_Profiles\""))
            return (JsonSerializer.Deserialize<AhkConfigRoot>(json, _jsonOptions)
                    ?? throw new JsonException("Not a settings file")).ToAppSettings();
        using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions
               { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("Profiles", StringComparison.OrdinalIgnoreCase)))
                throw new JsonException("Not a settings file");
        }
        return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions)
               ?? throw new JsonException("Not a settings file");
    }

    /// <summary>Parses settings-file text exactly as Load does: the AHK nested format when the text has
    /// "global_Settings", otherwise the flat format. Throws (JsonException and others) where Load would fail.</summary>
    public static AppSettings ParseForLoad(string json)
    {
        if (json.Contains("\"global_Settings\""))
            return (JsonSerializer.Deserialize<AhkConfigRoot>(json, _jsonOptions)
                    ?? throw new JsonException("Not a settings file")).ToAppSettings();
        return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
    }

    /// <summary>The text Save writes for these settings. The tests build their settings files with it; the settings
    /// move checks its copy by bytes and a parse instead (SettingsHome.IsSameSettingsFile).</summary>
    public static string Canonical(AppSettings settings) =>
        JsonSerializer.Serialize(AhkConfigRoot.FromAppSettings(settings), _jsonOptions);

    /// <summary>True once Load has read the file (or created it). False before Load and after a load that failed: the
    /// session then runs on defaults and saving is blocked, and App offers the restore prompt (IDEAS #49).</summary>
    public bool LoadedSuccessfully => _loadedSuccessfully;

    /// <summary>Why the last Load failed, or null. A file another program holds open fails with an IOException, which
    /// App retries before it offers a restore.</summary>
    public Exception? LastLoadError { get; private set; }

    /// <summary>IDEAS P91: the line of a settings file where its first problem is, counted from 1 like a text editor
    /// (the parser counts from 0), for an error Load gave. Null when the error is not a parse error or has no position
    /// (a file another program holds, "not a settings file"). Only the number of a line: never the JSON path (it names
    /// characters), a column or the value; the dialog, the save bar and the error log all say it through here.</summary>
    public static int? FailedLine(Exception? error) =>
        error is JsonException { LineNumber: long line and < int.MaxValue } ? (int)line + 1 : null;

    public Profile CurrentProfile => _settings.Profiles.GetValueOrDefault(_settings.LastUsedProfile) ?? new Profile();

    public SettingsService(string? settingsPath = null)
    {
        // The settings folder App chose at startup (IDEAS #57): %APPDATA%\EVE MultiPreview, or the exe folder.
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
    }

    /// <summary>Load settings from disk. Creates default settings if file doesn't exist. <paramref name="recordFailure"/>
    /// false leaves a failed load out of the error log, for a caller that tries again and records the last failure once
    /// (StartupSettings); LastLoadError is set either way.</summary>
    public void Load(bool recordFailure = true)
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                // The AHK nested format (has "global_Settings") or the flat C# format. Text that is not JSON, or an
                // AHK-format file that deserializes to null, throws here and is handled like any other unreadable file
                // below (.bak, error log, restore prompt). JSON without "global_Settings" is read as the flat format:
                // "{}", "null" or any object that holds none of its settings loads as defaults.
                _settings = ParseForLoad(File.ReadAllText(_settingsPath));
                _loadedSuccessfully = true;
                LastLoadError = null;
                System.Diagnostics.Debug.WriteLine($"[Settings] Loaded {_settingsPath}");
            }
            else
            {
                // New file — check if EVE-O Preview legacy config exists
                bool migratedEveO = false;
                
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string localEveO = Path.Combine(exeDir, "EVE-O Preview.json");
                string appDataEveO = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVE-O Preview", "EVE-O Preview.json");
                
                string? eveOToMigrate = null;
                if (File.Exists(localEveO)) eveOToMigrate = localEveO;
                else if (File.Exists(appDataEveO)) eveOToMigrate = appDataEveO;

                if (eveOToMigrate != null)
                {
                    try
                    {
                        string eveOJson = File.ReadAllText(eveOToMigrate);
                        var eveORoot = JsonSerializer.Deserialize<EveOConfigRoot>(eveOJson, _jsonOptions);
                        if (eveORoot != null)
                        {
                            _settings = eveORoot.ToAppSettings();
                            _loadedSuccessfully = true;
                            migratedEveO = true;
                            System.Diagnostics.Debug.WriteLine($"[Settings] Successfully migrated legacy EVE-O Preview config from {eveOToMigrate}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Settings] Failed to migrate EVE-O config: {ErrorLog.Describe(ex)}");
                    }
                }

                if (!migratedEveO)
                {
                    _settings = new AppSettings();
                }

                _loadedSuccessfully = true; // New file or migrated — safe to save
                LastLoadError = null;
                Save();
                System.Diagnostics.Debug.WriteLine($"[Settings] Created settings at {_settingsPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ErrorLog.Describe(ex)}");   // no JSON path: it names characters
            LastLoadError = ex;
            if (recordFailure) LogError(ex);
            // Back up the broken file so we don't lose user data
            try
            {
                var backupPath = _settingsPath + ".bak";
                if (File.Exists(_settingsPath))
                    File.Copy(_settingsPath, backupPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"[Settings] Backed up original config to {backupPath}");
            }
            catch { }
            _settings = new AppSettings();
            _loadedSuccessfully = false; // CRITICAL: Don't allow Save to overwrite real config
        }
        
    }
    


    /// <summary>Raised after every Save() attempt with its outcome. Raised on the thread that
    /// saved: SaveDelayed saves on a timer thread, so UI subscribers must marshal.</summary>
    public event Action<SaveStatus>? SaveCompleted;

    /// <summary>Save settings to disk using atomic write (temp file + rename). Thread-safe.
    /// Always saves in AHK-compatible format for backwards compatibility.</summary>
    public void Save() => SaveCore(onlyIfPending: false);

    /// <summary>The one write path. The pending flag (P96) is checked AND cleared inside _saveLock, so a debounce
    /// tick and Dispose can never both write one change, and a tick that lost the race to Dispose writes nothing.
    /// With <paramref name="onlyIfPending"/> it returns without writing (and without SaveCompleted) when no change
    /// is waiting; an explicit Save() writes regardless. SaveCompleted is raised outside the lock.</summary>
    private void SaveCore(bool onlyIfPending)
    {
        SaveStatus status;
        lock (_saveLock)
        {
            if (onlyIfPending && !_savePending) return;
            _savePending = false;   // whatever SaveDelayed was waiting to write is written now
            try
            {
                // Guard: don't overwrite user's real config with defaults after a load failure
                if (!_loadedSuccessfully)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] Save blocked — config was not loaded successfully");
                    status = SaveStatus.BlockedAfterLoadError;
                }
                else
                {
                    // Convert flat AppSettings → nested AHK format for backwards compat
                    var ahkRoot = AhkConfigRoot.FromAppSettings(_settings);
                    string json = JsonSerializer.Serialize(ahkRoot, _jsonOptions);
                    string tempPath = _settingsPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _settingsPath, overwrite: true);
                    _lastSaveTime = DateTime.Now;
                    status = SaveStatus.Saved;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ErrorLog.Describe(ex)}");
                LogError(ex);
                status = SaveStatus.WriteFailed;
            }
        }
        SaveCompleted?.Invoke(status);
    }

    /// <summary>Writes a change that is still waiting for its delayed save, now, and nothing else (P96): what
    /// Windows' SessionEnding calls, because OnExit may not get to run before the process is ended. Unlike
    /// <see cref="Dispose"/> it leaves the service running, since a session whose end is cancelled goes on. It is
    /// the same path as the timer's tick (<see cref="SaveCore"/>): a settings file that failed to load is never
    /// overwritten, a change is written once, and nothing is written when nothing is pending. Never throws: it runs
    /// in an event handler, and a subscriber of SaveCompleted that throws must not stop the handlers behind it.</summary>
    public void Flush()
    {
        try { SaveCore(onlyIfPending: true); }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>Debounced save — waits 200ms before actually saving (matches AHK Save_Settings_Delay_Timer).</summary>
    public void SaveDelayed()
    {
        _savePending = true;
        _debounceSaveTimer?.Stop();
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = new System.Timers.Timer(200) { AutoReset = false };
        // A tick that was already queued when Dispose (or an explicit Save) wrote the change writes nothing.
        _debounceSaveTimer.Elapsed += (_, _) => SaveCore(onlyIfPending: true);
        _debounceSaveTimer.Start();
    }

    // ── Thumbnail Positions ─────────────────────────────────────────

    public void SaveThumbnailPosition(string characterName, int x, int y, int width, int height)
    {
        CurrentProfile.ThumbnailPositions[characterName] = new ThumbnailRect
        {
            X = x, Y = y, Width = width, Height = height
        };
        SaveDelayed();
    }

    public ThumbnailRect? GetThumbnailPosition(string characterName)
    {
        return CurrentProfile.ThumbnailPositions.GetValueOrDefault(characterName);
    }

    /// <summary>Drop all saved per-character thumbnail positions for the current
    /// profile so the next layout falls back to the default flow-layout. Caller
    /// re-lays out the live thumbnails and persists.</summary>
    public void ClearThumbnailPositions()
    {
        CurrentProfile.ThumbnailPositions.Clear();
    }

    /// <summary>Deep copy of the current profile's saved thumbnail positions — for
    /// the layout undo/redo stack.</summary>
    public Dictionary<string, ThumbnailRect> GetThumbnailPositionsSnapshot()
    {
        var copy = new Dictionary<string, ThumbnailRect>();
        foreach (var (k, v) in CurrentProfile.ThumbnailPositions)
            copy[k] = new ThumbnailRect { X = v.X, Y = v.Y, Width = v.Width, Height = v.Height };
        return copy;
    }

    /// <summary>Replace the current profile's saved thumbnail positions with a
    /// snapshot (layout undo/redo) and persist.</summary>
    public void RestoreThumbnailPositions(Dictionary<string, ThumbnailRect> snapshot)
    {
        var dict = CurrentProfile.ThumbnailPositions;
        dict.Clear();
        foreach (var (k, v) in snapshot)
            dict[k] = new ThumbnailRect { X = v.X, Y = v.Y, Width = v.Width, Height = v.Height };
        Save();
    }

    // ── Profile Management ──────────────────────────────────────────

    /// <summary>Raised AFTER LastUsedProfile actually changes (and the new state
    /// is persisted). Subscribers like the open Settings window use this to
    /// re-bind their UI to the newly-active profile so further edits don't
    /// leak into the wrong profile. Not raised when the requested profile is
    /// already the active one.</summary>
    public event Action<string>? ProfileSwitched;

    public void SwitchProfile(string profileName)
    {
        if (!_settings.Profiles.ContainsKey(profileName)) return;
        bool changed = _settings.LastUsedProfile != profileName;
        _settings.LastUsedProfile = profileName;
        Save();
        if (changed) ProfileSwitched?.Invoke(profileName);
    }

    public void CreateProfile(string name)
    {
        if (!_settings.Profiles.ContainsKey(name))
        {
            var before = GroupAlertChoices.GroupNames(_settings);
            _settings.Profiles[name] = new Profile();
            _settings.LastUsedProfile = name;
            // IDEAS #31: the groups of every profile keep their alert choices; what no group has is forgotten.
            GroupAlertChoices.Follow(_settings, before);
            Save();
        }
    }

    public bool DeleteProfile(string name)
    {
        if (_settings.Profiles.Count <= 1) return false;
        if (!_settings.Profiles.ContainsKey(name)) return false;

        var before = GroupAlertChoices.GroupNames(_settings);
        _settings.Profiles.Remove(name);
        if (_settings.LastUsedProfile == name)
            _settings.LastUsedProfile = _settings.Profiles.Keys.First();
        // IDEAS #31: the alert choices of the groups only this profile had go with it, so that a later group of
        // such a name starts with none.
        GroupAlertChoices.Follow(_settings, before);
        Save();
        return true;
    }

    public string[] GetProfileNames() => _settings.Profiles.Keys.ToArray();

    /// <summary>Returns the currently active Profile object.</summary>
    public Profile? GetActiveProfile()
    {
        return _settings.Profiles.GetValueOrDefault(_settings.LastUsedProfile);
    }

    /// <summary>The AppSettings properties a settings file holds: public, read/write, not [JsonIgnore] (those forward to
    /// the profile in use, which the copied Profiles already bring).</summary>
    private static readonly System.Reflection.PropertyInfo[] FileProperties = typeof(AppSettings)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                    && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)))
        .ToArray();

    /// <summary>Replaces every setting with <paramref name="newSettings"/>' (import, P112). The values are copied into the
    /// SAME object: the log monitor, the hotkeys' guard, the alert hub and the windows that were handed it at start see
    /// the import at once. Each property is assigned, never a collection changed in place, so a reader on another
    /// thread (the log monitor's) sees the old value or the new one. Runs on the UI thread.</summary>
    public void ReplaceSettings(AppSettings newSettings)
    {
        if (!ReferenceEquals(newSettings, _settings))
            foreach (var p in FileProperties)
                p.SetValue(_settings, p.GetValue(newSettings));
        _loadedSuccessfully = true;
        Save();
    }

    // ── Client Positions ────────────────────────────────────────────

    public void SaveClientPosition(string characterName, int x, int y, int width, int height, bool isMaximized)
    {
        CurrentProfile.ClientPositions[characterName] = new ClientPosition
        {
            X = x, Y = y, Width = width, Height = height, IsMaximized = isMaximized ? 1 : 0
        };
        SaveDelayed();
    }

    public ClientPosition? GetClientPosition(string characterName)
    {
        return CurrentProfile.ClientPositions.GetValueOrDefault(characterName);
    }

    // ── Error Logging ───────────────────────────────────────────────

    /// <summary>A failed load or save: counted, and written to the error log in the settings folder (IDEAS #55).
    /// Never throws.</summary>
    private static void LogError(Exception ex) => ErrorLog.Shared.Record(ex, "Settings", DateTime.Now);

    /// <summary>Stops the debounce timer and writes a change still waiting on it (P96). Guarantees: a save already
    /// in flight (a timer tick holding _saveLock) finishes before this returns; a pending change is written exactly
    /// once, by whichever of the tick or this call takes the lock first (the flag is checked and cleared inside
    /// the lock); a second call writes nothing. Waits for one file write at most. Never throws.</summary>
    public void Dispose()
    {
        _debounceSaveTimer?.Stop();
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = null;
        try { SaveCore(onlyIfPending: true); }
        catch (Exception ex) { LogError(ex); }   // e.g. a SaveCompleted subscriber that throws at exit
    }
}
