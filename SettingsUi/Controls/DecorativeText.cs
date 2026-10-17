using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>Text that only decorates (Carbon's red "// " before a section title): drawn, but left out of what a screen
/// reader is given, so the title is read without "slash slash" before it. (WPF has no AccessibilityView property; a peer
/// that is neither a control nor a content element is how an element is left out.)</summary>
public sealed class DecorativeText : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : TextBlockAutomationPeer
    {
        public Peer(TextBlock owner) : base(owner) { }
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
