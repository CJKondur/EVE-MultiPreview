using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EveMultiPreview.Views;

/// <summary>
/// Scrollable character picker dialog — replaces the old VB InputBox.
/// Shows a filterable list of known characters with manual-entry fallback.
/// </summary>
public partial class CharacterPickerDialog : Window
{
    private readonly List<string> _allNames;

    /// <summary>Gets the selected or typed character name, or null if cancelled.</summary>
    public string? SelectedName { get; private set; }

    public CharacterPickerDialog(string title, IEnumerable<string> knownNames)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        _allNames = knownNames.Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                               .ToList();
        RefreshList(string.Empty);
        TxtSearch.Focus();
    }

    private void RefreshList(string filter)
    {
        LstCharacters.Items.Clear();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allNames
            : _allNames.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var name in filtered)
            LstCharacters.Items.Add(name);

        if (LstCharacters.Items.Count > 0)
            LstCharacters.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList(TxtSearch.Text);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // No special action needed; selection is read on OK
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstCharacters.SelectedItem is string name)
        {
            SelectedName = name;
            DialogResult = true;
        }
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        // Prefer list selection; fall back to typed text
        if (LstCharacters.SelectedItem is string selected)
            SelectedName = selected;
        else if (!string.IsNullOrWhiteSpace(TxtSearch.Text))
            SelectedName = TxtSearch.Text.Trim();
        else
        {
            SelectedName = null;
            DialogResult = false;
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        SelectedName = null;
        DialogResult = false;
    }
}
