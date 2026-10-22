using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>IDEAS P17: the last <see cref="Capacity"/> changes the player made to a simple setting (a card of the
/// generic kinds) in this run of the app, newest first, with a guarded undo. Kept in memory only: it lives on the
/// <see cref="SettingsService"/>, which outlives the settings window, so closing and reopening the window keeps the
/// list, and a restart empties it. Nothing is written to the settings file or anywhere else. Hand-built editors
/// (tables, groups, crops, rules) never reach <see cref="Record"/> and are not listed.</summary>
public sealed class SettingChanges
{
    public const int Capacity = 20;
    private readonly Func<DateTime> _now;
    private readonly List<Change> _items = new();

    /// <param name="now">The clock; a test hands one.</param>
    public SettingChanges(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Change> Items => _items;

    /// <summary>One change. <see cref="OldText"/> and <see cref="NewText"/> are what the setting's own card shows for the
    /// two values, in the language in use when they are read. A character-scoped setting is listed by its label alone:
    /// the character is not part of it.</summary>
    public sealed record Change(SettingDescriptor D, Profile? Scope, object? OldValue, object? NewValue, DateTime Time)
    {
        public string Id => D.Id;
        public string LabelKey => D.LabelKey;
        public string OldText => Show(D, OldValue);
        public string NewText => Show(D, NewValue);
    }

    /// <summary>The profile a change belongs to: the current one for a setting a profile holds (a <c>Profile.X</c> bind, or an
    /// <c>AppSettings.X</c> bind that is the forwarder of a property <see cref="Profile"/> also has), null for a global
    /// setting, which means the same in every profile and can be undone from any of them.</summary>
    public static Profile? ScopeOf(SettingDescriptor d, AppSettings s) => d.Binds.Any(bind =>
        bind.StartsWith("Profile.", StringComparison.Ordinal)
        || (bind.StartsWith("AppSettings.", StringComparison.Ordinal) && typeof(Profile).GetProperty(bind["AppSettings.".Length..]) != null))
        ? CharacterDirectory.Current(s) : null;

    /// <summary>Called by <see cref="SettingVM.Commit"/> after the value was stored. <paramref name="newValue"/> is what the
    /// setting reads back now (a slider's 57.4 on an integer setting is 57), so the undo's check compares like with like.
    /// A change that continues the newest one of the same setting (a slider dragged, a number typed digit by digit) is
    /// merged into it, and drops out of the list when it ends where the first one began. Only a slider, a number or a text
    /// is merged: a choice or a toggle changed twice stays two rows, so the player can step back to the middle value.</summary>
    public void Record(SettingDescriptor d, Profile? scope, object? oldValue, object? newValue)
    {
        var time = _now();
        if (_items.Count > 0 && _items[0] is var top && ReferenceEquals(top.D, d) && ReferenceEquals(top.Scope, scope)
            && d.Kind is SettingKind.Slider or SettingKind.Number or SettingKind.Text
            && SettingVM.Same(top.NewValue, oldValue))
        {
            if (SettingVM.Same(top.OldValue, newValue)) _items.RemoveAt(0);
            else _items[0] = top with { NewValue = newValue, Time = time };
            return;
        }
        _items.Insert(0, new Change(d, scope, oldValue, newValue, time));
        if (_items.Count > Capacity) _items.RemoveAt(Capacity);
    }

    /// <summary>Puts the old value back through the same steps a card's change takes: store, request the apply scope, run
    /// OnChanged. Refused (false, nothing touched) unless the setting still holds exactly the value this change gave it,
    /// (for a setting a profile holds: in the profile it was made in): a value the player changed again since is never overwritten. The undone change leaves
    /// the list and the undo itself is not recorded, so an older change of the same setting can be undone after it.</summary>
    public bool Undo(Change change, SettingsContext ctx)
    {
        if (!_items.Any(i => ReferenceEquals(i, change))) return false;
        if (change.Scope != null && !ReferenceEquals(CharacterDirectory.Current(ctx.S), change.Scope)) return false;
        if (!SettingVM.Same(change.D.Get(ctx.S), change.NewValue)) return false;
        change.D.Set(ctx.S, change.OldValue);
        ctx.Applier.Request(change.D.Apply);
        _items.RemoveAll(i => ReferenceEquals(i, change));
        change.D.OnChanged?.Invoke(ctx);
        return true;
    }

    /// <summary>A value as the setting's card shows it.</summary>
    private static string Show(SettingDescriptor d, object? v)
    {
        switch (d.Kind)
        {
            case SettingKind.Toggle:
                return SettingsContext.Str(v is true ? "L.S.On" : "L.S.Off");
            case SettingKind.Choice:
            case SettingKind.Segmented:
                var option = d.Options.FirstOrDefault(o => SettingVM.Same(o.Value, v));
                if (option != null) return option.Literal ?? SettingsContext.Str(option.LabelKey);
                break;
            case SettingKind.Hotkey:
                var key = HotkeyText.ToDisplay(v as string);
                return key.Length > 0 ? key : SettingsContext.Str("L.S.Hotkey.None");
            case SettingKind.Slider:
            case SettingKind.Number:
                var number = Convert.ToString(v, CultureInfo.CurrentCulture) ?? "";
                if (d.UnitKey == null) return number;
                var unit = SettingsContext.Str(d.UnitKey);
                return number + (unit == "%" ? "" : " ") + unit;
        }
        var text = Convert.ToString(v, CultureInfo.CurrentCulture) ?? "";
        return text.Length > 0 ? text : SettingsContext.Str("L.S.Changes.Empty");
    }
}
