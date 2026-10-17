using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.SettingsUi;

/// <summary>A navigation group's button where the look draws the group as "+ Name" / "- Name" (Terminal, Ink): the sign shows
/// whether the group is open, so a screen reader is told too (UI Automation's expanded / collapsed, read from the group the
/// button stands for). Read-only: the button opens the group's first page, as it always did, and a group closes when
/// another opens.</summary>
public sealed class NavGroupButton : Button
{
    private bool _announced;

    public NavGroupButton()
    {
        Loaded += (_, _) => Watch(Group, true);
        Unloaded += (_, _) => Watch(Group, false);
        // A new group while loaded (Task 13 review): the old one is let go and the new one watched.
        DataContextChanged += (_, e) =>
        {
            Watch(e.OldValue as NavGroupVM, false);
            if (IsLoaded) Watch(e.NewValue as NavGroupVM, true);
        };
    }

    private NavGroupVM? Group => DataContext as NavGroupVM;

    private void Watch(NavGroupVM? group, bool on)
    {
        if (group == null) return;
        group.PropertyChanged -= OnGroupChanged;
        if (!on) return;
        _announced = group.IsExpanded;
        group.PropertyChanged += OnGroupChanged;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavGroupVM.IsExpanded) || sender is not NavGroupVM group || group.IsExpanded == _announced) return;
        var was = _announced;
        _announced = group.IsExpanded;
        if (UIElementAutomationPeer.FromElement(this) is Peer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, State(was), State(_announced));
    }

    private static ExpandCollapseState State(bool open) => open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : ButtonAutomationPeer, IExpandCollapseProvider
    {
        public Peer(NavGroupButton owner) : base(owner) { }

        public override object? GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.ExpandCollapse ? this : base.GetPattern(pattern);

        public ExpandCollapseState ExpandCollapseState => State(((NavGroupButton)Owner).Group?.IsExpanded == true);

        public void Expand() { if (ExpandCollapseState == ExpandCollapseState.Collapsed) ((IInvokeProvider)this).Invoke(); }

        /// <summary>A group closes when another opens; it cannot be closed by itself. Said as UI Automation says "this cannot
        /// be done" (not ElementNotEnabled: the button is enabled; Task 13 review), never as a silent success.</summary>
        public void Collapse() => throw new InvalidOperationException("A navigation group closes when another one opens.");
    }
}
