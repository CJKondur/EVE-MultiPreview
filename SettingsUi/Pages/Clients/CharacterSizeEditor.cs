using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

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
