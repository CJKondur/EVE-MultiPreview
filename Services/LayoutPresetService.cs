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
    public const long MaxFileBytes = 1_048_576;
    public const int MaxFileSlots = 256;

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

    /// <summary>Parse a shared preset file without adding it (null on failure / empty preset). P122: a file over
    /// <see cref="MaxFileBytes"/> is not read, one with more than <see cref="MaxFileSlots"/> places, or a place that fails
    /// <see cref="ShareCode.ValidSlot"/>, is refused whole. The name keeps its words but never holds a path character
    /// (<see cref="ShareCode.SafeName"/>); one with nothing left takes the file's name, then "Layout".</summary>
    public LayoutPreset? ReadFile(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes) return null;
            var preset = JsonSerializer.Deserialize<LayoutPreset>(File.ReadAllText(file));
            if (preset == null || preset.Slots == null || preset.Slots.Count == 0 || preset.Slots.Count > MaxFileSlots) return null;
            foreach (var slot in preset.Slots)
                if (slot == null || !ShareCode.ValidSlot(slot.Character, slot.Monitor, slot.Fx, slot.Fy, slot.Fw, slot.Fh)) return null;
            var name = ShareCode.SafeName(preset.Name);
            if (name.Length == 0) name = ShareCode.SafeName(Path.GetFileNameWithoutExtension(file));
            preset.Name = name.Length > 0 ? name : "Layout";
            return preset;
        }
        catch { return null; }
    }

    /// <summary>Import a single shared preset file, add it to the library, return it
    /// (null on failure / empty preset).</summary>
    public LayoutPreset? Import(string file)
    {
        var preset = ReadFile(file);
        if (preset != null) AddOrReplace(preset);
        return preset;
    }
}
