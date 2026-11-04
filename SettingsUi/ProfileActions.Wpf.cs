using System.Collections.Generic;

namespace EveMultiPreview.SettingsUi;

/// <summary>Windows only (never in Linux/SharedSources.props): ProfileActions' two WPF dialogs.</summary>
public static partial class ProfileActions
{
    private static partial (string From, string? To, bool Full)? AskCopy(SettingsContext ctx, List<string> names)
    {
        var dialog = new Views.CopyLayoutDialog(names, ctx.S.LastUsedProfile) { Owner = ctx.Host.DialogOwner };
        if (dialog.ShowDialog() != true || dialog.SelectedFrom == null) return null;
        return (dialog.SelectedFrom, dialog.IsAllProfiles ? null : dialog.SelectedTo, dialog.FullProfileCopy);
    }

    public static partial Dictionary<int, string>? AskMapping(SettingsContext ctx, IReadOnlyList<string> codeCharacters, IReadOnlyList<string> local)
    {
        if (ctx.Host.DialogOwner is not { } owner) return null;
        var dialog = new Views.LayoutRemapDialog(owner, codeCharacters, local, profileCode: true);
        return dialog.ShowDialog() == true ? dialog.Mapping : null;
    }
}
