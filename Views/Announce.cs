using System.Windows;
using System.Windows.Automation.Peers;

namespace EveMultiPreview.Views;

/// <summary>Tells a screen reader that a live region changed. An element with AutomationProperties.LiveSetting set
/// (Polite or Assertive) is a live region, but WPF announces nothing by itself when its text changes: the element's
/// automation peer has to raise LiveRegionChanged. Call <see cref="Changed"/> right after the new text is set, each
/// time it is set (also when the same text is said again).</summary>
public static class Announce
{
    /// <summary>Raises LiveRegionChanged on <paramref name="element"/>'s automation peer, making the peer when the
    /// element has none yet. Does nothing for null and for an element WPF makes no peer for. Costs next to nothing when
    /// no screen reader runs: the event goes nowhere then.</summary>
    public static void Changed(UIElement? element)
    {
        if (element == null) return;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
