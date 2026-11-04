using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Own size rules (replaces "Active Character Sizing" and "Resize one character").</summary>
public static class CharacterSize
{
    public const int Min = 50, Max = 4000;

    /// <summary>The saved size, else the default size every new thumbnail gets.</summary>
    public static (int W, int H) Current(SettingsContext ctx, string name)
    {
        var rect = ctx.Service.GetThumbnailPosition(name);
        var start = ctx.S.ThumbnailStartLocation;
        return rect != null && rect.Width > 0 && rect.Height > 0
            ? ((int)rect.Width, (int)rect.Height)
            : ((int)start.Width, (int)start.Height);
    }

    public static bool IsRunning(SettingsContext ctx, string name) => ctx.Services.Thumbnails?.GetThumbnailForCharacter(name) != null;

    /// <summary>Running, or a saved position to keep: a new rect without a real X/Y would put the
    /// thumbnail in the screen corner on its next login.</summary>
    public static bool CanApply(SettingsContext ctx, string name) => IsRunning(ctx, name) || ctx.Service.GetThumbnailPosition(name) != null;

    /// <summary>Returns the L.* key of an error, or null when the size was applied.</summary>
    public static string? Apply(SettingsContext ctx, string name, string wText, string hText)
    {
        if (!TryInt(wText, out var w) || !TryInt(hText, out var h)) return Setting.ErrNotANumber;
        if (w < Min || w > Max || h < Min || h > Max) return Setting.ErrRange;
        if (IsRunning(ctx, name))
        {
            ctx.Services.Thumbnails!.ApplySizeToCharacter(name, w, h);   // resizes, keeps its place, saves
            return null;
        }
        var rect = ctx.Service.GetThumbnailPosition(name);
        if (rect == null) return "L.S.character.size.NeedsLogin";
        rect.Width = w;
        rect.Height = h;
        ctx.Applier.Request(ApplyScope.Save);
        return null;
    }

    private static bool TryInt(string text, out int value) => ThumbnailFields.TryWhole(text, out value);
}
