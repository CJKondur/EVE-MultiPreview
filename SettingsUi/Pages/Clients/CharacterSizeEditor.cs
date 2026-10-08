using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

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

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
        || int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

/// <summary>Character page › Own size: width × height and Apply, with an inline error (spec §8.5).</summary>
public sealed class CharacterSizeEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly string _name;
    private readonly TextBox _width = new() { MinWidth = 64, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _height = new() { MinWidth = 64, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _apply;
    private readonly TextBlock _message = new();

    public CharacterSizeEditor(SettingsContext ctx, string name)
    {
        _ctx = ctx;
        _name = name;
        _apply = EditorCard.Button("L.S.Act.character.applySize", Apply);
        _message.SetResourceReference(StyleProperty, "Fx.Error");
        Loc.SetNameKey(_width, "L.S.Slot.W");
        Loc.SetNameKey(_height, "L.S.Slot.H");
        Content = EditorCard.Build("character.size",
            EditorCard.Row(_width, EditorCard.Value("×"), _height, EditorCard.Note("L.S.Unit.Px"), _apply), _message);
        Refresh();
    }

    public void Refresh()
    {
        var (w, h) = CharacterSize.Current(_ctx, _name);
        _width.Text = w.ToString(CultureInfo.CurrentCulture);
        _height.Text = h.ToString(CultureInfo.CurrentCulture);
        var can = CharacterSize.CanApply(_ctx, _name);
        _width.IsEnabled = _height.IsEnabled = _apply.IsEnabled = can;
        Show(can ? null : SettingsContext.Str("L.S.character.size.NeedsLogin"));
    }

    private void Apply()
    {
        var error = CharacterSize.Apply(_ctx, _name, _width.Text, _height.Text);
        if (error == null) { Refresh(); return; }
        Show(string.Format(CultureInfo.CurrentCulture, SettingsContext.Str(error), CharacterSize.Min, CharacterSize.Max));
    }

    private void Show(string? message)
    {
        _message.Text = message ?? "";
        _message.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
