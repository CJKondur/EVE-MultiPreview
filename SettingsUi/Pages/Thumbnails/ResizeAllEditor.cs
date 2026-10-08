using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>"Resize every thumbnail": the old Apply to all (TxtResizeAllW/H, OnApplySizeToAll).</summary>
public static class ResizeAll
{
    /// <summary>Returns the L.* key of an error ({0}/{1} = the limits), or null when the size was applied.
    /// With thumbnails running, ThumbnailManager.ApplySizeToAll resizes each one where it is, makes it the
    /// size new ones start at, saves and keeps one undo step; without, only the starting size is stored.</summary>
    public static string? Apply(SettingsContext ctx, string widthText, string heightText)
    {
        if (!ThumbnailFields.TryWhole(widthText, out var w) || !ThumbnailFields.TryWhole(heightText, out var h)) return Setting.ErrNotANumber;
        if (w < CharacterSize.Min || w > CharacterSize.Max || h < CharacterSize.Min || h > CharacterSize.Max) return Setting.ErrRange;
        if (ctx.Services.Thumbnails is { } tm)
        {
            tm.ApplySizeToAll(w, h);
            return null;
        }
        ctx.S.ThumbnailStartLocation.Width = w;
        ctx.S.ThumbnailStartLocation.Height = h;
        ctx.Applier.Request(ApplyScope.Save);
        return null;
    }
}

/// <summary>Thumbnails › Appearance › Resize every thumbnail: width × height, Apply to all, and an
/// inline error (spec §8.5).</summary>
public sealed class ResizeAllEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly TextBox _width = new() { MinWidth = 64, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _height = new() { MinWidth = 64, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new();

    public ResizeAllEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        AutomationProperties.SetName(_width, SettingsContext.Str("L.S.Slot.W"));
        AutomationProperties.SetName(_height, SettingsContext.Str("L.S.Slot.H"));
        _message.SetResourceReference(StyleProperty, "Fx.Error");
        Content = EditorCard.Build("thumbs.appearance.resizeAll",
            EditorCard.Row(_width, EditorCard.Value("×"), _height, EditorCard.Note("L.S.Unit.Px"),
                EditorCard.Button("L.S.Act.thumbs.appearance.applySize", Apply)),
            _message);
        Refresh();
    }

    public string WidthText => _width.Text;
    public string HeightText => _height.Text;

    /// <summary>Shows the size new thumbnails start at, which Apply to all last set.</summary>
    public void Refresh()
    {
        var start = _ctx.S.ThumbnailStartLocation;
        _width.Text = ((int)start.Width).ToString(CultureInfo.CurrentCulture);
        _height.Text = ((int)start.Height).ToString(CultureInfo.CurrentCulture);
        Show(null);
    }

    private void Apply()
    {
        var error = ResizeAll.Apply(_ctx, _width.Text, _height.Text);
        if (error == null) { Refresh(); return; }
        Show(string.Format(CultureInfo.CurrentCulture, SettingsContext.Str(error), CharacterSize.Min, CharacterSize.Max));
    }

    private void Show(string? message)
    {
        _message.Text = message ?? "";
        _message.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
