using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EveMultiPreview.Services;
using Point = System.Windows.Point;

namespace EveMultiPreview.SettingsUi;

/// <summary>The per-page help column (spec §9). Built in code from the markup because topic bodies
/// are rich text; rebuilt on page change and on language change (ShowPage again).</summary>
public partial class HelpPane : System.Windows.Controls.UserControl
{
    private readonly Func<string, string?> _resolve;
    private readonly IReadOnlyList<HelpTopic> _topics;
    private readonly Dictionary<string, FrameworkElement> _topicElements = new();
    private FrameworkElement? _scrolledTo;

    /// <summary>App use: strings from the live resources, topics from HelpTopics.All.</summary>
    public HelpPane() : this(key => LocalizationService.Str(key, ""), HelpTopics.All) { }

    /// <summary><paramref name="resolve"/> returns null or "" for a missing key.</summary>
    public HelpPane(Func<string, string?> resolve, IReadOnlyList<HelpTopic> topics)
    {
        InitializeComponent();
        _resolve = resolve;
        _topics = topics;
    }

    public event Action? CloseRequested;
    /// <summary>Topics shown for the current page (missing ones are skipped).</summary>
    public int TopicCount { get; private set; }

    public void ShowPage(string pageId)
    {
        Body.Children.Clear();
        _topicElements.Clear();
        _scrolledTo = null;
        TopicCount = 0;
        AddBlocks(_resolve(HelpTopics.IntroKey(pageId)), Body);
        foreach (var topic in _topics.Where(t => t.PageId == pageId))
        {
            var title = _resolve(topic.TitleKey);
            var body = _resolve(topic.BodyKey);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) continue;   // spec §8.5: hide, don't show blank
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Fx.Label"), FontWeight = FontWeights.SemiBold });
            AddBlocks(body, panel);
            Body.Children.Add(panel);
            TopicCount++;
            foreach (var id in topic.SettingIds) _topicElements[id] = panel;
        }
        Scroll.ScrollToTop();
    }

    /// <summary>Scrolls the topic that explains <paramref name="itemId"/> to the top of the pane (spec §9); false if no
    /// topic does. BringIntoView stopped as soon as the topic was visible, usually at the bottom.</summary>
    public bool ScrollToTopicFor(string itemId)
    {
        if (!_topicElements.TryGetValue(itemId, out var element)) return false;
        if (element == _scrolledTo) return true;   // already brought to the top: hovering another card of it leaves the pane alone
        _scrolledTo = element;
        Scroll.UpdateLayout();
        Scroll.ScrollToVerticalOffset(element.TranslatePoint(new Point(0, 0), Body).Y);
        return true;
    }

    /// <summary>How far below the top of the visible help the topic for <paramref name="itemId"/> starts (tests); NaN if none.</summary>
    public double TopicTop(string itemId) =>
        _topicElements.TryGetValue(itemId, out var element) ? element.TranslatePoint(new Point(0, 0), Scroll).Y : double.NaN;

    private void AddBlocks(string? text, System.Windows.Controls.Panel target)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var block in HelpMarkup.Parse(text))
        {
            var tb = new TextBlock { Style = (Style)FindResource("Fx.Label"), Margin = new Thickness(block is HelpBullet ? 12 : 0, 6, 0, 0) };
            if (block is HelpBullet) tb.Inlines.Add(new Run("•  "));
            foreach (var run in block.Runs)
                tb.Inlines.Add(run.Bold ? new Bold(new Run(run.Text)) : new Run(run.Text));
            target.Children.Add(tb);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
