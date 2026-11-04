namespace EveMultiPreview.SettingsUi;

/// <summary>Which answer of a question keeps things as they are (IDEAS P92): the default button, the focused one and the
/// first Tab stop of the dialog. Enter on a question that has just opened takes it; Esc and the X always keep.</summary>
public enum SafeAnswer
{
    /// <summary>Cancel: both answers change something (delete everywhere / only this profile's keys), and an OK/Cancel question.</summary>
    Cancel,
    /// <summary>The first listed option: the answers are a pure choice, not keep versus change.</summary>
    First,
    /// <summary>The second listed option: the answers are "change" and "keep" (Take it / Keep mine).</summary>
    Second,
}
