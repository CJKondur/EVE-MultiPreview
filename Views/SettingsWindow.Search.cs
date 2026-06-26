using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Views;

// Settings search / Ctrl+F across all panels. Builds a one-time index of every
// concise label/checkbox/button/group-header in each panel, then jumps to the
// panel and glows the matched control. Pure UI convenience over the existing
// ShowPanel navigation — a deep 17-panel window is hard to navigate by memory.
public partial class SettingsWindow
{
    private sealed record SearchEntry(string Text, string Panel, System.Windows.FrameworkElement Element);

    private sealed class SearchResultRow
    {
        public SearchEntry Entry { get; }
        public string Display { get; }
        public SearchResultRow(SearchEntry e) { Entry = e; Display = $"{e.Text}    ·    {e.Panel}"; }
        public override string ToString() => Display;
    }

    private List<SearchEntry>? _searchIndex;

    /// <summary>Ctrl+F (ApplicationCommands.Find) — focus the search box.</summary>
    private void OnFindSettings(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        TxtSettingsSearch.Focus();
        TxtSettingsSearch.SelectAll();
    }

    private void OnSettingsSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var q = (TxtSettingsSearch.Text ?? "").Trim();
        SearchPlaceholder.Visibility = q.Length == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        if (q.Length < 2) { SearchResultsPopup.IsOpen = false; return; }
        if (_searchIndex == null) BuildSearchIndex();

        var matches = _searchIndex!
            .Where(en => en.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || en.Panel.Contains(q, StringComparison.OrdinalIgnoreCase))
            .GroupBy(en => en.Text.ToLowerInvariant() + "|" + en.Panel)
            .Select(g => g.First())
            .OrderByDescending(en => en.Text.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(en => en.Text.Length)
            .Take(40)
            .Select(en => new SearchResultRow(en))
            .ToList();

        LstSearchResults.ItemsSource = matches;
        SearchResultsPopup.IsOpen = matches.Count > 0;
    }

    private void OnSettingsSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                TxtSettingsSearch.Text = "";
                SearchResultsPopup.IsOpen = false;
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter:
                var row = LstSearchResults.SelectedItem as SearchResultRow
                          ?? (LstSearchResults.Items.Count > 0 ? LstSearchResults.Items[0] as SearchResultRow : null);
                if (row != null) ActivateSearchEntry(row.Entry);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down when LstSearchResults.Items.Count > 0:
                LstSearchResults.SelectedIndex = 0;
                (LstSearchResults.ItemContainerGenerator.ContainerFromIndex(0)
                    as System.Windows.Controls.ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LstSearchResults.SelectedItem is SearchResultRow row)
            ActivateSearchEntry(row.Entry);
    }

    private void ActivateSearchEntry(SearchEntry entry)
    {
        SearchResultsPopup.IsOpen = false;
        ShowPanel(entry.Panel);
        // Defer until the panel is shown/laid out, then scroll to + glow the control.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { entry.Element.BringIntoView(); } catch { }
            HighlightElement(entry.Element);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        LstSearchResults.SelectedItem = null;
    }

    /// <summary>Brief gold glow on the matched control so the eye lands on it.</summary>
    private static void HighlightElement(System.Windows.FrameworkElement fe)
    {
        var prev = fe.Effect;
        fe.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = System.Windows.Media.Colors.Gold,
            BlurRadius = 20,
            ShadowDepth = 0,
            Opacity = 1.0,
        };
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        t.Tick += (_, _) => { t.Stop(); fe.Effect = prev; };
        t.Start();
    }

    private void BuildSearchIndex()
    {
        var idx = new List<SearchEntry>();
        foreach (var panel in _panels)
        {
            if (FindName("Panel" + panel) is not System.Windows.DependencyObject root) continue;
            foreach (var fe in EnumerateLogical(root))
            {
                var text = ExtractLabel(fe);
                if (string.IsNullOrWhiteSpace(text)) continue;
                text = text!.Trim();
                // Concise labels only — skip help-paragraph blurbs and stray chars.
                if (text.Length < 2 || text.Length > 70) continue;
                idx.Add(new SearchEntry(text, panel, fe));
            }
        }
        _searchIndex = idx;
    }

    private static IEnumerable<System.Windows.FrameworkElement> EnumerateLogical(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is not System.Windows.DependencyObject dobj) continue;
            if (dobj is System.Windows.FrameworkElement fe) yield return fe;
            foreach (var d in EnumerateLogical(dobj)) yield return d;
        }
    }

    private static string? ExtractLabel(System.Windows.FrameworkElement fe) => fe switch
    {
        System.Windows.Controls.CheckBox cb => cb.Content as string,
        System.Windows.Controls.RadioButton rb => rb.Content as string,
        System.Windows.Controls.Label lb => lb.Content as string,
        System.Windows.Controls.TextBlock tb => tb.Text,
        System.Windows.Controls.Button btn => btn.Content as string,
        System.Windows.Controls.GroupBox gb => gb.Header as string,
        _ => null,
    };
}
