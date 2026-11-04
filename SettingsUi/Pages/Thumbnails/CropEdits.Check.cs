namespace EveMultiPreview.SettingsUi;

/// <summary>CropEdits' number check: the Windows editor's boxes (Setting's error keys are Windows-only).</summary>
public static partial class CropEdits
{
    /// <summary>A typed whole number in range, or the L.* key of the error ({0}/{1} = the range).</summary>
    public static string? CheckNumber(string? text, int min, int max, out int value)
    {
        if (!ThumbnailFields.TryWhole(text, out value)) return Setting.ErrNotANumber;
        return value < min || value > max ? Setting.ErrRange : null;
    }
}
