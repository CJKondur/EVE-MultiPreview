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
    private bool _loadedSuccessfully = false;

    public AppSettings Settings => _settings;

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

    /// <summary>The text Save writes for these settings. Two files hold the same settings when their Canonical texts
    /// are equal (the settings move checks its copy this way, IDEAS #57).</summary>
    public static string Canonical(AppSettings settings) =>
        JsonSerializer.Serialize(AhkConfigRoot.FromAppSettings(settings), _jsonOptions);

    /// <summary>True once Load has read the file (or created it). False before Load and after a load that failed: the
    /// session then runs on defaults and saving is blocked, and App offers the restore prompt (IDEAS #49).</summary>
    public bool LoadedSuccessfully => _loadedSuccessfully;

    /// <summary>Why the last Load failed, or null. A file another program holds open fails with an IOException, which
    /// App retries before it offers a restore.</summary>
    public Exception? LastLoadError { get; private set; }

    public Profile CurrentProfile => _settings.Profiles.GetValueOrDefault(_settings.LastUsedProfile) ?? new Profile();

    public SettingsService(string? settingsPath = null)
    {
        // The settings folder App chose at startup (IDEAS #57): %APPDATA%\EVE MultiPreview, or the exe folder.
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
    }

    /// <summary>Load settings from disk. Creates default settings if file doesn't exist.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                // The AHK nested format (has "global_Settings") or the flat C# format. A file that parses to nothing
                // throws here and is handled like any other unreadable file below (.bak, error log, restore prompt).
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
                        System.Diagnostics.Debug.WriteLine($"[Settings] Failed to migrate EVE-O config: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ex.Message}");
            LastLoadError = ex;
            LogError(ex);
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
    public void Save()
    {
        SaveStatus status;
        lock (_saveLock)
        {
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
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ex.Message}");
                LogError(ex);
                status = SaveStatus.WriteFailed;
            }
        }
        SaveCompleted?.Invoke(status);
    }

    /// <summary>Debounced save — waits 200ms before actually saving (matches AHK Save_Settings_Delay_Timer).</summary>
    public void SaveDelayed()
    {
        _debounceSaveTimer?.Stop();
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = new System.Timers.Timer(200) { AutoReset = false };
        _debounceSaveTimer.Elapsed += (_, _) => Save();
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
            _settings.Profiles[name] = new Profile();
            _settings.LastUsedProfile = name;
            Save();
        }
    }

    public bool DeleteProfile(string name)
    {
        if (_settings.Profiles.Count <= 1) return false;
        if (!_settings.Profiles.ContainsKey(name)) return false;

        _settings.Profiles.Remove(name);
        if (_settings.LastUsedProfile == name)
            _settings.LastUsedProfile = _settings.Profiles.Keys.First();
        Save();
        return true;
    }

    public string[] GetProfileNames() => _settings.Profiles.Keys.ToArray();

    /// <summary>Returns the currently active Profile object.</summary>
    public Profile? GetActiveProfile()
    {
        return _settings.Profiles.GetValueOrDefault(_settings.LastUsedProfile);
    }

    /// <summary>Replace the entire settings object (used by import).</summary>
    public void ReplaceSettings(AppSettings newSettings)
    {
        _settings = newSettings;
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

    private static void LogError(Exception ex)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppDomain.CurrentDomain.BaseDirectory;
            string logPath = Path.Combine(exeDir, "error_log.txt");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Settings Save Error: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n";
            File.AppendAllText(logPath, entry);
        }
        catch { }
    }

    public void Dispose()
    {
        _debounceSaveTimer?.Stop();
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = null;
    }
}
