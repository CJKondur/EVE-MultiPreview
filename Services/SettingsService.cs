using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveMultiPreview.Converters;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

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
    public Profile CurrentProfile => _settings.Profiles.GetValueOrDefault(_settings.LastUsedProfile) ?? new Profile();

    public SettingsService(string? settingsPath = null)
    {
        // For single-file self-contained apps, AppDomain.CurrentDomain.BaseDirectory
        // points to the temp extraction directory — NOT where the exe lives.
        // Environment.ProcessPath gives the actual exe path on disk.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppDomain.CurrentDomain.BaseDirectory;
        _settingsPath = settingsPath ?? Path.Combine(exeDir, "EVE MultiPreview.json");
    }

    /// <summary>Load settings from disk. Creates default settings if file doesn't exist.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);

                // Detect format: AHK JSON has "global_Settings" wrapper
                if (json.Contains("\"global_Settings\""))
                {
                    // AHK format → deserialize to intermediate model, then convert
                    var ahkRoot = JsonSerializer.Deserialize<AhkConfigRoot>(json, _jsonOptions);
                    if (ahkRoot != null)
                    {
                        _settings = ahkRoot.ToAppSettings();
                        _loadedSuccessfully = true;
                        System.Diagnostics.Debug.WriteLine($"[Settings] Loaded AHK format from {_settingsPath}");
                    }
                    else
                    {
                        _settings = new AppSettings();
                        _loadedSuccessfully = false;
                    }
                }
                else
                {
                    // C# native format (future) — try direct deserialize
                    _settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
                    _loadedSuccessfully = true;
                    System.Diagnostics.Debug.WriteLine($"[Settings] Loaded C# format from {_settingsPath}");
                }
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
                Save();
                System.Diagnostics.Debug.WriteLine($"[Settings] Created settings at {_settingsPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ex.Message}");
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
    


    /// <summary>Save settings to disk using atomic write (temp file + rename). Thread-safe.
    /// Always saves in AHK-compatible format for backwards compatibility.</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                // Guard: don't overwrite user's real config with defaults after a load failure
                if (!_loadedSuccessfully)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] Save blocked — config was not loaded successfully");
                    return;
                }

                // Convert flat AppSettings → nested AHK format for backwards compat
                var ahkRoot = AhkConfigRoot.FromAppSettings(_settings);
                string json = JsonSerializer.Serialize(ahkRoot, _jsonOptions);
                string tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
                _lastSaveTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ex.Message}");
                LogError(ex);
            }
        }
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

    // ── Profile Management ──────────────────────────────────────────

    public void SwitchProfile(string profileName)
    {
        if (_settings.Profiles.ContainsKey(profileName))
        {
            _settings.LastUsedProfile = profileName;
            Save();
        }
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
