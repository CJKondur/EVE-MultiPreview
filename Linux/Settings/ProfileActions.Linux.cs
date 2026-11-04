using EveMultiPreview.Linux;

namespace EveMultiPreview.SettingsUi;

/// <summary>The Linux side of ProfileActions (its Windows side is SettingsUi/ProfileActions.Wpf.cs): the copy dialog and the
/// character-mapping dialog of a profile code, drawn by the Linux settings window. Without that window (a test's host) both
/// answer "cancelled", as the Windows ones do without a dialog owner.</summary>
public static partial class ProfileActions
{
    private static partial (string From, string? To, bool Full)? AskCopy(SettingsContext ctx, List<string> names) =>
        ctx.Host is SharedSettingsWindow window ? window.AskCopy(names, ctx.S.LastUsedProfile) : null;

    public static partial Dictionary<int, string>? AskMapping(SettingsContext ctx, IReadOnlyList<string> codeCharacters, IReadOnlyList<string> local) =>
        ctx.Host is SharedSettingsWindow window ? window.AskMapping(codeCharacters, local, profileCode: true) : null;
}

/// <summary>The Linux side of CharacterDirectory.Window.cs: the running characters are the Linux app's.</summary>
public static partial class CharacterDirectory
{
    /// <summary>Logged-in characters with a client running, sorted. Empty without the Linux settings window.</summary>
    public static IReadOnlyList<string> Running(SettingsContext ctx) =>
        (ctx.Host is SharedSettingsWindow window ? window.RunningCharacters() : Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}
