using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Models;

/// <summary>
/// Root settings model — compatible with the AHK JSON format.
/// Maps to "EVE MultiPreview.json".
/// Property names match AHK Propertys.ahk exactly for cross-format compatibility.
/// </summary>
public class AppSettings
{
    // ── Layout ──────────────────────────────────────────────────────
    public ThumbnailRect ThumbnailStartLocation { get; set; } = new() { X = 20, Y = 20, Width = 280, Height = 180 };
    public ThumbnailSize ThumbnailMinimumSize { get; set; } = new() { Width = 50, Height = 50 };
    public bool ThumbnailSnap { get; set; } = true;
    public int ThumbnailSnapDistance { get; set; } = 20;
    public bool IndividualThumbnailResize { get; set; } = false;
    public bool LockPositions { get; set; } = false;
    public int PreferredMonitor { get; set; } = 1;
    public string ThumbnailBackgroundColor { get; set; } = "#57504E";
    public bool ColorBlindMode { get; set; } = false;

    // ── Behavior ────────────────────────────────────────────────────
    public bool HideActiveThumbnail { get; set; } = false;
    public int MinimizeDelay { get; set; } = 100;
    public bool EnableKeyBlockGuard { get; set; } = false;
    public bool GlobalHotkeys { get; set; } = true;
    public string SuspendHotkey { get; set; } = "";
    public string ClickThroughHotkey { get; set; } = "";
    public string HideShowThumbnailsHotkey { get; set; } = "";
    public string HidePrimaryHotkey { get; set; } = "";
    public string HideSecondaryHotkey { get; set; } = "";
    public string ProfileCycleForwardHotkey { get; set; } = "";
    public string ProfileCycleBackwardHotkey { get; set; } = "";
    public bool ShowSessionTimer { get; set; } = false;
    public bool ShowSystemName { get; set; } = true;
    public bool SimpleMode { get; set; } = false;
    public bool SetupCompleted { get; set; } = false;

    /// <summary>Controls whether the Settings window auto-opens when the app launches.</summary>
    public StartupSettingsMode StartupSettings { get; set; } = StartupSettingsMode.Off;

    // ── Debug Logging ───────────────────────────────────────────────
    public bool EnableDebugLogging_Injection { get; set; } = false;
    public bool EnableDebugLogging_Cycling { get; set; } = false;
    public bool EnableDebugLogging_WindowHooks { get; set; } = false;
    public bool EnableDebugLogging_DWM { get; set; } = false;

    // ── Alert Settings ──────────────────────────────────────────────

    public bool PveMode { get; set; } = false;
    public bool EnableAlertSounds { get; set; } = false;
    public int AlertSoundVolume { get; set; } = 100;
    public Dictionary<string, string> AlertColors { get; set; } = new();
    public Dictionary<string, string> AlertSounds { get; set; } = new();
    public Dictionary<string, int> SoundCooldowns { get; set; } = new();
    public Dictionary<string, bool> EnabledAlertTypes { get; set; } = new()
    {
        ["attack"] = true, ["warp_scramble"] = true, ["decloak"] = true,
        ["fleet_invite"] = true, ["convo_request"] = true, ["system_change"] = true,
        ["mine_cargo_full"] = false, ["mine_asteroid_depleted"] = false,
        ["mine_crystal_broken"] = false, ["mine_module_stopped"] = false
    };
    public Dictionary<string, string> SeverityColors { get; set; } = new()
    {
        ["critical"] = "#FF0000", ["warning"] = "#FFA500", ["info"] = "#4A9EFF"
    };
    public Dictionary<string, int> SeverityCooldowns { get; set; } = new()
    {
        ["critical"] = 5, ["warning"] = 15, ["info"] = 30
    };
    public Dictionary<string, int> SeverityFlashRates { get; set; } = new()
    {
        ["critical"] = 200, ["warning"] = 500, ["info"] = 1000
    };
    public Dictionary<string, bool> SeverityTrayNotify { get; set; } = new()
    {
        ["critical"] = true, ["warning"] = false, ["info"] = false
    };

    // ── Alert Hub ───────────────────────────────────────────────────
    public bool AlertHubEnabled { get; set; } = false;
    public int AlertHubX { get; set; } = 0;
    public int AlertHubY { get; set; } = 0;
    public int AlertToastDirection { get; set; } = 5;
    public int AlertToastDuration { get; set; } = 6;

