using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace EveMultiPreview.Views;

/// <summary>A border that is clicked with the mouse (the alert hub's bell, toasts and picker buttons, the quick-switch
/// wheel's cards) and says so to UI Automation: a plain border has no automation peer, so a screen reader never meets
/// it. The peer is a button (name: AutomationProperties.Name, set by whoever builds it) that can be invoked.
/// Nothing about the border's look, hit-testing or keyboard changes.</summary>
public class PressBorder : Border
{
    /// <summary>What a screen reader's "click" does; null: nothing to do now, and the peer offers no Invoke.</summary>
    public Action? Press { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new PressBorderPeer(this);
}

internal sealed class PressBorderPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    private readonly PressBorder _owner;

    public PressBorderPeer(PressBorder owner) : base(owner) => _owner = owner;

    protected override string GetClassNameCore() => nameof(PressBorder);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    public override object? GetPattern(PatternInterface pattern) =>
        pattern == PatternInterface.Invoke && _owner.Press != null ? this : base.GetPattern(pattern);

    /// <summary>Runs later on the window's thread, as a button's does, so the caller of Invoke is not held while it runs.</summary>
    public void Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        if (_owner.Press is { } press) _owner.Dispatcher.BeginInvoke(press);
    }
}
