using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EveMultiPreview.SettingsUi;

/// <summary>Settings search in the navigation pane (spec §4, §6).</summary>
public partial class SearchBox : System.Windows.Controls.UserControl
{
    private readonly Func<IReadOnlyList<SearchEntry>> _index;
    private readonly Action<SearchEntry> _open;

    public SearchBox(Func<IReadOnlyList<SearchEntry>> index, Action<SearchEntry> open)
    {
        InitializeComponent();
        _index = index;
        _open = open;
    }

    public void FocusBox() { Box.Focus(); Box.SelectAll(); }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hits = SearchIndex.Query(_index(), Box.Text);
        Results.ItemsSource = hits;
        ResultsPopup.IsOpen = hits.Count > 0;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Box.Clear(); ResultsPopup.IsOpen = false; e.Handled = true; break;
            case Key.Enter: OpenSelectedOrFirst(); e.Handled = true; break;
            case Key.Down when Results.Items.Count > 0:
                Results.SelectedIndex = 0;
                (Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelectedOrFirst(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ResultsPopup.IsOpen = false; Box.Focus(); e.Handled = true; }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e) => OpenSelectedOrFirst();

    private void OpenSelectedOrFirst()
    {
        var entry = Results.SelectedItem as SearchEntry ?? (Results.ItemsSource as IEnumerable<SearchEntry>)?.FirstOrDefault();
        if (entry == null) return;
        ResultsPopup.IsOpen = false;
        _open(entry);
    }
}