    // ── Log Monitoring ──────────────────────────────────────────────
    public bool EnableChatLogMonitoring { get; set; } = true;
    public bool EnableGameLogMonitoring { get; set; } = true;
    public string ChatLogDirectory { get; set; } = "";
    public string GameLogDirectory { get; set; } = "";

    // ── Stat Overlay ────────────────────────────────────────────────
    public int StatOverlayFontSize { get; set; } = 8;
    public int StatOverlayOpacity { get; set; } = 200;
    public string StatOverlayBgColor { get; set; } = "#1a1a2e";
    public string StatOverlayTextColor { get; set; } = "#00FF88";
    public bool StatLoggingEnabled { get; set; } = false;
    public string StatLogDirectory { get; set; } = "";
    public int StatLogRetentionDays { get; set; } = 30;
    public Dictionary<string, CharacterStatSettings> PerCharacterStats { get; set; } = new();
    public Dictionary<string, ThumbnailRect> StatWindowPositions { get; set; } = new();

    /// <summary>Global default metric set — bits that are visible for every character
    /// unless overridden. Effective per-character = (Global | ForcedOn) &amp; ~ForcedOff.</summary>
    public StatMetrics GlobalStatMetrics { get; set; } = StatMetrics.None;

    // ── Settings GUI ────────────────────────────────────────────────
    public int SettingsWindowWidth { get; set; } = 1080;
    public int SettingsWindowHeight { get; set; } = 1080;
    public int SettingsUiFontSize { get; set; } = 12;

    // ── RTSS / Misc ─────────────────────────────────────────────────
    public bool RtssEnabled { get; set; } = false;
    public int RtssFpsLimit { get; set; } = 15;
    public bool ShowRtssFps { get; set; } = false;
    public bool ReceivePreReleaseUpdates { get; set; } = false;

    // ── Under Fire Indicator ────────────────────────────────────────
    public bool EnableUnderFireIndicator { get; set; } = true;
    public int UnderFireTimeoutSeconds { get; set; } = 5;

    // ── Process Monitor ─────────────────────────────────────────────
    public bool ShowProcessStats { get; set; } = false;
    public string ProcessStatsTextSize { get; set; } = "9";

    // ── Quick-Switch Wheel ──────────────────────────────────────────
    public string QuickSwitchHotkey { get; set; } = "";
    public List<string> QuickSwitchCardOrder { get; set; } = new();

    // ── Character Select Cycling ────────────────────────────────────
    public bool CharSelectCyclingEnabled { get; set; } = false;
    public string CharSelectForwardHotkey { get; set; } = "";
    public string CharSelectBackwardHotkey { get; set; } = "";

    // ── Thumbnail Groups (global) ───────────────────────────────────
    public List<ThumbnailGroup> ThumbnailGroups { get; set; } = new();

    // ── Thumbnail Annotations ───────────────────────────────────────
    public Dictionary<string, string> ThumbnailAnnotations { get; set; } = new();

    // ── EVE Manager ─────────────────────────────────────────────────
    public bool EveManagerUseESI { get; set; } = true;
    public string EveBackupDir { get; set; } = "";
    public string EveSettingsDir { get; set; } = "";
    public AhkEveManager? EveManager { get; set; }

    // ── Profiles ────────────────────────────────────────────────────
    public string LastUsedProfile { get; set; } = "Default";
    public Dictionary<string, Profile> Profiles { get; set; } = new()
    {
        ["Default"] = new Profile()
    };

    // ═══════════════════════════════════════════════════════════════════
    // Convenience accessors — proxy to the active profile so consuming
    // code can use s.PropertyName without knowing about profile nesting.
    // ═══════════════════════════════════════════════════════════════════

    [JsonIgnore]
    private Profile _cp => Profiles.GetValueOrDefault(LastUsedProfile) ?? Profiles.Values.FirstOrDefault() ?? new Profile();

