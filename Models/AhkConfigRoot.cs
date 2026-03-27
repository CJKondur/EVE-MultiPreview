using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

/// <summary>
/// Root model that matches the AHK JSON structure EXACTLY.
/// Used as an intermediate format for deserialization/serialization.
/// </summary>
public class AhkConfigRoot
{
    [JsonPropertyName("EveManager")]
    public AhkEveManager? EveManager { get; set; }

    [JsonPropertyName("_Profiles")]
    public Dictionary<string, AhkProfile> Profiles { get; set; } = new()
    {
        ["Default"] = new AhkProfile()
    };

    [JsonPropertyName("global_Settings")]
    public AhkGlobalSettings GlobalSettings { get; set; } = new();

    // ── Conversion: AhkConfigRoot → AppSettings ────────────────────

    public AppSettings ToAppSettings()
    {
        var s = new AppSettings();
        var g = GlobalSettings;

        // ── Global Settings ─────────────────────────────────────────
        s.ThumbnailStartLocation = g.ThumbnailStartLocation ?? new ThumbnailRect { X = 20, Y = 20, Width = 280, Height = 180 };
        s.ThumbnailMinimumSize = g.ThumbnailMinimumSize ?? new ThumbnailSize { Width = 100, Height = 60 };
        s.ThumbnailSnap = g.ThumbnailSnap != 0;
        s.ThumbnailSnapDistance = g.ThumbnailSnapDistance;
        s.ThumbnailBackgroundColor = g.ThumbnailBackgroundColor ?? "0x57504e";
        s.GlobalHotkeys = g.GlobalHotkeys != 0;
        s.SuspendHotkey = g.SuspendHotkeysHotkey ?? "";
        s.ClickThroughHotkey = g.ClickThroughHotkey ?? "";
        s.HideShowThumbnailsHotkey = g.HideShowThumbnailsHotkey ?? "";
        s.HidePrimaryHotkey = g.HidePrimaryHotkey ?? "";
        s.HideSecondaryHotkey = g.HideSecondaryHotkey ?? "";
        s.ProfileCycleForwardHotkey = g.ProfileCycleForwardHotkey ?? "";
        s.ProfileCycleBackwardHotkey = g.ProfileCycleBackwardHotkey ?? "";
        s.ShowSessionTimer = g.ShowSessionTimer != 0;
        s.ShowSystemName = g.ShowSystemName != 0;
        s.PreferredMonitor = g.PreferredMonitor;
        s.LockPositions = g.LockPositions != 0;
        s.HideActiveThumbnail = g.HideActiveThumbnail != 0;
        s.IndividualThumbnailResize = g.IndividualThumbnailResize != 0;
        s.SimpleMode = g.SimpleMode != 0;
        s.SetupCompleted = g.SetupCompleted != 0;
        s.LastUsedProfile = g.LastUsedProfile ?? "Default";
        s.EnableKeyBlockGuard = g.EnableKeyBlockGuard != 0;

        // Alert settings
        s.EnableAttackAlerts = g.EnableAttackAlerts != 0;
        s.PveMode = g.PVEMode != 0;
        s.EnableAlertSounds = g.EnableAlertSounds != 0;
        s.AlertSoundVolume = g.AlertSoundVolume;
        s.AlertHubEnabled = g.AlertHubEnabled != 0;
        s.AlertHubX = g.AlertHubX;
        s.AlertHubY = g.AlertHubY;
        s.AlertToastDirection = g.AlertToastDirection;
        s.AlertToastDuration = g.AlertToastDuration;

        // Dicts
        s.AlertColors = g.AlertColors ?? new();
        s.AlertSounds = g.AlertSounds ?? new();
        s.SoundCooldowns = g.SoundCooldowns ?? new();
        s.SeverityColors = g.SeverityColors ?? new();
        s.SeverityCooldowns = g.SeverityCooldowns ?? new();
        s.SeverityFlashRates = g.SeverityFlashRates ?? new();
        s.SeverityTrayNotify = ConvertIntDictToBoolDict(g.SeverityTrayNotify);
        s.EnabledAlertTypes = ConvertIntDictToBoolDict(g.EnabledAlertTypes);

        // Log monitoring
        s.EnableChatLogMonitoring = g.EnableChatLogMonitoring != 0;
        s.EnableGameLogMonitoring = g.EnableGameLogMonitoring != 0;
        s.ChatLogDirectory = g.ChatLogDirectory ?? "";
        s.GameLogDirectory = g.GameLogDirectory ?? "";

        // Stats
        s.StatOverlayFontSize = g.StatOverlayFontSize;
        s.StatOverlayOpacity = g.StatOverlayOpacity;
        s.StatLoggingEnabled = g.StatLogEnabled != 0;
        s.StatLogDirectory = g.StatLogPath ?? "";
        s.StatLogRetentionDays = g.StatLogRetentionDays;
        s.PerCharacterStats = ConvertStatOverlayConfig(g.StatOverlayConfig);
        s.StatWindowPositions = g.StatWindowPositions ?? new();

        // RTSS
        s.RtssEnabled = g.RTSS_Enabled != 0;
        s.RtssFpsLimit = g.RTSS_IdleFPS;

        // Char select
        s.CharSelectCyclingEnabled = g.CharSelectCyclingEnabled != 0;
        s.CharSelectForwardHotkey = g.CharSelectForwardHotkey ?? "";
        s.CharSelectBackwardHotkey = g.CharSelectBackwardHotkey ?? "";

        // Process monitor
        s.ShowProcessStats = g.ShowProcessStats != 0;
        s.ProcessStatsTextSize = g.ProcessStatsTextSize.ToString();

        // Thumbnail annotations
        s.ThumbnailAnnotations = g.ThumbnailAnnotations ?? new();

        // Quick-Switch Wheel
        s.QuickSwitchHotkey = g.QuickSwitchHotkey ?? "";

        // Settings window
        s.SettingsWindowWidth = g.SettingsWindowWidth;
        s.SettingsWindowHeight = g.SettingsWindowHeight;

        // Eve Manager
        s.EveManagerUseESI = g.EveManagerUseESI != 0;
        s.EveBackupDir = g.EveBackupDir ?? "";
        s.EveSettingsDir = g.EveSettingsDir ?? "";

        // Thumbnail groups (global)
        s.ThumbnailGroups = g.ThumbnailGroups ?? new();

        // EveManager pass-through
        s.EveManager = EveManager;

        // ── Profiles ────────────────────────────────────────────────
        s.Profiles = new();
        foreach (var (name, ahkProfile) in Profiles)
        {
            var profile = new Profile();

            // Direct per-profile data
            profile.ThumbnailPositions = ahkProfile.ThumbnailPositions ?? new();
            profile.ClientPositions = ahkProfile.ClientPossitions ?? new();
            profile.Hotkeys = ConvertHotkeyArray(ahkProfile.Hotkeys);
            profile.HotkeyGroups = ahkProfile.HotkeyGroups ?? new();
            profile.SecondaryThumbnails = ahkProfile.SecondaryThumbnails ?? new();
            profile.ThumbnailVisibility = ahkProfile.ThumbnailVisibility ?? new();
            profile.Groups = ahkProfile.Groups ?? new();
            profile.DontMinimizeClients = ahkProfile.ClientSettings?.DontMinimizeClients ?? new();

            // Thumbnail Settings (per-profile sub-object → flat profile fields)
            var ts = ahkProfile.ThumbnailSettings;
            if (ts != null)
            {
                profile.ShowAllColoredBorders = ts.ShowAllColoredBorders != 0;
                profile.HideThumbnailsOnLostFocus = ts.HideThumbnailsOnLostFocus != 0;
                profile.ShowThumbnailsAlwaysOnTop = ts.ShowThumbnailsAlwaysOnTop != 0;
                profile.ThumbnailOpacity = ts.ThumbnailOpacity;
                profile.ClientHighlightBorderThickness = ts.ClientHighligtBorderthickness;
                profile.ClientHighlightColor = ts.ClientHighligtColor ?? "#E36A0D";
                profile.ShowClientHighlightBorder = ts.ShowClientHighlightBorder != 0;
                profile.ThumbnailTextFont = ts.ThumbnailTextFont ?? "Gill Sans MT";
                profile.ThumbnailTextSize = ts.ThumbnailTextSize.ToString();
                profile.ThumbnailTextColor = ts.ThumbnailTextColor ?? "#FAC57A";
                profile.ShowThumbnailTextOverlay = ts.ShowThumbnailTextOverlay != 0;
                profile.ThumbnailTextMargins = ts.ThumbnailTextMargins ?? new ThumbnailMargins { X = 5, Y = 5 };
                profile.InactiveClientBorderThickness = ts.InactiveClientBorderthickness;
                profile.InactiveClientBorderColor = ts.InactiveClientBorderColor ?? "#8A8A8A";
                profile.NotLoggedInIndicator = ts.NotLoggedInIndicator ?? "text";
                profile.NotLoggedInColor = ts.NotLoggedInColor ?? "#555555";
            }

            // Client Settings (per-profile sub-object)
            var cs = ahkProfile.ClientSettings;
            if (cs != null)
            {
                profile.MinimizeInactiveClients = cs.MinimizeInactiveClients != 0;
                profile.AlwaysMaximize = cs.AlwaysMaximize != 0;
                profile.TrackClientPositions = cs.TrackClientPossitions != 0;
            }

            // Custom Colors (per-profile sub-object with parallel arrays)
            var cc = ahkProfile.CustomColors;
            if (cc != null)
            {
                profile.CustomColorsActive = cc.cColorActive == "1" || cc.cColorActive == "true";
                profile.CustomColors = ConvertParallelArrayColors(cc.cColors);
            }

            s.Profiles[name] = profile;
        }

        return s;
    }

