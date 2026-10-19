using System;
using System.Collections.Generic;
using EveMultiPreview.Models;

namespace EveMultiPreview.Views;

/// <summary>
/// Maps a (possibly shared / cross-machine) layout preset's slots onto the user's
/// own running characters. Exact name matches are pre-selected; the user can
/// reassign or skip each slot. Result is in <see cref="Mapping"/> (slot index →
/// target character) when the dialog returns true.
/// </summary>
public sealed class LayoutRemapDialog : System.Windows.Window
{
    private readonly List<System.Windows.Controls.ComboBox> _combos = new();

    /// <summary>slot index → chosen target character (only mapped, non-skipped slots).</summary>
    public Dictionary<int, string> Mapping { get; } = new();

    /// <summary>The slots' lists, in order (tests).</summary>
    public IReadOnlyList<System.Windows.Controls.ComboBox> Lists => _combos;

    private static string Str(string key, string english) => EveMultiPreview.Services.LocalizationService.Str(key, english);

    public LayoutRemapDialog(System.Windows.Window owner, IReadOnlyList<LayoutSlot> slots, IReadOnlyList<string> availableChars)
    {
        EveMultiPreview.SettingsUi.UiStyles.Attach(this);
        Owner = owner;
        Title = Str("L.Remap.Title", "Map Preset Slots → Your Characters");
        Width = 480;
        SizeToContent = System.Windows.SizeToContent.Height;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");
        // FX-m6: closed lists in a scroll viewer: the wheel scrolls the list of slots, not a slot's choice (RE2-m4).
        PreviewMouseWheel += EveMultiPreview.SettingsUi.SettingsShell.WheelPastClosedLists;

        var dock = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(12) };

        var header = new System.Windows.Controls.TextBlock
        {
            Text = Str("L.Remap.Intro", "Assign each preset slot to one of your running characters. Exact name matches are pre-selected; choose “(skip)” to leave a slot unused."),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        };
        header.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Fx.Label");
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        dock.Children.Add(header);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        var ok = new System.Windows.Controls.Button
        {
            Content = Str("L.Remap.Apply", "Apply"), Padding = new System.Windows.Thickness(14, 4, 14, 4),
            Margin = new System.Windows.Thickness(0, 0, 6, 0), IsDefault = true,
        };
        ok.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Fx.PrimaryButton");
        ok.Click += (_, _) => Commit();
        var cancel = new System.Windows.Controls.Button
        {
            Content = Str("L.Remap.Cancel", "Cancel"), Padding = new System.Windows.Thickness(14, 4, 14, 4), IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dock.Children.Add(buttons);

        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            MaxHeight = 420,
        };
        var rows = new System.Windows.Controls.StackPanel();
        scroll.Content = rows;
        dock.Children.Add(scroll);

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new System.Windows.Thickness(0, 2, 0, 2),
            };
            var slotName = new System.Windows.Controls.TextBlock
            {
                Text = $"{i + 1}.  {(string.IsNullOrWhiteSpace(slot.Character) ? Str("L.Remap.Slot", "(slot)") : slot.Character)}",
                Width = 200,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            slotName.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Fx.Label");
            row.Children.Add(slotName);

            var combo = new System.Windows.Controls.ComboBox { Width = 220 };
            combo.Items.Add(Str("L.Remap.Skip", "(skip)"));
            int sel = 0;
            for (int j = 0; j < availableChars.Count; j++)
            {
                combo.Items.Add(availableChars[j]);
                if (sel == 0 && string.Equals(availableChars[j], slot.Character, StringComparison.OrdinalIgnoreCase))
                    sel = j + 1; // +1 for the "(skip)" entry
            }
            combo.SelectedIndex = sel;
            row.Children.Add(combo);

            rows.Children.Add(row);
            _combos.Add(combo);
        }

        var frame = new System.Windows.Controls.Border { Child = dock };
        frame.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Fx.Dialog");
        Content = frame;
    }

    private void Commit()
    {
        Mapping.Clear();
        for (int i = 0; i < _combos.Count; i++)
        {
            if (_combos[i].SelectedIndex > 0 && _combos[i].SelectedItem is string sel && !string.IsNullOrWhiteSpace(sel))
                Mapping[i] = sel;
        }
        DialogResult = true;
    }
}