    // Per-profile Thumbnail Settings proxies
    [JsonIgnore] public bool ShowAllColoredBorders { get => _cp.ShowAllColoredBorders; set => _cp.ShowAllColoredBorders = value; }
    [JsonIgnore] public bool HideThumbnailsOnLostFocus { get => _cp.HideThumbnailsOnLostFocus; set => _cp.HideThumbnailsOnLostFocus = value; }
    [JsonIgnore] public bool ShowThumbnailsAlwaysOnTop { get => _cp.ShowThumbnailsAlwaysOnTop; set => _cp.ShowThumbnailsAlwaysOnTop = value; }
    [JsonIgnore] public int ThumbnailOpacity { get => _cp.ThumbnailOpacity; set => _cp.ThumbnailOpacity = value; }
    [JsonIgnore] public int ClientHighlightBorderThickness { get => _cp.ClientHighlightBorderThickness; set => _cp.ClientHighlightBorderThickness = value; }
    [JsonIgnore] public string ClientHighlightColor { get => _cp.ClientHighlightColor; set => _cp.ClientHighlightColor = value; }
    [JsonIgnore] public bool ShowClientHighlightBorder { get => _cp.ShowClientHighlightBorder; set => _cp.ShowClientHighlightBorder = value; }
    [JsonIgnore] public string ThumbnailTextFont { get => _cp.ThumbnailTextFont; set => _cp.ThumbnailTextFont = value; }
    [JsonIgnore] public string ThumbnailTextSize { get => _cp.ThumbnailTextSize; set => _cp.ThumbnailTextSize = value; }
    [JsonIgnore] public string ThumbnailTextColor { get => _cp.ThumbnailTextColor; set => _cp.ThumbnailTextColor = value; }
    [JsonIgnore] public bool ShowThumbnailTextOverlay { get => _cp.ShowThumbnailTextOverlay; set => _cp.ShowThumbnailTextOverlay = value; }
    [JsonIgnore] public ThumbnailMargins ThumbnailTextMargins { get => _cp.ThumbnailTextMargins; set => _cp.ThumbnailTextMargins = value; }
    [JsonIgnore] public int InactiveClientBorderThickness { get => _cp.InactiveClientBorderThickness; set => _cp.InactiveClientBorderThickness = value; }
    [JsonIgnore] public string InactiveClientBorderColor { get => _cp.InactiveClientBorderColor; set => _cp.InactiveClientBorderColor = value; }
    [JsonIgnore] public string NotLoggedInIndicator { get => _cp.NotLoggedInIndicator; set => _cp.NotLoggedInIndicator = value; }
    [JsonIgnore] public string NotLoggedInColor { get => _cp.NotLoggedInColor; set => _cp.NotLoggedInColor = value; }

    // Per-profile Client Settings proxies
    [JsonIgnore] public bool MinimizeInactiveClients { get => _cp.MinimizeInactiveClients; set => _cp.MinimizeInactiveClients = value; }
    [JsonIgnore] public bool AlwaysMaximize { get => _cp.AlwaysMaximize; set => _cp.AlwaysMaximize = value; }
    [JsonIgnore] public bool TrackClientPositions { get => _cp.TrackClientPositions; set => _cp.TrackClientPositions = value; }

    // Per-profile Custom Colors proxies
    [JsonIgnore] public bool CustomColorsActive { get => _cp.CustomColorsActive; set => _cp.CustomColorsActive = value; }
    [JsonIgnore] public Dictionary<string, CustomColorEntry> CustomColors { get => _cp.CustomColors; set => _cp.CustomColors = value; }

    // Per-profile Thumbnail Visibility proxy
    [JsonIgnore] public Dictionary<string, int> ThumbnailVisibility { get => _cp.ThumbnailVisibility; set => _cp.ThumbnailVisibility = value; }

    // Per-profile secondary thumbnails proxy
    [JsonIgnore] public Dictionary<string, SecondaryThumbnailSettings> SecondaryThumbnails { get => _cp.SecondaryThumbnails; set => _cp.SecondaryThumbnails = value; }

    // Per-profile performance settings proxies
    [JsonIgnore] public bool ManageAffinity { get => _cp.ManageAffinity; set => _cp.ManageAffinity = value; }
    [JsonIgnore] public bool AutoBalanceCores { get => _cp.AutoBalanceCores; set => _cp.AutoBalanceCores = value; }
    [JsonIgnore] public Dictionary<string, int> PerClientCores { get => _cp.PerClientCores; set => _cp.PerClientCores = value; }

