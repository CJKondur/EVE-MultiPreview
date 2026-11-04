using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S3b: the editors of App › Profiles &amp; backups, as the Windows ProfileEditor and AutoProfileEditor draw them. The
/// work is the shared ProfileActions / AutoProfile; the dialogs it opens are the Linux window's (ProfileActions.Linux.cs).</summary>
internal sealed partial class SettingsPages
{
    private static string S3(string key) => SettingsContext.Str(key);

    /// <summary>A button with a catalog label (access key and leading emoji left out) that runs <paramref name="click"/>.</summary>
    internal static Button Action(string key, Action click, bool destructive = false)
    {
        var b = new Button { Content = new TextBlock { Text = Ui.Plain(Ui.NoAccess(S3(key))) }, Margin = new Thickness(0, 2, 6, 2) };
        if (destructive) b.Foreground = Brushes.IndianRed;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A card's own title and description, as the Windows EditorCard.Build draws them for an editor id.</summary>
    internal static StackPanel Titled(string itemId)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Ui.NoAccess(S3(LinuxSupport.Key("L.S." + itemId))), TextWrapping = TextWrapping.Wrap });
        if (LocalizationService.Str(LinuxSupport.Key("L.S." + itemId + ".Desc"), "") is { Length: > 0 } desc)
            panel.Children.Add(new TextBlock { Text = desc, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static WrapPanel Buttons(params Control[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>App › Profiles › Current profile (the Windows ProfileEditor): the profile in use, picked from the list; New,
    /// Copy to other profiles, Compare, Delete; and the look and profile codes.</summary>
    internal Control ProfilePicker(SettingsContext ctx)
    {
        var panel = Titled("app.profiles.current");
        var names = ctx.Service.GetProfileNames();
        var list = new ComboBox { MinWidth = 200, MaxWidth = 360 };
        foreach (var name in names) list.Items.Add(new TextBlock { Text = name });
        list.SelectedIndex = Array.IndexOf(names, ctx.S.LastUsedProfile);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < names.Length && names[list.SelectedIndex] != ctx.S.LastUsedProfile)
                ProfileActions.Switch(ctx, names[list.SelectedIndex]);   // ProfileSwitched draws the page again
        };
        panel.Children.Add(list);
        Func<string, bool> copy = text => ctx.Host is SharedSettingsWindow w && w.CopyText(text);
        panel.Children.Add(Buttons(
            Action("L.S.Profile.New", () => ProfileActions.NewInteractive(ctx)),
            Action("L.S.Profile.Copy", () => ProfileActions.CopyInteractive(ctx)),
            Action("L.S.Profile.Compare", () => ProfileActions.CompareInteractive(ctx)),
            Action("L.S.Profile.Delete", () => ProfileActions.DeleteInteractive(ctx, ctx.S.LastUsedProfile), destructive: true)));
        panel.Children.Add(Buttons(
            Action("L.S.Profile.CopyLookCode", () => ProfileActions.CopyLookCode(ctx, copy)),
            Action("L.S.Profile.CopyProfileCode", () => ProfileActions.CopyProfileCode(ctx, copy)),
            Action("L.S.Profile.FromCode", () => ProfileActions.FromCodeInteractive(ctx))));
        return panel;
    }

    /// <summary>App › Profiles › Characters that pick this profile (the Windows AutoProfileEditor): the current profile's
    /// characters with ✕, a character picker and "add the logged-in ones". It writes only the current profile's
    /// AutoSelectCharacters; the app reads it on its half-second timer (Previews.FollowAutoProfile).</summary>
    internal Control AutoProfileCharacters(SettingsContext ctx)
    {
        var panel = Titled(EditorIds.AutoProfileEditor);
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var none = new TextBlock { Text = S3("L.S.AutoProfile.None"), Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
        void Write(string stored)
        {
            var profile = CharacterDirectory.Current(ctx.S);
            if (profile.AutoSelectCharacters != stored)
            {
                profile.AutoSelectCharacters = stored;
                ctx.Applier.Request(ApplyScope.Save);
            }
            Show();
        }
        void Show()
        {
            chips.Children.Clear();
            var names = AutoProfile.Characters(CharacterDirectory.Current(ctx.S).AutoSelectCharacters);
            foreach (var name in names) chips.Children.Add(Chip(name, () => Write(AutoProfile.Without(CharacterDirectory.Current(ctx.S).AutoSelectCharacters, name))));
            none.IsVisible = names.Count == 0;
        }
        Show();
        panel.Children.Add(chips);
        panel.Children.Add(none);
        panel.Children.Add(Buttons(
            Action("L.S.AutoProfile.Add", () =>
            {
                var name = ctx.Host.PickCharacter(S3("L.S.AutoProfile.PickTitle"), CharacterDirectory.AllKnown(ctx.S, CharacterDirectory.Running(ctx)));
                if (name == null) return;
                if (!AutoProfile.IsUsableName(name)) { ctx.Host.Toast(S3("L.S.AutoProfile.BadName")); return; }
                Write(AutoProfile.With(CharacterDirectory.Current(ctx.S).AutoSelectCharacters, name));
            }),
            Action("L.S.AutoProfile.AddRunning", () =>
            {
                var stored = CharacterDirectory.Current(ctx.S).AutoSelectCharacters;
                foreach (var name in CharacterDirectory.Running(ctx).Where(AutoProfile.IsUsableName)) stored = AutoProfile.With(stored, name);
                Write(stored);
            })));
        return panel;
    }

    /// <summary>Clients › Windows › Never minimize (the Windows NeverMinimizeEditor): the profile's DontMinimizeClients as names
    /// with ✕, Add… and Add all running clients; a name is listed once, whatever its case. Read at every switch that minimizes
    /// the other clients (Previews.ActivateClient).</summary>
    internal Control NeverMinimize(SettingsContext ctx)
    {
        var panel = Titled("clients.windows.neverMinimize");
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        void Show()
        {
            chips.Children.Clear();
            var list = CharacterDirectory.Current(ctx.S).DontMinimizeClients;
            foreach (var name in list.ToList())
                chips.Children.Add(Chip(name, () => { CharacterFields.SetNeverMinimize(ctx.S, name, false); ctx.Applier.Request(ApplyScope.Save); Show(); }));
            if (list.Count == 0) chips.Children.Add(new TextBlock { Text = S3("L.S.clients.windows.neverMinimize.Empty"), Opacity = 0.65 });
        }
        Show();
        panel.Children.Add(Buttons(
            Action("L.S.NeverMinimize.Add", () =>
            {
                var name = ctx.Host.PickCharacter(S3("L.S.NeverMinimize.AddTitle"), CharacterDirectory.AllKnown(ctx.S, CharacterDirectory.Running(ctx)));
                if (name == null) return;
                CharacterFields.SetNeverMinimize(ctx.S, name, true);
                ctx.Applier.Request(ApplyScope.Save);
                Show();
            }),
            Action("L.S.Running.AddAll", () =>
            {
                var running = CharacterDirectory.Running(ctx);
                if (running.Count == 0) { ctx.Host.Toast(S3("L.S.Running.None")); return; }
                var added = running.Count(n => !CharacterFields.NeverMinimize(ctx.S, n));
                if (added == 0) { ctx.Host.Toast(S3("L.S.Running.AllThere")); return; }
                foreach (var n in running) if (!CharacterFields.NeverMinimize(ctx.S, n)) CharacterFields.SetNeverMinimize(ctx.S, n, true);
                ctx.Host.Toast(string.Format(S3("L.S.Running.Added"), added));
                ctx.Applier.Request(ApplyScope.Save);
                Show();
            })));
        panel.Children.Add(chips);
        return panel;
    }

    /// <summary>A name with a ✕ that takes it away (the Windows EditorCard.Chip).</summary>
    internal static Control Chip(string text, Action remove)
    {
        var x = new Button { Content = "✕", Padding = new Thickness(6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        x.Click += (_, _) => remove();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(x);
        return new Border
        {
            Child = row, Padding = new Thickness(10, 2, 2, 2), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#22FFFFFF")),
        };
    }
}
