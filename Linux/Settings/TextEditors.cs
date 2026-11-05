using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S8: Thumbnails › Text &amp; borders › Text template, the Avalonia port of TextTemplateEditor (keep in sync): a box
/// for the template and a list of examples that fill it (its first entry a heading that picks nothing). Writes only
/// ThumbnailTextTemplate, cleaned to what a template may hold (TextTemplate.Clean), when the box loses the focus or Enter is
/// pressed and when an example is picked; the app fills it on every thumbnail at the next tick (Previews.TemplateLook).</summary>
internal sealed partial class SettingsEditors
{
    internal Control TextTemplateCard(SettingsContext ctx)
    {
        var panel = Titled(EditorIds.TextTemplateEditor);
        var box = new TextBox { Text = ctx.S.ThumbnailTextTemplate ?? "", MaxLength = TextTemplate.MaxLength, MinWidth = 320 };
        var examples = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        examples.Items.Add(new TextBlock { Text = S3("L.S.Opt.TemplateExamples") });
        foreach (var example in TextTemplateExamples) examples.Items.Add(new TextBlock { Text = example });
        examples.SelectedIndex = 0;
        bool loading = false;

        void Commit()
        {
            if (loading) return;
            string text = TextTemplate.Clean(box.Text);
            if (text != (box.Text ?? "")) box.Text = text;
            if (text == (ctx.S.ThumbnailTextTemplate ?? "")) return;
            ctx.S.ThumbnailTextTemplate = text;
            ctx.Applier.Request(ApplyScope.Thumbnails);   // every thumbnail composes its text again
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
        examples.SelectionChanged += (_, _) =>
        {
            if (loading || examples.SelectedIndex <= 0) return;
            box.Text = TextTemplateExamples[examples.SelectedIndex - 1];
            Commit();
            loading = true;
            examples.SelectedIndex = 0;   // back to the heading: the list is a way to fill the box, not a state
            loading = false;
        };
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        box.Margin = new Thickness(0, 0, 12, 4);
        row.Children.Add(box);
        row.Children.Add(examples);
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>TextTemplateEditor.ExampleTemplates (that editor is WPF, so not compiled here): template text, not words, the
    /// same in every language. A Linux check compares the two.</summary>
    internal static readonly string[] TextTemplateExamples = { "{name} · {system}", "{label} {name} {timer}", "{name}", "{name} · {system} · {jumps}" };
}
