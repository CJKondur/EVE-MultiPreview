namespace EveMultiPreview.SettingsUi;

/// <summary>One option of a Choice or Segmented setting. LabelKey is an L.* resource key;
/// Literal, when set, is shown instead (language names, percentages: never translated).</summary>
public sealed record ChoiceOption(object Value, string LabelKey)
{
    public string? Literal { get; init; }
}
