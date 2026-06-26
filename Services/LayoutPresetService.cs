using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Stores named layout presets in a small JSON file next to the config, and
/// handles single-file export/import so fleet-wall templates can be shared.
/// Capture/apply geometry lives in ThumbnailManager (it owns the thumbnails);
/// this class is just persistence + the shared file format.
/// </summary>
public sealed class LayoutPresetService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<LayoutPreset> Presets { get; private set; } = new();

    public LayoutPresetService(string configDir)
    {
        _path = Path.Combine(configDir, "layout_presets.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Presets = JsonSerializer.Deserialize<List<LayoutPreset>>(File.ReadAllText(_path)) ?? new();
        }
        catch { Presets = new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Presets, Json)); }
        catch { }
    }

    public IReadOnlyList<string> Names => Presets.Select(p => p.Name).ToList();

    public LayoutPreset? Get(string name)
        => Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddOrReplace(LayoutPreset preset)
    {
        Presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        Presets.Add(preset);
        Save();
    }

    public void Delete(string name)
    {
        Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Export(LayoutPreset preset, string file)
        => File.WriteAllText(file, JsonSerializer.Serialize(preset, Json));

    /// <summary>Import a single shared preset file, add it to the library, return it
    /// (null on failure / empty preset).</summary>
    public LayoutPreset? Import(string file)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<LayoutPreset>(File.ReadAllText(file));
            if (preset == null || preset.Slots == null || preset.Slots.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(preset.Name))
                preset.Name = Path.GetFileNameWithoutExtension(file);
            AddOrReplace(preset);
            return preset;
        }
        catch { return null; }
    }
}
