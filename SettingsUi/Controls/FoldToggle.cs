using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace EveMultiPreview.SettingsUi;

/// <summary>The header bar of a panel that folds (Capsule's section): a toggle whose checked state is "open". A screen
/// reader is told it expands and collapses (expanded / collapsed, the section's title as its name), not that it is a
/// switch that is on or off. While a row of the section shows an error it says so as its status, so a folded section's
/// error is heard, not only seen (IDEAS P105, plan S5).</summary>
public sealed class FoldToggle : ToggleButton
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(nameof(HasError), typeof(bool),
        typeof(FoldToggle), new PropertyMetadata(false, (d, e) => ((FoldToggle)d).ErrorChanged((bool)e.OldValue, (bool)e.NewValue)));

    /// <summary>Whether a row of the section shows an error (SectionVM.HasError).</summary>
    public bool HasError { get => (bool)GetValue(HasErrorProperty); set => SetValue(HasErrorProperty, value); }

    /// <summary>What a screen reader is given as the section's status: the words of L.S.Section.HasError while it holds an
    /// error, else nothing.</summary>
    public string Status => HasError ? Words : "";

    private string Words => TryFindResource("L.S.Section.HasError") as string ?? "";

    private void ErrorChanged(bool was, bool now) =>
        (UIElementAutomationPeer.FromElement(this) as Peer)?.RaisePropertyChangedEvent(
            AutomationElementIdentifiers.ItemStatusProperty, was ? Words : "", now ? Words : "");

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

        protected override string GetItemStatusCore() => _owner.Status;

        public ExpandCollapseState ExpandCollapseState => _owner.IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() => _owner.IsChecked = true;
        public void Collapse() => _owner.IsChecked = false;
    }
}
