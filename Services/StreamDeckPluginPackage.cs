using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace EveMultiPreview.Services;

/// <summary>What the Stream Deck plugin button offers: nothing (this exe carries no plugin, as a build without it does),
/// Install, Update (the installed plugin is older than the one inside this exe) or Reinstall.</summary>
public enum PluginOffer { None, Install, Update, Reinstall }

/// <summary>The Stream Deck plugin travels inside the exe (one download, and every update brings the matching plugin).
/// The Stream Deck app installs a .streamDeckPlugin file that is opened, so installing is: write the packed plugin to a
/// temporary file and open it. Stream Deck then asks the player to confirm.</summary>
public static class StreamDeckPluginPackage
{
    public const string ResourceName = "StreamDeckPlugin";
    public const string Uuid = "com.cjkondur.evemultipreview";
    private const string FileName = "EVE.MultiPreview.streamDeckPlugin";

    /// <summary>The plugin's version inside this exe, or null when the exe carries no plugin or it cannot be read.</summary>
    public static Version? PackedVersion()
    {
        try
        {
            using var stream = typeof(StreamDeckPluginPackage).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(Uuid + ".sdPlugin/manifest.json");
            if (entry == null) return null;
            using var manifest = entry.Open();
            return VersionOf(manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException) { return null; }
    }

    /// <summary>The installed plugin's version, from Stream Deck's plugin folder; null when it is not installed or unreadable.</summary>
    public static Version? InstalledVersion()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Elgato", "StreamDeck", "Plugins", Uuid + ".sdPlugin", "manifest.json");
        try
        {
            using var manifest = File.OpenRead(path);
            return VersionOf(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The top-level "Version" of a plugin manifest, or null.</summary>
    public static Version? VersionOf(Stream manifest)
    {
        using var doc = JsonDocument.Parse(manifest);
        return doc.RootElement.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String
            && Version.TryParse(v.GetString(), out var version) ? version : null;
    }

    /// <summary>What to offer for a packed and an installed version.</summary>
    public static PluginOffer Offer(Version? packed, Version? installed) =>
        packed == null ? PluginOffer.None
        : installed == null ? PluginOffer.Install
        : installed < packed ? PluginOffer.Update
        : PluginOffer.Reinstall;

    /// <summary>Writes the packed plugin to the temp folder and opens it, which hands it to the Stream Deck app. False when
    /// there is no packed plugin, it could not be written, or nothing opens .streamDeckPlugin files (Stream Deck is not
    /// installed).</summary>
    public static bool Install()
    {
        try
        {
            using var stream = typeof(StreamDeckPluginPackage).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return false;
            var path = Path.Combine(Path.GetTempPath(), FileName);
            using (var file = File.Create(path)) stream.CopyTo(file);
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            DiagnosticsService.LogWindowHook($"[StreamDeckPlugin] install failed: {ex.GetType().Name}");
            return false;
        }
    }
}