    // ── Conversion: AppSettings → AhkConfigRoot ────────────────────

    public static AhkConfigRoot FromAppSettings(AppSettings s)
    {
        var root = new AhkConfigRoot();
        var g = root.GlobalSettings;

        // ── Global Settings ─────────────────────────────────────────
        g.ThumbnailStartLocation = s.ThumbnailStartLocation;
        g.ThumbnailMinimumSize = s.ThumbnailMinimumSize;
        g.ThumbnailSnap = s.ThumbnailSnap ? 1 : 0;
        g.ThumbnailSnapDistance = s.ThumbnailSnapDistance;
        g.ThumbnailBackgroundColor = s.ThumbnailBackgroundColor;
        g.GlobalHotkeys = s.GlobalHotkeys ? 1 : 0;
        g.SuspendHotkeysHotkey = s.SuspendHotkey;
        g.ClickThroughHotkey = s.ClickThroughHotkey;
        g.HideShowThumbnailsHotkey = s.HideShowThumbnailsHotkey;
        g.HidePrimaryHotkey = s.HidePrimaryHotkey;
        g.HideSecondaryHotkey = s.HideSecondaryHotkey;
        g.ProfileCycleForwardHotkey = s.ProfileCycleForwardHotkey;
        g.ProfileCycleBackwardHotkey = s.ProfileCycleBackwardHotkey;
        g.ShowSessionTimer = s.ShowSessionTimer ? 1 : 0;
        g.ShowSystemName = s.ShowSystemName ? 1 : 0;
        g.PreferredMonitor = s.PreferredMonitor;
        g.LockPositions = s.LockPositions ? 1 : 0;
        g.HideActiveThumbnail = s.HideActiveThumbnail ? 1 : 0;
        g.IndividualThumbnailResize = s.IndividualThumbnailResize ? 1 : 0;
        g.SimpleMode = s.SimpleMode ? 1 : 0;
        g.SetupCompleted = s.SetupCompleted ? 1 : 0;
        g.LastUsedProfile = s.LastUsedProfile;
        g.EnableKeyBlockGuard = s.EnableKeyBlockGuard ? 1 : 0;

        // Alert
        g.EnableAttackAlerts = s.EnableAttackAlerts ? 1 : 0;
        g.PVEMode = s.PveMode ? 1 : 0;
        g.EnableAlertSounds = s.EnableAlertSounds ? 1 : 0;
        g.AlertSoundVolume = s.AlertSoundVolume;
        g.AlertHubEnabled = s.AlertHubEnabled ? 1 : 0;
        g.AlertHubX = s.AlertHubX;
        g.AlertHubY = s.AlertHubY;
        g.AlertToastDirection = s.AlertToastDirection;
        g.AlertToastDuration = s.AlertToastDuration;

        g.AlertColors = s.AlertColors;
        g.AlertSounds = s.AlertSounds;
        g.SoundCooldowns = s.SoundCooldowns;
        g.SeverityColors = s.SeverityColors;
        g.SeverityCooldowns = s.SeverityCooldowns;
        g.SeverityFlashRates = s.SeverityFlashRates;
        g.SeverityTrayNotify = ConvertBoolDictToIntDict(s.SeverityTrayNotify);
        g.EnabledAlertTypes = ConvertBoolDictToIntDict(s.EnabledAlertTypes);

        // Log
        g.EnableChatLogMonitoring = s.EnableChatLogMonitoring ? 1 : 0;
        g.EnableGameLogMonitoring = s.EnableGameLogMonitoring ? 1 : 0;
        g.ChatLogDirectory = s.ChatLogDirectory;
        g.GameLogDirectory = s.GameLogDirectory;

        // Stats
        g.StatOverlayFontSize = s.StatOverlayFontSize;
        g.StatOverlayOpacity = s.StatOverlayOpacity;
        g.StatLogEnabled = s.StatLoggingEnabled ? 1 : 0;
        g.StatLogPath = s.StatLogDirectory;
        g.StatLogRetentionDays = s.StatLogRetentionDays;
        g.StatOverlayConfig = ConvertStatOverlayToAhk(s.PerCharacterStats);
        g.StatWindowPositions = s.StatWindowPositions;

        // RTSS
        g.RTSS_Enabled = s.RtssEnabled ? 1 : 0;
        g.RTSS_IdleFPS = s.RtssFpsLimit;

        // Char select
        g.CharSelectCyclingEnabled = s.CharSelectCyclingEnabled ? 1 : 0;
        g.CharSelectForwardHotkey = s.CharSelectForwardHotkey;
        g.CharSelectBackwardHotkey = s.CharSelectBackwardHotkey;

        // Process monitor
        g.ShowProcessStats = s.ShowProcessStats ? 1 : 0;
        g.ProcessStatsTextSize = int.TryParse(s.ProcessStatsTextSize, out var pfs) ? pfs : 9;

        // Thumbnail annotations
        g.ThumbnailAnnotations = s.ThumbnailAnnotations ?? new();

        // Quick-Switch Wheel
        g.QuickSwitchHotkey = s.QuickSwitchHotkey;

        // Settings window
        g.SettingsWindowWidth = s.SettingsWindowWidth;
        g.SettingsWindowHeight = s.SettingsWindowHeight;

        // Eve Manager
        g.EveManagerUseESI = s.EveManagerUseESI ? 1 : 0;
        g.EveBackupDir = s.EveBackupDir;
        g.EveSettingsDir = s.EveSettingsDir;

        // Thumbnail groups
        g.ThumbnailGroups = s.ThumbnailGroups;

        // EveManager pass-through
        root.EveManager = s.EveManager;

        // ── Profiles ────────────────────────────────────────────────
        root.Profiles = new();
        foreach (var (name, profile) in s.Profiles)
        {
            var ap = new AhkProfile
            {
                ThumbnailPositions = profile.ThumbnailPositions,
                ClientPossitions = profile.ClientPositions,
                Hotkeys = ConvertHotkeyDictToArray(profile.Hotkeys),
                HotkeyGroups = profile.HotkeyGroups,
                SecondaryThumbnails = profile.SecondaryThumbnails,
                ThumbnailVisibility = profile.ThumbnailVisibility,
                Groups = profile.Groups,
            };

            // Thumbnail Settings sub-object
            ap.ThumbnailSettings = new AhkThumbnailSettings
            {
                ShowAllColoredBorders = profile.ShowAllColoredBorders ? 1 : 0,
                HideThumbnailsOnLostFocus = profile.HideThumbnailsOnLostFocus ? 1 : 0,
                ShowThumbnailsAlwaysOnTop = profile.ShowThumbnailsAlwaysOnTop ? 1 : 0,
                ThumbnailOpacity = profile.ThumbnailOpacity,
                ClientHighligtBorderthickness = profile.ClientHighlightBorderThickness,
                ClientHighligtColor = profile.ClientHighlightColor,
                ShowClientHighlightBorder = profile.ShowClientHighlightBorder ? 1 : 0,
                ThumbnailTextFont = profile.ThumbnailTextFont,
                ThumbnailTextSize = int.TryParse(profile.ThumbnailTextSize, out var sz) ? sz : 12,
                ThumbnailTextColor = profile.ThumbnailTextColor,
                ShowThumbnailTextOverlay = profile.ShowThumbnailTextOverlay ? 1 : 0,
                ThumbnailTextMargins = profile.ThumbnailTextMargins,
                InactiveClientBorderthickness = profile.InactiveClientBorderThickness,
                InactiveClientBorderColor = profile.InactiveClientBorderColor,
                NotLoggedInIndicator = profile.NotLoggedInIndicator,
                NotLoggedInColor = profile.NotLoggedInColor,
            };

            // Client Settings sub-object
            ap.ClientSettings = new AhkClientSettings
            {
                MinimizeInactiveClients = profile.MinimizeInactiveClients ? 1 : 0,
                AlwaysMaximize = profile.AlwaysMaximize ? 1 : 0,
                TrackClientPossitions = profile.TrackClientPositions ? 1 : 0,
                DontMinimizeClients = profile.DontMinimizeClients,
            };

            // Custom Colors sub-object
            ap.CustomColors = new AhkCustomColors
            {
                cColorActive = profile.CustomColorsActive ? "1" : "0",
                cColors = ConvertCustomColorsToParallel(profile.CustomColors),
            };

            root.Profiles[name] = ap;
        }

        return root;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static Dictionary<string, bool> ConvertIntDictToBoolDict(Dictionary<string, int>? dict)
    {
        if (dict == null) return new();
        var result = new Dictionary<string, bool>();
        foreach (var (k, v) in dict)
            result[k] = v != 0;
        return result;
    }

    private static Dictionary<string, int> ConvertBoolDictToIntDict(Dictionary<string, bool>? dict)
    {
        if (dict == null) return new();
        var result = new Dictionary<string, int>();
        foreach (var (k, v) in dict)
            result[k] = v ? 1 : 0;
        return result;
    }

    private static Dictionary<string, HotkeyBinding> ConvertHotkeyArray(List<Dictionary<string, string>>? arr)
    {
        var result = new Dictionary<string, HotkeyBinding>();
        if (arr == null) return result;
        foreach (var entry in arr)
        {
            foreach (var (charName, hotkey) in entry)
                result[charName] = new HotkeyBinding { Key = hotkey };
        }
        return result;
    }

    private static List<Dictionary<string, string>> ConvertHotkeyDictToArray(Dictionary<string, HotkeyBinding>? dict)
    {
        var result = new List<Dictionary<string, string>>();
        if (dict == null) return result;
        foreach (var (charName, binding) in dict)
            result.Add(new Dictionary<string, string> { [charName] = binding.Key });
        return result;
    }

    private static Dictionary<string, CharacterStatSettings> ConvertStatOverlayConfig(
        Dictionary<string, Dictionary<string, int>>? config)
    {
        var result = new Dictionary<string, CharacterStatSettings>();
        if (config == null) return result;
        foreach (var (charName, stats) in config)
        {
            result[charName] = new CharacterStatSettings
            {
                Dps = stats.TryGetValue("dps", out var d) && d != 0,
                Logi = stats.TryGetValue("logi", out var l) && l != 0,
                Mining = stats.TryGetValue("mining", out var m) && m != 0,
                Ratting = stats.TryGetValue("ratting", out var r) && r != 0,
            };
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, int>> ConvertStatOverlayToAhk(
        Dictionary<string, CharacterStatSettings>? config)
    {
        var result = new Dictionary<string, Dictionary<string, int>>();
        if (config == null) return result;
        foreach (var (charName, stats) in config)
        {
            result[charName] = new Dictionary<string, int>
            {
                ["dps"] = stats.Dps ? 1 : 0,
                ["logi"] = stats.Logi ? 1 : 0,
                ["mining"] = stats.Mining ? 1 : 0,
                ["ratting"] = stats.Ratting ? 1 : 0,
            };
        }
        return result;
    }

    private static Dictionary<string, CustomColorEntry> ConvertParallelArrayColors(AhkCColors? cColors)
    {
        var result = new Dictionary<string, CustomColorEntry>();
        if (cColors == null) return result;
        var names = cColors.CharNames ?? new();
        var borders = cColors.Bordercolor ?? new();
        var texts = cColors.TextColor ?? new();
        var iaBorders = cColors.IABordercolor ?? new();

        for (int i = 0; i < names.Count; i++)
        {
            result[names[i]] = new CustomColorEntry
            {
                Border = i < borders.Count ? borders[i] : "FFFFFF",
                Text = i < texts.Count ? texts[i] : "FFFFFF",
                InactiveBorder = i < iaBorders.Count ? iaBorders[i] : "FFFFFF",
            };
        }
        return result;
    }

    private static AhkCColors ConvertCustomColorsToParallel(Dictionary<string, CustomColorEntry>? dict)
    {
        var cc = new AhkCColors();
        if (dict == null) return cc;
        foreach (var (name, entry) in dict)
        {
            cc.CharNames.Add(name);
            cc.Bordercolor.Add(entry.Border ?? "FFFFFF");
            cc.TextColor.Add(entry.Text ?? "FFFFFF");
            cc.IABordercolor.Add(entry.InactiveBorder ?? "FFFFFF");
        }
        return cc;
    }
}

// ── AHK Profile ────────────────────────────────────────────────────

public class AhkProfile
{
    [JsonPropertyName("Thumbnail Positions")]
    public Dictionary<string, ThumbnailRect>? ThumbnailPositions { get; set; }

    [JsonPropertyName("Client Possitions")]
    public Dictionary<string, ClientPosition>? ClientPossitions { get; set; }

    [JsonPropertyName("Hotkeys")]
    public List<Dictionary<string, string>>? Hotkeys { get; set; }

    [JsonPropertyName("Hotkey Groups")]
    public Dictionary<string, HotkeyGroup>? HotkeyGroups { get; set; }

    [JsonPropertyName("Secondary Thumbnails")]
    public Dictionary<string, SecondaryThumbnailSettings>? SecondaryThumbnails { get; set; }

    [JsonPropertyName("Thumbnail Settings")]
    public AhkThumbnailSettings? ThumbnailSettings { get; set; }

    [JsonPropertyName("Client Settings")]
    public AhkClientSettings? ClientSettings { get; set; }

    [JsonPropertyName("Custom Colors")]
    public AhkCustomColors? CustomColors { get; set; }

    [JsonPropertyName("Thumbnail Visibility")]
    public Dictionary<string, int>? ThumbnailVisibility { get; set; }

    [JsonPropertyName("Groups")]
    public Dictionary<string, ThumbnailGroup>? Groups { get; set; }
}

// ── AHK Thumbnail Settings (per-profile sub-object) ────────────────

public class AhkThumbnailSettings
{
    [JsonPropertyName("ShowAllColoredBorders")]
    public int ShowAllColoredBorders { get; set; }

    [JsonPropertyName("HideThumbnailsOnLostFocus")]
    public int HideThumbnailsOnLostFocus { get; set; }

    [JsonPropertyName("ShowThumbnailsAlwaysOnTop")]
    public int ShowThumbnailsAlwaysOnTop { get; set; } = 1;

    [JsonPropertyName("ThumbnailOpacity")]
    public int ThumbnailOpacity { get; set; } = 80;

    [JsonPropertyName("ClientHighligtBorderthickness")]
    public int ClientHighligtBorderthickness { get; set; } = 4;

    [JsonPropertyName("ClientHighligtColor")]
    public string? ClientHighligtColor { get; set; } = "#E36A0D";

    [JsonPropertyName("ShowClientHighlightBorder")]
    public int ShowClientHighlightBorder { get; set; } = 1;

    [JsonPropertyName("ThumbnailTextFont")]
    public string? ThumbnailTextFont { get; set; } = "Gill Sans MT";

    [JsonPropertyName("ThumbnailTextSize")]
    public int ThumbnailTextSize { get; set; } = 12;

    [JsonPropertyName("ThumbnailTextColor")]
    public string? ThumbnailTextColor { get; set; } = "#FAC57A";

    [JsonPropertyName("ShowThumbnailTextOverlay")]
    public int ShowThumbnailTextOverlay { get; set; } = 1;

    [JsonPropertyName("ThumbnailTextMargins")]
    public ThumbnailMargins? ThumbnailTextMargins { get; set; }

    [JsonPropertyName("InactiveClientBorderthickness")]
    public int InactiveClientBorderthickness { get; set; } = 2;

    [JsonPropertyName("InactiveClientBorderColor")]
    public string? InactiveClientBorderColor { get; set; } = "#8A8A8A";

    [JsonPropertyName("NotLoggedInIndicator")]
    public string? NotLoggedInIndicator { get; set; } = "text";

    [JsonPropertyName("NotLoggedInColor")]
    public string? NotLoggedInColor { get; set; } = "#555555";
}

// ── AHK Client Settings (per-profile sub-object) ──────────────────

public class AhkClientSettings
{
    [JsonPropertyName("MinimizeInactiveClients")]
    public int MinimizeInactiveClients { get; set; }

    [JsonPropertyName("AlwaysMaximize")]
    public int AlwaysMaximize { get; set; }

    [JsonPropertyName("TrackClientPossitions")]
    public int TrackClientPossitions { get; set; }

    [JsonPropertyName("Dont_Minimize_Clients")]
    public List<string> DontMinimizeClients { get; set; } = new();
}

// ── AHK Custom Colors (per-profile sub-object) ────────────────────

public class AhkCustomColors
{
    [JsonPropertyName("cColorActive")]
    public string cColorActive { get; set; } = "0";

    [JsonPropertyName("cColors")]
    public AhkCColors? cColors { get; set; }
}

public class AhkCColors
{
    [JsonPropertyName("CharNames")]
    public List<string> CharNames { get; set; } = new();

    [JsonPropertyName("Bordercolor")]
    public List<string> Bordercolor { get; set; } = new();

    [JsonPropertyName("TextColor")]
    public List<string> TextColor { get; set; } = new();

    [JsonPropertyName("IABordercolor")]
    public List<string> IABordercolor { get; set; } = new();
}

// ── AHK Global Settings ───────────────────────────────────────────

public class AhkGlobalSettings
{
    [JsonPropertyName("ThumbnailStartLocation")]
    public ThumbnailRect? ThumbnailStartLocation { get; set; }

    [JsonPropertyName("ThumbnailMinimumSize")]
    public ThumbnailSize? ThumbnailMinimumSize { get; set; }

    [JsonPropertyName("ThumbnailSnap")]
    public int ThumbnailSnap { get; set; } = 1;

    [JsonPropertyName("ThumbnailSnap_Distance")]
    public int ThumbnailSnapDistance { get; set; } = 20;

    [JsonPropertyName("ThumbnailBackgroundColor")]
    public string? ThumbnailBackgroundColor { get; set; } = "#57504E";

    [JsonPropertyName("Global_Hotkeys")]
    public int GlobalHotkeys { get; set; } = 1;

    [JsonPropertyName("Suspend_Hotkeys_Hotkey")]
    public string? SuspendHotkeysHotkey { get; set; } = "";

    [JsonPropertyName("ClickThroughHotkey")]
    public string? ClickThroughHotkey { get; set; } = "";

    [JsonPropertyName("HideShowThumbnailsHotkey")]
    public string? HideShowThumbnailsHotkey { get; set; } = "";

    [JsonPropertyName("HidePrimaryHotkey")]
    public string? HidePrimaryHotkey { get; set; } = "";

    [JsonPropertyName("HideSecondaryHotkey")]
    public string? HideSecondaryHotkey { get; set; } = "";

    [JsonPropertyName("ProfileCycleForwardHotkey")]
    public string? ProfileCycleForwardHotkey { get; set; } = "";

    [JsonPropertyName("ProfileCycleBackwardHotkey")]
    public string? ProfileCycleBackwardHotkey { get; set; } = "";

    [JsonPropertyName("ShowSessionTimer")]
    public int ShowSessionTimer { get; set; }

    [JsonPropertyName("ShowSystemName")]
    public int ShowSystemName { get; set; } = 1;

    [JsonPropertyName("PreferredMonitor")]
    public int PreferredMonitor { get; set; } = 1;

    [JsonPropertyName("LockPositions")]
    public int LockPositions { get; set; }

    [JsonPropertyName("HideActiveThumbnail")]
    public int HideActiveThumbnail { get; set; }

    [JsonPropertyName("IndividualThumbnailResize")]
    public int IndividualThumbnailResize { get; set; }

    [JsonPropertyName("SimpleMode")]
    public int SimpleMode { get; set; }

    [JsonPropertyName("SetupCompleted")]
    public int SetupCompleted { get; set; }

    [JsonPropertyName("LastUsedProfile")]
    public string? LastUsedProfile { get; set; } = "Default";

    [JsonPropertyName("EnableKeyBlockGuard")]
    public int EnableKeyBlockGuard { get; set; }

    [JsonPropertyName("EnableAttackAlerts")]
    public int EnableAttackAlerts { get; set; }

    [JsonPropertyName("PVEMode")]
    public int PVEMode { get; set; }

    [JsonPropertyName("EnableAlertSounds")]
    public int EnableAlertSounds { get; set; }

    [JsonPropertyName("AlertSoundVolume")]
    public int AlertSoundVolume { get; set; } = 100;

    [JsonPropertyName("AlertHubEnabled")]
    public int AlertHubEnabled { get; set; } = 1;

    [JsonPropertyName("AlertHubX")]
    public int AlertHubX { get; set; }

    [JsonPropertyName("AlertHubY")]
    public int AlertHubY { get; set; }

    [JsonPropertyName("AlertToastDirection")]
    public int AlertToastDirection { get; set; } = 5;

    [JsonPropertyName("AlertToastDuration")]
    public int AlertToastDuration { get; set; } = 6;

    [JsonPropertyName("AlertColors")]
    public Dictionary<string, string>? AlertColors { get; set; }

    [JsonPropertyName("AlertSounds")]
    public Dictionary<string, string>? AlertSounds { get; set; }

    [JsonPropertyName("SoundCooldowns")]
    public Dictionary<string, int>? SoundCooldowns { get; set; }

    [JsonPropertyName("SeverityColors")]
    public Dictionary<string, string>? SeverityColors { get; set; }

    [JsonPropertyName("SeverityCooldowns")]
    public Dictionary<string, int>? SeverityCooldowns { get; set; }

    [JsonPropertyName("SeverityFlashRates")]
    public Dictionary<string, int>? SeverityFlashRates { get; set; }

    [JsonPropertyName("SeverityTrayNotify")]
    public Dictionary<string, int>? SeverityTrayNotify { get; set; }

    [JsonPropertyName("EnabledAlertTypes")]
    public Dictionary<string, int>? EnabledAlertTypes { get; set; }

    [JsonPropertyName("EnableChatLogMonitoring")]
    public int EnableChatLogMonitoring { get; set; } = 1;

    [JsonPropertyName("EnableGameLogMonitoring")]
    public int EnableGameLogMonitoring { get; set; } = 1;

    [JsonPropertyName("ChatLogDirectory")]
    public string? ChatLogDirectory { get; set; } = "";

    [JsonPropertyName("GameLogDirectory")]
    public string? GameLogDirectory { get; set; } = "";

    [JsonPropertyName("StatOverlayConfig")]
    public Dictionary<string, Dictionary<string, int>>? StatOverlayConfig { get; set; }

    [JsonPropertyName("StatOverlayFontSize")]
    public int StatOverlayFontSize { get; set; } = 8;

    [JsonPropertyName("StatOverlayOpacity")]
    public int StatOverlayOpacity { get; set; } = 200;

    [JsonPropertyName("StatLogEnabled")]
    public int StatLogEnabled { get; set; }

    [JsonPropertyName("StatLogPath")]
    public string? StatLogPath { get; set; } = "";

    [JsonPropertyName("StatLogRetentionDays")]
    public int StatLogRetentionDays { get; set; } = 30;

    [JsonPropertyName("StatWindowPositions")]
    public Dictionary<string, ThumbnailRect>? StatWindowPositions { get; set; }

    [JsonPropertyName("RTSS_Enabled")]
    public int RTSS_Enabled { get; set; }

    [JsonPropertyName("RTSS_IdleFPS")]
    public int RTSS_IdleFPS { get; set; } = 15;

    [JsonPropertyName("CharSelect_CyclingEnabled")]
    public int CharSelectCyclingEnabled { get; set; }

    [JsonPropertyName("CharSelect_ForwardHotkey")]
    public string? CharSelectForwardHotkey { get; set; } = "";

    [JsonPropertyName("CharSelect_BackwardHotkey")]
    public string? CharSelectBackwardHotkey { get; set; } = "";

    [JsonPropertyName("SettingsWindowWidth")]
    public int SettingsWindowWidth { get; set; } = 1080;

    [JsonPropertyName("SettingsWindowHeight")]
    public int SettingsWindowHeight { get; set; } = 1080;

    [JsonPropertyName("EveManagerUseESI")]
    public int EveManagerUseESI { get; set; } = 1;

    [JsonPropertyName("EveBackupDir")]
    public string? EveBackupDir { get; set; } = "";

    [JsonPropertyName("EveSettingsDir")]
    public string? EveSettingsDir { get; set; } = "";

    [JsonPropertyName("ThumbnailGroups")]
    public List<ThumbnailGroup>? ThumbnailGroups { get; set; }

    [JsonPropertyName("Minimize_Delay")]
    public int MinimizeDelay { get; set; } = 100;

    [JsonPropertyName("ShowProcessStats")]
    public int ShowProcessStats { get; set; }

    [JsonPropertyName("ProcessStatsTextSize")]
    public int ProcessStatsTextSize { get; set; } = 9;

    [JsonPropertyName("QuickSwitchHotkey")]
    public string? QuickSwitchHotkey { get; set; } = "";

    [JsonPropertyName("ThumbnailAnnotations")]
    public Dictionary<string, string>? ThumbnailAnnotations { get; set; } = new();
}

// ── AHK EveManager ────────────────────────────────────────────────

public class AhkEveManager
{
    [JsonPropertyName("CharNameCache")]
    public Dictionary<string, AhkCharCacheEntry>? CharNameCache { get; set; }
}

public class AhkCharCacheEntry
{
    [JsonPropertyName("fetched")]
    public string? Fetched { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
