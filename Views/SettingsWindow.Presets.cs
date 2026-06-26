using System;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

// Named, monitor-agnostic layout presets (capture / apply / delete / export / import).
// Geometry lives in ThumbnailManager; persistence + file format in LayoutPresetService.
public partial class SettingsWindow
{
    private LayoutPresetService? _layoutPresets;
    private LayoutPresetService LayoutPresets => _layoutPresets ??= new LayoutPresetService(_svc.ConfigDirectory);

    private void PopulateLayoutPresets()
    {
        var current = CmbLayoutPresets.Text;
        CmbLayoutPresets.Items.Clear();
        foreach (var n in LayoutPresets.Names) CmbLayoutPresets.Items.Add(n);
        CmbLayoutPresets.Text = current;
    }

    private string PresetNameInBox() => (CmbLayoutPresets.Text ?? "").Trim();

    private void OnSaveLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_thumbnailManager == null) return;
        var name = PresetNameInBox();
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show("Type a name for the preset first.", "Save Layout Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var preset = _thumbnailManager.CaptureCurrentLayoutPreset(name);
        if (preset.Slots.Count == 0)
        {
            System.Windows.MessageBox.Show("No thumbnails to capture — launch your clients first.", "Save Layout Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        LayoutPresets.AddOrReplace(preset);
        PopulateLayoutPresets();
        CmbLayoutPresets.Text = name;
    }

    private void OnApplyLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        var preset = LayoutPresets.Get(PresetNameInBox());
        if (preset == null)
        {
            System.Windows.MessageBox.Show("Pick a saved preset to apply.", "Apply Layout Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _thumbnailManager?.ApplyLayoutPreset(preset);
        // A preset that captured crop state may have flipped CropEnabled — reconcile crops.
        if (preset.IncludesVisibility) _cropManager?.Refresh();
    }

    private void OnRemapLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        var preset = LayoutPresets.Get(PresetNameInBox());
        if (preset == null)
        {
            System.Windows.MessageBox.Show("Pick a saved preset to remap.", "Remap Layout Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var chars = new System.Collections.Generic.List<string>();
        var names = _thumbnailManager?.GetActiveCharacterNames();
        if (names != null)
            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n)) chars.Add(n);
        chars.Sort(StringComparer.OrdinalIgnoreCase);
        if (chars.Count == 0)
        {
            System.Windows.MessageBox.Show("No running clients to map to — launch your clients first.", "Remap Layout Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dlg = new LayoutRemapDialog(this, preset.Slots, chars);
        if (dlg.ShowDialog() == true)
        {
            _thumbnailManager?.ApplyLayoutPresetMapped(preset, dlg.Mapping);
            if (preset.IncludesVisibility) _cropManager?.Refresh();
        }
    }

    private void OnDeleteLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        var name = PresetNameInBox();
        if (LayoutPresets.Get(name) == null) return;
        if (System.Windows.MessageBox.Show($"Delete layout preset '{name}'?", "Delete Preset",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        LayoutPresets.Delete(name);
        PopulateLayoutPresets();
        CmbLayoutPresets.Text = "";
    }

    private void OnExportLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        var name = PresetNameInBox();
        var preset = LayoutPresets.Get(name);
        if (preset == null)
        {
            System.Windows.MessageBox.Show("Pick a saved preset to export.", "Export Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = name + ".emplayout",
            Filter = "EVE MultiPreview layout (*.emplayout)|*.emplayout|JSON (*.json)|*.json",
            DefaultExt = ".emplayout",
        };
        if (dlg.ShowDialog() == true)
        {
            try { LayoutPresets.Export(preset, dlg.FileName); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Export failed: {ex.Message}", "Export Preset",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    private void OnImportLayoutPreset(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "EVE MultiPreview layout (*.emplayout;*.json)|*.emplayout;*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var preset = LayoutPresets.Import(dlg.FileName);
        if (preset == null)
        {
            System.Windows.MessageBox.Show("That file isn't a valid layout preset.", "Import Preset",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        PopulateLayoutPresets();
        CmbLayoutPresets.Text = preset.Name;
    }
}
