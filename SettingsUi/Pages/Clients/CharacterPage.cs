namespace EveMultiPreview.SettingsUi;

/// <summary>The per-character page (spec §8.4), reached as "character/&lt;name&gt;".</summary>
public static class CharacterPage
{
    public const string Prefix = "character/";

    public static string IdFor(string name) => Prefix + name;
}