    // Per-profile hotkey groups proxy
    [JsonIgnore] public Dictionary<string, HotkeyGroup> HotkeyGroups { get => _cp.HotkeyGroups; set => _cp.HotkeyGroups = value; }

    // Per-profile crop proxies
    [JsonIgnore] public bool CropEnabled { get => _cp.CropEnabled; set => _cp.CropEnabled = value; }
    [JsonIgnore] public Dictionary<string, List<CropDefinition>> Crops { get => _cp.Crops; set => _cp.Crops = value; }

    // Backwards-compat: SettingsWindowSize as an object (for code that uses SettingsWindowSize.Width/Height)
    [JsonIgnore] public WindowSize SettingsWindowSize => new() { Width = SettingsWindowWidth, Height = SettingsWindowHeight };

    // Properties that were removed but may still be referenced — keep as simple props
    public bool ResizeThumbnailsOnHover { get; set; } = false;
    public double HoverScale { get; set; } = 1.5;
    public int NotLoggedInDim { get; set; } = 80;
    public string AlertFlashColor { get; set; } = "0xff0000";
    public int AlertFlashRate { get; set; } = 500;
    public bool AlertFlashEnabled { get; set; } = true;
    public int AlertCooldown { get; set; } = 5;
    public bool AlertSoundEnabled { get; set; } = false;
    public string AlertSoundFile { get; set; } = "";
    public int AlertSoundCooldown { get; set; } = 10;
    public bool StatOverlayEnabled { get; set; } = false;
    public bool ShowDpsOverlay { get; set; } = false;
    public bool ShowLogiOverlay { get; set; } = false;
    public bool ShowMiningOverlay { get; set; } = false;
    public bool ShowRattingOverlay { get; set; } = false;
    public bool IncludeNpcDamage { get; set; } = false;

    // ── Color Parsing Utility ────────────────────────────────────────
    /// <summary>
    /// Parse color strings in multiple formats: rgb(r,g,b), #RRGGBB, 0xRRGGBB.
    /// Returns a WPF Color. Matches AHK Propertys.ahk convertToHex behavior.
    /// </summary>
    public static System.Windows.Media.Color ParseColor(string input, System.Windows.Media.Color fallback = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        input = input.Trim();

        // rgb(r,g,b) format
        var rgbMatch = Regex.Match(input, @"^rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", RegexOptions.IgnoreCase);
        if (rgbMatch.Success)
        {
            return System.Windows.Media.Color.FromRgb(
                byte.Parse(rgbMatch.Groups[1].Value),
                byte.Parse(rgbMatch.Groups[2].Value),
                byte.Parse(rgbMatch.Groups[3].Value));
        }

        // 0xRRGGBB format → convert to #RRGGBB
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            input = "#" + input[2..];

        // Ensure # prefix
        if (!input.StartsWith("#"))
            input = "#" + input;

        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(input);
        }
        catch
        {
            return fallback;
        }
    }
}

// ── Sub-Models ──────────────────────────────────────────────────────

/// <summary>Controls what happens to the Settings window when the app starts.
/// Persisted as an integer (0/1/2) for AHK compatibility.</summary>
public enum StartupSettingsMode
{
    /// <summary>App starts to the tray — Settings stays closed until the user opens it.</summary>
    Off = 0,
    /// <summary>App automatically opens the Settings window on launch.</summary>
    Open = 1,
    /// <summary>App automatically opens the Settings window, already minimized to the taskbar.</summary>
    OpenMinimized = 2,
}

public class ThumbnailRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class ThumbnailSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class ThumbnailMargins
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public class WindowSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class CustomColorEntry
{
    public string Char { get; set; } = "";
    public string Border { get; set; } = "FFFFFF";
    public string Text { get; set; } = "FFFFFF";
    public string InactiveBorder { get; set; } = "FFFFFF";
}

public class SecondaryThumbnailSettings
{
    [JsonPropertyName("enabled")]
    public int Enabled { get; set; } = 1;

    [JsonIgnore]
    public bool IsEnabled { get => Enabled != 0; set => Enabled = value ? 1 : 0; }

    [JsonPropertyName("opacity")]
    public int Opacity { get; set; } = 180;

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; } = 200;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 120;
}

