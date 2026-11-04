using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › Profiles &amp; backups › Characters that pick this profile (IDEAS #11): the current profile's
/// characters for "Switch profiles by who is logged in", as chips with ✕, a character picker and "add the logged-in
/// ones". It writes only the current profile's AutoSelectCharacters, and saves; App reads it on its half-second timer.</summary>
public sealed class AutoProfileEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = EditorIds.AutoProfileEditor;
    private readonly SettingsContext _ctx;
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _none = EditorCard.Note("L.S.AutoProfile.None");

    public AutoProfileEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var buttons = EditorCard.Row(EditorCard.Button("L.S.AutoProfile.Add", Add), EditorCard.Button("L.S.AutoProfile.AddRunning", AddRunning));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        var below = new StackPanel();
        below.Children.Add(_chips);
        below.Children.Add(_none);
        below.Children.Add(buttons);
        Content = EditorCard.Build(ItemId, null, below);
        Refresh();
    }

    /// <summary>The current profile's characters, as the chips show them.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    public void Refresh()
    {
        Names = AutoProfile.Characters(CharacterDirectory.Current(_ctx.S).AutoSelectCharacters);
        _chips.Children.Clear();
        foreach (var name in Names)
        {
            var chip = EditorCard.Chip(name, () => Remove(name));
            chip.Margin = new Thickness(0, 0, 6, 6);
            _chips.Children.Add(chip);
        }
        _none.Visibility = Names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A character from the picker (running ones first, then every name MultiPreview knows, or a typed one).</summary>
    public void Add()
    {
        var name = _ctx.Host.PickCharacter(SettingsContext.Str("L.S.AutoProfile.PickTitle"),
            CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null) return;
        if (!AutoProfile.IsUsableName(name)) { _ctx.Host.Toast(SettingsContext.Str("L.S.AutoProfile.BadName")); return; }
        Write(AutoProfile.With(CharacterDirectory.Current(_ctx.S).AutoSelectCharacters, name));
    }

    /// <summary>Every character logged in now.</summary>
    public void AddRunning()
    {
        var stored = CharacterDirectory.Current(_ctx.S).AutoSelectCharacters;
        foreach (var name in CharacterDirectory.Running(_ctx).Where(AutoProfile.IsUsableName)) stored = AutoProfile.With(stored, name);
        Write(stored);
    }

    public void Remove(string name) => Write(AutoProfile.Without(CharacterDirectory.Current(_ctx.S).AutoSelectCharacters, name));

    private void Write(string stored)
    {
        var profile = CharacterDirectory.Current(_ctx.S);
        if (profile.AutoSelectCharacters != stored)
        {
            profile.AutoSelectCharacters = stored;
            _ctx.Applier.Request(ApplyScope.Save);
        }
        Refresh();
    }
}
