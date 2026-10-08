using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Thumbnails › Layout &amp; screens › Saved layouts (spec §5): the old preset row
/// (SettingsWindow.Presets.cs) on the same LayoutPresetService file, shared by every profile.
/// Capture and apply are ThumbnailManager's.</summary>
public sealed class LayoutPresetsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly LayoutPresetService _library;
    private readonly ComboBox _list = new() { MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = EditorCard.Value("", secondary: true);
    private readonly List<Button> _needsOne = new();
    private bool _loading;

    public LayoutPresetsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _library = new LayoutPresetService(ctx.Service.ConfigDirectory);
        AutomationProperties.SetName(_list, Str("L.S.thumbs.layout.presets"));
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Selected = _list.SelectedItem as string;
            UpdateButtons();
        };
        var first = EditorCard.Row(_list, NeedsOne("L.S.Presets.Apply", Apply), NeedsOne("L.S.Presets.Remap", Remap),
            NeedsOne("L.S.Presets.Delete", Delete, destructive: true));
        var second = EditorCard.Row(EditorCard.Button("L.S.Presets.Save", SaveCurrent), NeedsOne("L.S.Presets.Export", Export),
            EditorCard.Button("L.S.Presets.Import", Import));
        second.Margin = new Thickness(0, 8, 0, 0);
        _note.Margin = new Thickness(0, 8, 0, 0);
        var below = new StackPanel();
        below.Children.Add(first);
        below.Children.Add(second);
        below.Children.Add(_note);
        Content = EditorCard.Build("thumbs.layout.presets", null, below);
        Refresh();
    }

    /// <summary>The saved layouts, in the file's order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    /// <summary>Test seam: stands in for ThumbnailManager.CaptureCurrentLayoutPreset.</summary>
    public Func<string, LayoutPreset?>? Capture { get; set; }

    /// <summary>The layout the buttons act on, or null.</summary>
    public string? Selected { get; private set; }

    private static string Str(string key) => SettingsContext.Str(key);

    private string Applied => CharacterDirectory.Current(_ctx.S).AppliedLayoutPreset;

    private static string SaveFilter => Str("L.S.Filter.Layouts") + " (*.emplayout)|*.emplayout|JSON (*.json)|*.json";
    private static string OpenFilter => Str("L.S.Filter.Layouts") + " (*.emplayout, *.json)|*.emplayout;*.json|*.*|*.*";

    private Button NeedsOne(string key, Action run, bool destructive = false)
    {
        var button = EditorCard.Button(key, run, destructive);
        _needsOne.Add(button);
        return button;
    }

    /// <summary>Re-reads the file (another window may have changed it). Keeps the selection while it
    /// exists, else selects the layout this profile last applied.</summary>
    public void Refresh()
    {
        _library.Load();
        Names = _library.Names;
        Selected = Find(Selected) ?? Find(Applied);
        _loading = true;
        _list.Items.Clear();
        foreach (var n in Names) _list.Items.Add(n);
        _list.SelectedItem = Selected;
        _loading = false;
        _note.Text = Names.Count == 0 ? Str("L.S.Presets.Empty")
            : string.IsNullOrWhiteSpace(Applied) ? Str("L.S.Presets.NoneUsed")
            : string.Format(Str("L.S.Presets.Current"), Applied);
        UpdateButtons();
    }

    public void Select(string name)
    {
        Selected = Find(name);
        _loading = true;
        _list.SelectedItem = Selected;
        _loading = false;
        UpdateButtons();
    }

    private string? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private void UpdateButtons()
    {
        foreach (var button in _needsOne) button.IsEnabled = Selected != null;
    }

    /// <summary>The selected layout, or a toast and null.</summary>
    private LayoutPreset? Chosen()
    {
        var preset = Selected == null ? null : _library.Get(Selected);
        if (preset == null) _ctx.Host.Toast(Str("L.S.Presets.PickFirst"));
        return preset;
    }

    /// <summary>Old OnApplyLayoutPreset. ApplyLayoutPreset is one undo step and saves what it changes.</summary>
    public void Apply()
    {
        var preset = Chosen();
        if (preset == null) return;
        _ctx.Services.Thumbnails?.ApplyLayoutPreset(preset);
        CharacterDirectory.Current(_ctx.S).AppliedLayoutPreset = preset.Name;   // #94: named when switching profiles
        // A layout saved with visibility may have turned crop windows on or off: reconcile them.
        _ctx.Applier.Request(preset.IncludesVisibility ? ApplyScope.Save | ApplyScope.Crops : ApplyScope.Save);
        _ctx.Host.Toast(string.Format(Str("L.S.Presets.Applied"), preset.Name));
        Refresh();
    }

    /// <summary>Old OnRemapLayoutPreset: choose which running character takes each place.</summary>
    public void Remap()
    {
        var preset = Chosen();
        if (preset == null) return;
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(Str("L.S.Presets.NoClients")); return; }
        if (_ctx.Host.DialogOwner is not { } owner) return;
        var dialog = new Views.LayoutRemapDialog(owner, preset.Slots, running);
        if (dialog.ShowDialog() != true) return;
        _ctx.Services.Thumbnails?.ApplyLayoutPresetMapped(preset, dialog.Mapping);
        if (preset.IncludesVisibility) _ctx.Applier.Request(ApplyScope.Crops);
        _ctx.Host.Toast(string.Format(Str("L.S.Presets.Applied"), preset.Name));
    }

    /// <summary>Old OnSaveLayoutPreset, asking before it replaces a layout with the same name.</summary>
    public void SaveCurrent()
    {
        var tm = _ctx.Services.Thumbnails;
        var capture = Capture ?? (tm == null || CharacterDirectory.Running(_ctx).Count == 0 ? null : tm.CaptureCurrentLayoutPreset);
        if (capture == null) { _ctx.Host.Toast(Str("L.S.Presets.NoThumbnails")); return; }
        var name = _ctx.Host.AskText(Str("L.S.Presets.SaveTitle"), Str("L.S.Presets.NamePrompt"), Selected ?? "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var preset = capture(name);
        if (preset == null || preset.Slots.Count == 0) { _ctx.Host.Toast(Str("L.S.Presets.NoThumbnails")); return; }
        if (_library.Get(name) != null
            && !_ctx.Host.Confirm(Str("L.S.Presets.ReplaceTitle"), string.Format(Str("L.S.Presets.ReplaceConfirm"), name))) return;
        _library.AddOrReplace(preset);
        Selected = name;
        Refresh();
        _ctx.Host.Toast(string.Format(Str("L.S.Presets.Saved"), name));
    }

    public void Delete()
    {
        if (Selected == null || _library.Get(Selected) == null) return;
        if (!_ctx.Host.Confirm(Str("L.S.Presets.DeleteTitle"), string.Format(Str("L.S.Presets.DeleteConfirm"), Selected))) return;
        _library.Delete(Selected);
        Selected = null;
        Refresh();
    }

    public void Export()
    {
        var preset = Chosen();
        if (preset == null) return;
        var file = _ctx.Host.PickSaveFile(preset.Name + ".emplayout", SaveFilter);
        if (file == null) return;
        try
        {
            _library.Export(preset, file);
            _ctx.Host.Toast(string.Format(Str("L.S.Presets.Exported"), Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Host.Toast(Str("L.S.Presets.ExportFailed"));
        }
    }

    /// <summary>Old OnImportLayoutPreset: adds the file's layout, asking before it replaces one with its name.</summary>
    public void Import()
    {
        var file = _ctx.Host.PickFile(null, OpenFilter);
        if (file == null) return;
        var preset = _library.ReadFile(file);
        if (preset == null) { _ctx.Host.Toast(Str("L.S.Presets.ImportFailed")); return; }
        if (_library.Get(preset.Name) != null
            && !_ctx.Host.Confirm(Str("L.S.Presets.ReplaceTitle"), string.Format(Str("L.S.Presets.ReplaceConfirm"), preset.Name))) return;
        _library.AddOrReplace(preset);
        Selected = preset.Name;
        Refresh();
        _ctx.Host.Toast(string.Format(Str("L.S.Presets.Imported"), preset.Name));
    }
}
