using System;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>What the window offers to cards, actions and editors. SettingsShell implements it;
/// tests use a fake.</summary>
public interface ISettingsHost
{
    void Navigate(string pageId, string? itemId = null);
    /// <summary>A short message in the window's corner. Never a separate window, never takes focus.</summary>
    void Toast(string text);
    bool Confirm(string title, string body);
    string? PickFolder(string? initial);
    string? PickFile(string? initial, string filter);
    string? PickSaveFile(string defaultName, string filter);
    string? PickColor(string? initial);
    /// <summary>Opens the hotkey capture dialog. Returns the new AHK hotkey string, "" to clear, null if cancelled.</summary>
    string? CaptureHotkey(string settingId, string? current);
    /// <summary>Re-read every card from settings (after an action changed many values, a language change, an import).</summary>
    void Reload();
}

/// <summary>Live app services the window may call. Every member may be null (tests, or a
/// service that failed to start), so callers use ?.</summary>
public sealed class AppServices
{
    public ThumbnailManager? Thumbnails { get; init; }
    public CropManager? Crops { get; init; }
    public HotkeyService? Hotkeys { get; init; }
}

public sealed class SettingsContext
{
    public SettingsContext(SettingsService service, SettingsApplier applier, AppServices services, ISettingsHost host)
    {
        Service = service;
        Applier = applier;
        Services = services;
        Host = host;
    }

    public SettingsService Service { get; }
    /// <summary>Always the live object: SettingsService.ReplaceSettings swaps it on import.</summary>
    public AppSettings S => Service.Settings;
    public SettingsApplier Applier { get; }
    public AppServices Services { get; }
    public ISettingsHost Host { get; }

    /// <summary>Localized string for code-built text; falls back to the key itself so a missing
    /// key is visible in testing rather than blank.</summary>
    public static string Str(string key) => LocalizationService.Str(key, key);
}