/// <summary>
/// A named profile containing per-character positions, per-profile settings, and hotkey bindings.
/// In AHK JSON, these are under _Profiles.{name} with nested sub-objects.
/// After adapter conversion, per-profile settings are flattened here.
/// </summary>
public class Profile
{
    // ── Per-character data ──────────────────────────────────────────
    public Dictionary<string, ThumbnailRect> ThumbnailPositions { get; set; } = new();
    public Dictionary<string, ClientPosition> ClientPositions { get; set; } = new();
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();
    public Dictionary<string, HotkeyGroup> HotkeyGroups { get; set; } = new();
    public Dictionary<string, SecondaryThumbnailSettings> SecondaryThumbnails { get; set; } = new();
    public Dictionary<string, ThumbnailGroup> Groups { get; set; } = new();
    public Dictionary<string, int> ThumbnailVisibility { get; set; } = new();
    public List<string> DontMinimizeClients { get; set; } = new();

    // ── Per-profile Thumbnail Settings (from AHK "Thumbnail Settings" sub-object) ──
    public bool ShowAllColoredBorders { get; set; } = false;
    public bool HideThumbnailsOnLostFocus { get; set; } = false;
    public bool ShowThumbnailsAlwaysOnTop { get; set; } = true;
    public int ThumbnailOpacity { get; set; } = 80;
    public int ClientHighlightBorderThickness { get; set; } = 4;
    public string ClientHighlightColor { get; set; } = "#E36A0D";
    public bool ShowClientHighlightBorder { get; set; } = true;
    public string ThumbnailTextFont { get; set; } = "Gill Sans MT";
    public string ThumbnailTextSize { get; set; } = "12";
    public string ThumbnailTextColor { get; set; } = "#FAC57A";
    public bool ShowThumbnailTextOverlay { get; set; } = true;
    public ThumbnailMargins ThumbnailTextMargins { get; set; } = new() { X = 5, Y = 5 };
    public int InactiveClientBorderThickness { get; set; } = 2;
    public string InactiveClientBorderColor { get; set; } = "#8A8A8A";
    public string NotLoggedInIndicator { get; set; } = "text";
    public string NotLoggedInColor { get; set; } = "#555555";

    // ── Per-profile Client Settings (from AHK "Client Settings" sub-object) ──
    public bool MinimizeInactiveClients { get; set; } = false;
    public bool AlwaysMaximize { get; set; } = false;
    public bool TrackClientPositions { get; set; } = false;

    // ── Per-profile Stat Overlay (per-character toggle for which stat types to show) ──
    public Dictionary<string, StatOverlayCharacterConfig> StatOverlayCharacters { get; set; } = new();

    // ── Per-profile Custom Colors (from AHK "Custom Colors" sub-object) ──
    public bool CustomColorsActive { get; set; } = false;
    public Dictionary<string, CustomColorEntry> CustomColors { get; set; } = new();

    // ── Per-profile Performance Settings ──
    public bool ManageAffinity { get; set; } = false;
    public bool AutoBalanceCores { get; set; } = true;
    public Dictionary<string, int> PerClientCores { get; set; } = new();

    // ── Per-profile Crops (multiple named crops per character) ──
    public bool CropEnabled { get; set; } = false;
    public Dictionary<string, List<CropDefinition>> Crops { get; set; } = new();
}

/// <summary>
/// A single cropped DWM thumbnail popup definition.
/// Source rect = region of the EVE client window to capture (rcSource).
/// Popup rect = on-screen position and size of the popup window displaying that region.
/// </summary>
public class CropDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Crop";

    [JsonPropertyName("sourceX")]
    public int SourceX { get; set; } = 0;

    [JsonPropertyName("sourceY")]
    public int SourceY { get; set; } = 0;

    [JsonPropertyName("sourceWidth")]
    public int SourceWidth { get; set; } = 400;

    [JsonPropertyName("sourceHeight")]
    public int SourceHeight { get; set; } = 300;

    [JsonPropertyName("popupX")]
    public int PopupX { get; set; } = 100;

    [JsonPropertyName("popupY")]
    public int PopupY { get; set; } = 100;

    [JsonPropertyName("popupWidth")]
    public int PopupWidth { get; set; } = 320;

    [JsonPropertyName("popupHeight")]
    public int PopupHeight { get; set; } = 240;

    [JsonPropertyName("showLabel")]
    public bool ShowLabel { get; set; } = true;
}

