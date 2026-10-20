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
using CheckBox = System.Windows.Controls.CheckBox;
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
    private readonly TextBlock _arrangement = EditorCard.Value("", secondary: true);
    private readonly List<Button> _needsOne = new();
    private readonly CheckBox _hideNames;
    private bool _loading;

    public LayoutPresetsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _library = new LayoutPresetService(ctx.Service.ConfigDirectory);
        Loc.SetNameKey(_list, "L.S.thumbs.layout.presets");
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
        // IDEAS #12: a layout as one line of text, and back.
        var third = EditorCard.Row(NeedsOne("L.S.Presets.CopyCode", CopyCode), EditorCard.Button("L.S.Presets.PasteCode", PasteCode));
        third.Margin = new Thickness(0, 8, 0, 0);
        // IDEAS P18: the layout as a picture of boxes and names, to share or attach to a help request.
        var hideText = new TextBlock();
        Loc.SetKey(hideText, "L.S.Presets.HideNames");
        _hideNames = new CheckBox { Content = hideText, IsChecked = HideNamesAtStart(), VerticalAlignment = VerticalAlignment.Center };
        var fourth = EditorCard.Row(NeedsOne("L.S.Presets.ExportImage", ExportImage), EditorCard.Button("L.S.Presets.WallImage", ExportWallImage), _hideNames);
        fourth.Margin = new Thickness(0, 8, 0, 0);
        _note.Margin = new Thickness(0, 8, 0, 0);
        _arrangement.Margin = new Thickness(0, 4, 0, 0);
        var below = new StackPanel();
        below.Children.Add(first);
        below.Children.Add(second);
        below.Children.Add(third);
        below.Children.Add(fourth);
        below.Children.Add(_note);
        below.Children.Add(_arrangement);
        Content = EditorCard.Build("thumbs.layout.presets", null, below);
        Refresh();
    }

    /// <summary>The saved layouts, in the file's order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    /// <summary>Test seam: stands in for ThumbnailManager.CaptureCurrentLayoutPreset.</summary>
    public Func<string, LayoutPreset?>? Capture { get; set; }

    /// <summary>Test seam: stands in for ClipboardText.Set.</summary>
    public Func<string, bool>? CopyText { get; set; }

    /// <summary>Test seam: stands in for the monitors' bounds (ThumbnailManager.CurrentMonitorBounds).</summary>
    public Func<IReadOnlyList<System.Drawing.Rectangle>> Screens { get; set; } = ThumbnailManager.CurrentMonitorBounds;

    /// <summary>IDEAS #10: what the selected layout says of the monitors it was saved on (how many, and whether they are the
    /// ones you have now); empty with no layout selected. Never a device name: the fingerprint holds none.</summary>
    public string ArrangementText => _arrangement.Text;

    /// <summary>Test seam: stands in for the monitors (LayoutImage.CurrentMonitors), bounds and work areas.</summary>
    public Func<IReadOnlyList<LayoutMonitor>> Monitors { get; set; } = LayoutImage.CurrentMonitors;

    /// <summary>Test seam: stands in for LayoutImage.Png (drawing the picture needs a render device).</summary>
    public Func<LayoutImagePlan, byte[]> EncodeImage { get; set; } = LayoutImage.Png;

    /// <summary>IDEAS P18: draw slot numbers instead of character names in the image (the "Hide names" box).</summary>
    public bool HideNames
    {
        get => _hideNames.IsChecked == true;
        set => _hideNames.IsChecked = value;
    }

    /// <summary>Whether "Hide names" starts ticked: while streamer mode (IDEAS #19) is on. The box is not a setting, so it
    /// starts from this each time the page is built, and <see cref="Refresh"/> ticks it again if the mode came on since.</summary>
    private static bool HideNamesAtStart() => StreamerMask.IsOn;

    /// <summary>The layout the buttons act on, or null.</summary>
    public string? Selected { get; private set; }

    private static string Str(string key) => SettingsContext.Str(key);

    private string Applied => CharacterDirectory.Current(_ctx.S).AppliedLayoutPreset;

    private static string SaveFilter => Str("L.S.Filter.Layouts") + " (*.emplayout)|*.emplayout|JSON (*.json)|*.json";
    private static string ImageFilter => "PNG (*.png)|*.png";
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
        if (StreamerMask.IsOn) _hideNames.IsChecked = true;   // the mode came on since the page was built (never unticks a box the player ticked)
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
        ShowArrangement();
    }

    private void ShowArrangement()
    {
        var preset = Selected == null ? null : _library.Get(Selected);
        int count = MonitorFingerprint.Count(preset?.MonitorFingerprint);
        _arrangement.Text = preset == null ? ""
            : count < 0 ? Str("L.S.Presets.ArrangementNone")
            : string.Format(Str(preset.MonitorFingerprint == MonitorFingerprint.Of(Screens()) ? "L.S.Presets.ArrangementNow" : "L.S.Presets.Arrangement"), count);
        _arrangement.Visibility = _arrangement.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
    public void Remap() => RunRemap();

    /// <summary>True only when the dialog was accepted and the layout applied (P129: Paste a code says what to do next when not).</summary>
    private bool RunRemap()
    {
        var preset = Chosen();
        if (preset == null) return false;
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(Str("L.S.Presets.NoClients")); return false; }
        if (_ctx.Host.DialogOwner is not { } owner) return false;
        var dialog = new Views.LayoutRemapDialog(owner, preset.Slots, running);
        if (dialog.ShowDialog() != true) return false;
        _ctx.Services.Thumbnails?.ApplyLayoutPresetMapped(preset, dialog.Mapping);
        if (preset.IncludesVisibility) _ctx.Applier.Request(ApplyScope.Crops);
        _ctx.Host.Toast(string.Format(Str("L.S.Presets.Applied"), preset.Name));
        return true;
    }

    /// <summary>What captures the live wall, or null with no clients running.</summary>
    private Func<string, LayoutPreset?>? CaptureSource()
    {
        var tm = _ctx.Services.Thumbnails;
        return Capture ?? (tm == null || CharacterDirectory.Running(_ctx).Count == 0 ? null : tm.CaptureCurrentLayoutPreset);
    }

    /// <summary>Old OnSaveLayoutPreset, asking before it replaces a layout with the same name.</summary>
    public void SaveCurrent()
    {
        var capture = CaptureSource();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _ctx.Host.Toast(Str("L.S.Presets.ExportFailed"));
        }
    }

    /// <summary>IDEAS P18: the selected layout as a PNG picture of boxes and names, saved where the user chooses. Drawn
    /// text only; writes nothing else.</summary>
    public void ExportImage()
    {
        var preset = Chosen();
        if (preset != null) SaveImage(preset, preset.Name);
    }

    /// <summary>IDEAS P18: the wall as it is now, as a PNG picture.</summary>
    public void ExportWallImage()
    {
        var capture = CaptureSource();
        if (capture == null) { _ctx.Host.Toast(Str("L.S.Presets.NoThumbnails")); return; }
        var preset = capture("");
        if (preset == null) { _ctx.Host.Toast(Str("L.S.Presets.NoThumbnails")); return; }
        SaveImage(preset, "layout");
    }

    private void SaveImage(LayoutPreset preset, string name)
    {
        var monitors = Monitors();
        bool hide = HideNames || StreamerMask.IsOn;   // streamer mode wins over an unticked box, whenever it came on
        var plan = LayoutImage.Plan(LayoutImage.Resolve(preset, monitors), monitors, hide);
        if (plan.Boxes.Count == 0) { _ctx.Host.Toast(Str("L.S.Presets.ImageEmpty")); return; }
        // With names hidden the file name must not carry the layout's name either: the dialog is on the screen too.
        var file = _ctx.Host.PickSaveFile((hide ? "layout" : LayoutImage.FileStem(name)) + ".png", ImageFilter);
        if (file == null) return;
        // Drawing, encoding or writing can fail in ways no list of exceptions foresees (a render device, a full disk): any of
        // them is "could not save", never a crash. The picture goes to a temporary file beside the chosen one and is moved
        // over it, so a failure half way never leaves a partial PNG under the name the player chose.
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = EncodeImage(plan);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
            _ctx.Host.Toast(string.Format(Str("L.S.Presets.ImageSaved"), Path.GetFileName(file)));
        }
        catch (Exception)
        {
            try { File.Delete(temp); } catch (Exception) { }   // a temporary file that was not made, or cannot be removed, is not worth a second message
            _ctx.Host.Toast(Str("L.S.Presets.ImageFailed"));
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

    /// <summary>IDEAS #12: the selected layout as a code on the clipboard (its places only). Writes nothing.</summary>
    public void CopyCode()
    {
        var preset = Chosen();
        if (preset == null) return;
        var code = ShareCode.Layout(preset);
        if (code == null) { _ctx.Host.Toast(Str("L.S.Presets.CodeTooBig")); return; }
        bool ok = (CopyText ?? ClipboardText.Set)(code);
        // P128: more than one Discord message takes: say so, rather than "Paste it in Discord". A failed copy says so whatever the length.
        var key = !ok ? "L.S.Presets.CodeCopyFailed" : code.Length > ShareCode.DiscordSafeLength ? "L.S.Presets.CodeLong" : "L.S.Presets.CodeCopied";
        _ctx.Host.Toast(ok ? string.Format(Str(key), preset.Name, code.Length) : Str(key));
    }

    /// <summary>IDEAS #12: a layout from a pasted code, added to the list (asking before it replaces one with its name).
    /// With clients running, Remap and apply opens at once: the code's characters are someone else's, and Apply would
    /// save places for their names.</summary>
    public void PasteCode()
    {
        var text = _ctx.Host.AskText(Str("L.S.Presets.PasteTitle"), Str("L.S.Presets.PastePrompt"), "");
        if (text == null) return;
        var read = ShareCode.ReadLayout(text);
        if (read.Value is not { } preset) { _ctx.Host.Toast(Str(read.ErrorKey)); return; }
        if (_library.Get(preset.Name) != null
            && !_ctx.Host.Confirm(Str("L.S.Presets.ReplaceTitle"), string.Format(Str("L.S.Presets.ReplaceConfirm"), preset.Name))) return;
        _library.AddOrReplace(preset);
        Selected = preset.Name;
        Refresh();
        if (CharacterDirectory.Running(_ctx).Count == 0)
        {
            _ctx.Host.Toast(string.Format(Str("L.S.Presets.CodeAdded"), preset.Name));
            return;
        }
        // P129: a cancelled remap (or none possible) leaves the layout in the list: say what to do next, as with no clients running.
        bool applied = RunRemap();
        if (!applied) _ctx.Host.Toast(string.Format(Str("L.S.Presets.CodeAdded"), preset.Name));
    }
}
