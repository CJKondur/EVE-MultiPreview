namespace EveMultiPreview.SettingsUi;

/// <summary>The character page's "Open crop windows": the Crop windows editor's next refresh selects this character.</summary>
public static class CropsRequest
{
    private static string? _requested;

    public static void Request(string character) => _requested = character;

    /// <summary>The requested character, once: it is cleared as it is taken.</summary>
    public static string? Take()
    {
        var character = _requested;
        _requested = null;
        return character;
    }
}