/// <summary>Per-character stat overlay configuration.</summary>
public class StatOverlayCharacterConfig
{
    public bool Dps { get; set; }
    public bool Logi { get; set; }
    public bool Mining { get; set; }
    public bool Ratting { get; set; }
    public bool Npc { get; set; }
}

public class ClientPosition
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("IsMaximized")]
    public double IsMaximized { get; set; }
}

public class HotkeyBinding
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("modifiers")]
    public string Modifiers { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "switch";
}

public class ThumbnailGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#4fc3f7";

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = new();
}

public class HotkeyGroup
{
    [JsonPropertyName("ForwardsHotkey")]
    public string ForwardsHotkey { get; set; } = "";

    [JsonPropertyName("BackwardsHotkey")]
    public string BackwardsHotkey { get; set; } = "";

    [JsonPropertyName("Characters")]
    public List<string> Characters { get; set; } = new();
}

/// <summary>
/// Custom converter that reads both AHK array format [{"CharName": "F1"}]
/// and C# dictionary format {"CharName": {"key": "F1"}}.
/// Always writes in C# dictionary format.
/// </summary>
public class HotkeyDictionaryConverter : JsonConverter<Dictionary<string, HotkeyBinding>>
{
    public override Dictionary<string, HotkeyBinding> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, HotkeyBinding>();

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // AHK format: [{"CharName": "F1"}, {"CharName2": "F2"}]
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            string charName = reader.GetString()!;
                            reader.Read();
                            string hotkey = reader.GetString() ?? "";
                            result[charName] = new HotkeyBinding { Key = hotkey };
                        }
                    }
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // C# format: {"CharName": {"key": "F1", ...}}
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string charName = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        // Simple string value
                        result[charName] = new HotkeyBinding { Key = reader.GetString() ?? "" };
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var binding = JsonSerializer.Deserialize<HotkeyBinding>(ref reader, options)
                                      ?? new HotkeyBinding();
                        result[charName] = binding;
                    }
                }
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, HotkeyBinding> value, JsonSerializerOptions options)
    {
        // Write in AHK-compatible array format for backwards compatibility
        writer.WriteStartArray();
        foreach (var kv in value)
        {
            writer.WriteStartObject();
            writer.WriteString(kv.Key, kv.Value.Key);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Per-metric visibility flags for the stat overlay. One bit per metric (plus
/// <see cref="IncludeNpc"/>). Resolution for a character:
/// <c>effective = (GlobalStatMetrics | ForcedOn) &amp; ~ForcedOff</c>.
/// </summary>
[Flags]
public enum StatMetrics : uint
{
    None = 0,

    // DPS
    DpsOut = 1u << 0, // Out = Dmg/s Out
    DpsIn  = 1u << 1, // In = Dmg/s In
    Tdi    = 1u << 2, // TDI = Total Dmg In
    Tdo    = 1u << 3, // TDO = Total Dmg Out

    // Logi
    Arps = 1u << 4, // ARPS = Armor Rep/s
    Srps = 1u << 5, // SRPS = Shield Rep/s
    Ctps = 1u << 6, // CTPS = Cap Transf/s
    Taro = 1u << 7, // TARO = Total Armor Rep Out
    Tari = 1u << 8, // TARI = Total Armor Rep In
    Tsro = 1u << 9, // TSRO = Total Shield Rep Out
    Tsri = 1u << 10, // TSRI = Total Shield Rep In

    // Mining
    Ompc = 1u << 11, // OMPC = Ore Mined/Cycle
    Omph = 1u << 12, // OMPH = Ore Mined/Hour
    Gmpc = 1u << 13, // GMPC = Gas Mined/Cycle
    Gmph = 1u << 14, // GMPH = Gas Mined/Hour
    Imph = 1u << 15, // IMPH = Ice Mined/Hour

    // Ratting
    Tipt = 1u << 16, // TIPT = Total ISK/Tick
    Tiph = 1u << 17, // TIPH = Total ISK/Hour
    Tips = 1u << 18, // TIPS = Total ISK/Session

    // Meta — affects what counts as DPS, not a metric itself
    IncludeNpc = 1u << 19,

    // Category masks (not stored on their own, used by the UI for group operations)
    DpsMask  = DpsOut | DpsIn | Tdi | Tdo,
    LogiMask = Arps | Srps | Ctps | Taro | Tari | Tsro | Tsri,
    MineMask = Ompc | Omph | Gmpc | Gmph | Imph,
    RatMask  = Tipt | Tiph | Tips,
    AllMetrics = DpsMask | LogiMask | MineMask | RatMask,
}

/// <summary>
/// Per-character override masks for the stat overlay. Each bit is tri-state:
///   forced on  → set in <see cref="ForcedOn"/>, always visible for this character
///   forced off → set in <see cref="ForcedOff"/>, always hidden for this character
///   inherit    → absent from both masks, follows <see cref="AppSettings.GlobalStatMetrics"/>
///
/// A bit should never be set in both masks; <see cref="Resolve"/> treats ForcedOff as stronger.
/// </summary>
public class CharacterStatSettings
{
    [JsonPropertyName("forcedOn")]
    public StatMetrics ForcedOn { get; set; } = StatMetrics.None;

    [JsonPropertyName("forcedOff")]
    public StatMetrics ForcedOff { get; set; } = StatMetrics.None;

    public static StatMetrics Resolve(StatMetrics global, CharacterStatSettings? overrides)
    {
        if (overrides == null) return global;
        return (global | overrides.ForcedOn) & ~overrides.ForcedOff;
    }

    /// <summary>Tri-state view for a specific bit — null = inherit, true = ForcedOn, false = ForcedOff.</summary>
    public bool? GetOverrideState(StatMetrics bit)
    {
        if ((ForcedOff & bit) != 0) return false;
        if ((ForcedOn & bit) != 0) return true;
        return null;
    }

    /// <summary>Set a tri-state override for the given bit.</summary>
    public void SetOverrideState(StatMetrics bit, bool? state)
    {
        ForcedOn &= ~bit;
        ForcedOff &= ~bit;
        if (state == true) ForcedOn |= bit;
        else if (state == false) ForcedOff |= bit;
    }

    public int OverrideCount => System.Numerics.BitOperations.PopCount((uint)(ForcedOn | ForcedOff));
}

/// <summary>Static metadata for a single stat metric — code, category, human label.</summary>
public readonly record struct StatMetricDef(StatMetrics Bit, string Code, string Category, string Label);

public static class StatMetricCatalog
{
    public static readonly StatMetricDef[] All =
    {
        new(StatMetrics.DpsOut, "Out",  "DPS",  "Dmg/s Out"),
        new(StatMetrics.DpsIn,  "In",   "DPS",  "Dmg/s In"),
        new(StatMetrics.Tdi,    "TDI",  "DPS",  "Total Dmg In"),
        new(StatMetrics.Tdo,    "TDO",  "DPS",  "Total Dmg Out"),

        new(StatMetrics.Arps, "ARPS", "Logi", "Armor Rep/s"),
        new(StatMetrics.Srps, "SRPS", "Logi", "Shield Rep/s"),
        new(StatMetrics.Ctps, "CTPS", "Logi", "Cap Transf/s"),
        new(StatMetrics.Taro, "TARO", "Logi", "Total Armor Rep Out"),
        new(StatMetrics.Tari, "TARI", "Logi", "Total Armor Rep In"),
        new(StatMetrics.Tsro, "TSRO", "Logi", "Total Shield Rep Out"),
        new(StatMetrics.Tsri, "TSRI", "Logi", "Total Shield Rep In"),

        new(StatMetrics.Ompc, "OMPC", "Mine", "Ore Mined/Cycle"),
        new(StatMetrics.Omph, "OMPH", "Mine", "Ore Mined/Hour"),
        new(StatMetrics.Gmpc, "GMPC", "Mine", "Gas Mined/Cycle"),
        new(StatMetrics.Gmph, "GMPH", "Mine", "Gas Mined/Hour"),
        new(StatMetrics.Imph, "IMPH", "Mine", "Ice Mined/Hour"),

        new(StatMetrics.Tipt, "TIPT", "Rat", "Total ISK/Tick"),
        new(StatMetrics.Tiph, "TIPH", "Rat", "Total ISK/Hour"),
        new(StatMetrics.Tips, "TIPS", "Rat", "Total ISK/Session"),
    };
}

