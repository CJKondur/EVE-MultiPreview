using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace EveMultiPreview.SettingsUi;

/// <summary>The header bar of a panel that folds (Capsule's section): a toggle whose checked state is "open". A screen
/// reader is told it expands and collapses (expanded / collapsed, the section's title as its name), not that it is a
/// switch that is on or off.</summary>
public sealed class FoldToggle : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Announce(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Announce(ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed); }

    private void Announce(ExpandCollapseState was, ExpandCollapseState now) =>
        (UIElementAutomationPeer.FromElement(this) as Peer)?.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, was, now);

    private sealed class Peer : ToggleButtonAutomationPeer, IExpandCollapseProvider
    {
        private readonly FoldToggle _owner;
        public Peer(FoldToggle owner) : base(owner) => _owner = owner;

        public override object? GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.ExpandCollapse ? this : pattern == PatternInterface.Toggle ? null : base.GetPattern(pattern);

        public ExpandCollapseState ExpandCollapseState => _owner.IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() => _owner.IsChecked = true;
        public void Collapse() => _owner.IsChecked = false;
    }
}
