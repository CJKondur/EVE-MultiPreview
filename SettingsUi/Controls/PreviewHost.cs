using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace EveMultiPreview.SettingsUi;

/// <summary>The frame of a look's miniature (UI styles spec §9.2): inert. It takes no mouse and no keyboard, nothing in it
/// is a tab stop, and a screen reader does not see into it. The gallery card around it is the control.</summary>
public sealed class PreviewHost : Border
{
    public PreviewHost()
    {
        IsHitTestVisible = false;
        Focusable = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Hidden(this);

    /// <summary>A peer with no children that is neither a control nor content: the miniature is not in the screen
    /// reader's tree.</summary>
    private sealed class Hidden : FrameworkElementAutomationPeer
    {
        public Hidden(FrameworkElement owner) : base(owner) { }
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
