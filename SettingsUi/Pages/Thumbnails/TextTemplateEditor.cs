using System;
using System.Windows;
using System.Windows.Input;
using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Thumbnails › Text &amp; borders › Text on thumbnails › Text template (IDEAS P2): a box for the template that builds
/// the text on a thumbnail ("{name} · {system} · {timer} · {label} · {jumps} · {week}"), and a list of four examples that fill it. Empty keeps
/// the name, label, system and timer as separate lines. Writes only ThumbnailTextTemplate, kept to the length a template may
/// have (<see cref="TextTemplate.Clean"/>), when the box loses focus or Enter is pressed and when an example is picked.</summary>
public sealed class TextTemplateEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = EditorIds.TextTemplateEditor;

    /// <summary>The examples in the list: template text, not words, so they are the same in every language.</summary>
    public static readonly string[] ExampleTemplates = { "{name} · {system}", "{label} {name} {timer}", "{name}", "{name} · {system} · {jumps}" };

    private readonly SettingsContext _ctx;
    private bool _loading;

    public TextTemplateEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Box.MaxLength = TextTemplate.MaxLength;
        Loc.SetNameKey(Box, "L.S." + ItemId);
        Loc.SetNameKey(ExamplesList, "L.S.Opt.TemplateExamples");
        ExamplesList.Items.Add(SettingsContext.Str("L.S.Opt.TemplateExamples"));
        foreach (var example in ExampleTemplates) ExamplesList.Items.Add(example);
        ExamplesList.SelectedIndex = 0;

        Box.LostFocus += (_, _) => Commit();
        Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        ExamplesList.SelectionChanged += (_, _) =>
        {
            if (_loading || ExamplesList.SelectedIndex <= 0) return;
            Box.Text = ExampleTemplates[ExamplesList.SelectedIndex - 1];
            Commit();
            _loading = true;
            ExamplesList.SelectedIndex = 0;   // back to the heading: the list is a way to fill the box, not a state
            _loading = false;
        };
        Content = EditorCard.Build(ItemId, ExamplesList, Box);
        Refresh();
    }

    public TextBox Box { get; } = new() { VerticalContentAlignment = VerticalAlignment.Center };

    /// <summary>The list of examples; its first entry is a heading that picks nothing.</summary>
    public ComboBox ExamplesList { get; } = new() { MinWidth = 220 };

    public void Refresh()
    {
        _loading = true;
        Box.Text = _ctx.S.ThumbnailTextTemplate ?? "";
        _loading = false;
    }

    private void Commit()
    {
        if (_loading) return;
        string text = TextTemplate.Clean(Box.Text);
        if (text != Box.Text) Box.Text = text;
        if (text == (_ctx.S.ThumbnailTextTemplate ?? "")) return;
        _ctx.S.ThumbnailTextTemplate = text;
        _ctx.Applier.Request(ApplyScope.Thumbnails);   // every thumbnail composes its text again
    }
}
