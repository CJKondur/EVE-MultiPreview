using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

/// <summary>The settings window's parts of <see cref="CharacterFields"/>.</summary>
public static partial class CharacterFields
{
    /// <summary>The label editor's live push to the thumbnail; no re-apply needed.</summary>
    public static void PushLabel(SettingsContext ctx, string name)
    {
        var text = Label(ctx.S, name);
        var color = LabelColor(ctx.S, name);
        ctx.Services.Thumbnails?.UpdateCharacterLabel(name, text.Length == 0 ? null : text, color.Length == 0 ? null : color, LabelSize(ctx.S, name));
    }

    public static IReadOnlyList<ChoiceOption> CoreOptions() =>
        new[] { new ChoiceOption(AutoCore, "L.S.Opt.CoreAuto") }
            .Concat(Enumerable.Range(0, Environment.ProcessorCount).Select(i =>
                new ChoiceOption(i, "") { Literal = string.Format(CultureInfo.CurrentCulture, SettingsContext.Str("L.S.Opt.CoreN"), i) }))
            .ToList();
}
