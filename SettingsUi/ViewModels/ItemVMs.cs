using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using EveMultiPreview.Services;
using FrameworkElement = System.Windows.FrameworkElement;

namespace EveMultiPreview.SettingsUi;

/// <summary>Editors that hold their own state reload through this when the page refreshes.</summary>
public interface IRefreshable { void Refresh(); }

public abstract class ItemVM : ObservableObject
{
    private bool _highlighted;

    protected ItemVM(string id, SettingsContext ctx) { Id = id; Ctx = ctx; }

    public string Id { get; }
    protected SettingsContext Ctx { get; }
    public abstract bool IsVisible { get; }
    public virtual string? LabelKey => null;
    public virtual string? DescKey => null;
    public virtual string? Error => null;
    public bool HasError => Error != null;

    /// <summary>Search highlights the item it navigated to (spec §6).</summary>
    public bool IsHighlighted { get => _highlighted; set { _highlighted = value; Raise(); } }

    /// <summary>Raised after this item changed settings, so the page re-checks every ShowIf.</summary>
    public event Action? Changed;
    protected void NotifyChanged() => Changed?.Invoke();

    /// <summary>Re-read everything from settings (profile switch, reload, language change).</summary>
    public virtual void Refresh() => RaiseAll();
    public void RaiseVisibility() => Raise(nameof(IsVisible));
}

/// <summary>One generic setting card (spec §6 card types).</summary>
public sealed class SettingVM : ItemVM
{
    private string? _draft;
    private string? _error;

    public SettingVM(SettingDescriptor d, SettingsContext ctx) : base(d.Id, ctx)
    {
        D = d;
        CaptureHotkeyCommand = new RelayCommand(_ => CaptureHotkey());
        ClearCommand = new RelayCommand(_ => Commit(""));
        BrowseCommand = new RelayCommand(_ => Browse());
        PickColorCommand = new RelayCommand(_ => PickColor());
    }

    public SettingDescriptor D { get; }
    public SettingKind Kind => D.Kind;
    public override string LabelKey => D.LabelKey;
    public override string DescKey => D.DescKey;
    public override string? Error => _error;
    public override bool IsVisible => D.ShowIf?.Invoke(Ctx.S) ?? true;
    public string? UnitKey => D.UnitKey;
    public double Min => D.Min;
    public double Max => D.Max;
    public double Step => D.Step;
    public IReadOnlyList<ChoiceOption> Options => D.Options;

    public ICommand CaptureHotkeyCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand PickColorCommand { get; }

    public bool IsOn { get => D.Get(Ctx.S) is true; set => Commit(value); }

    public double Number
    {
        get { try { return Convert.ToDouble(D.Get(Ctx.S), CultureInfo.InvariantCulture); } catch { return 0; } }
        set => Commit(value);
    }

    /// <summary>Number, Text, Folder, File and Color cards as text. A rejected entry stays visible beside its error.</summary>
    public string Text
    {
        get => _draft ?? Format(D.Get(Ctx.S));
        set
        {
            _draft = value;
            if (D.Kind == SettingKind.Number)
            {
                if (!TryParseNumber(value, out var n)) { ShowError(Setting.ErrNotANumber); return; }
                Commit(n);
            }
            else Commit(value);
        }
    }

    public ChoiceOption? Selected
    {
        get { var current = D.Get(Ctx.S); return D.Options.FirstOrDefault(o => Same(o.Value, current)); }
        set { if (value != null) Commit(value.Value); }
    }

    public bool HasValue => !string.IsNullOrEmpty(D.Get(Ctx.S) as string);

    public string HotkeyDisplay
    {
        get { var text = HotkeyText.ToDisplay(D.Get(Ctx.S) as string); return text.Length > 0 ? text : SettingsContext.Str("L.S.Hotkey.None"); }
    }

    /// <summary>Validate, store, request the descriptor's apply scope, run OnChanged (spec §6 Saving).</summary>
    public void Commit(object? value)
    {
        var errorKey = D.Validate?.Invoke(value);
        if (errorKey != null) { ShowError(errorKey); return; }
        var current = D.Get(Ctx.S);
        // A colour typed with spaces round it is the same colour: storing it again would only save for nothing.
        var unchanged = Same(current, value) || (D.Kind == SettingKind.Color && value is string typed && Same(current, typed.Trim()));
        _draft = null;
        _error = null;
        if (!unchanged)
        {
            D.Set(Ctx.S, value);
            Ctx.Applier.Request(D.Apply);
            D.OnChanged?.Invoke(Ctx);
        }
        RaiseAll();
        if (!unchanged) NotifyChanged();
    }

    public override void Refresh()
    {
        _draft = null;
        _error = null;
        RaiseAll();
    }

    private void ShowError(string key)
    {
        _error = string.Format(CultureInfo.CurrentCulture, SettingsContext.Str(key), D.Min, D.Max);
        RaiseAll();
    }

    private void CaptureHotkey()
    {
        var result = Ctx.Host.CaptureHotkey(HotkeyUses.IdOf(D), D.Get(Ctx.S) as string);
        if (result != null) Commit(result);
    }

    private void Browse()
    {
        var current = D.Get(Ctx.S) as string;
        var picked = D.Kind == SettingKind.Folder ? Ctx.Host.PickFolder(current) : Ctx.Host.PickFile(current, D.FileFilter);
        if (picked != null) Commit(picked);
    }

    private void PickColor()
    {
        var picked = Ctx.Host.PickColor(D.Get(Ctx.S) as string);
        if (picked != null) Commit(picked);
    }

    private static string Format(object? v) => Convert.ToString(v, CultureInfo.CurrentCulture) ?? "";

    private static bool Same(object? a, object? b) =>
        a is Enum || b is Enum
            ? Convert.ToInt64(a, CultureInfo.InvariantCulture) == Convert.ToInt64(b, CultureInfo.InvariantCulture)
            : Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture);

    private static bool TryParseNumber(string? s, out double n)
    {
        s = s?.Trim() ?? "";
        var current = ThumbnailFields.Grouped(s, CultureInfo.CurrentCulture, out var currentStyle);
        var invariant = ThumbnailFields.InvariantGrouped(s, out var invariantStyle);
        return double.TryParse(current, NumberStyles.Float | currentStyle, CultureInfo.CurrentCulture, out n)
            || double.TryParse(invariant, NumberStyles.Float | invariantStyle, CultureInfo.InvariantCulture, out n);
    }
}

public sealed class ActionRowVM : ItemVM
{
    public ActionRowVM(ActionRow row, SettingsContext ctx) : base(row.Id, ctx)
    {
        Row = row;
        Buttons = row.Actions.Select(a => new ActionVM(a, ctx, this)).ToList();
    }

    public ActionRow Row { get; }
    public IReadOnlyList<ActionVM> Buttons { get; }
    public override string LabelKey => Row.LabelKey;
    public override string DescKey => Row.DescKey;
    public override bool IsVisible => Row.ShowIf?.Invoke(Ctx.S) ?? true;
    internal void AfterRun() { RaiseAll(); NotifyChanged(); }
}

public sealed class ActionVM
{
    private readonly SettingsContext _ctx;
    private readonly ActionRowVM _row;

    public ActionVM(ActionDef def, SettingsContext ctx, ActionRowVM row)
    {
        Def = def;
        _ctx = ctx;
        _row = row;
        Command = new RelayCommand(_ => Run());
    }

    public ActionDef Def { get; }
    public string LabelKey => Def.LabelKey;
    public bool IsDestructive => Def.Destructive;
    public ICommand Command { get; }

    /// <summary>Destructive and confirm-flagged actions ask first (spec §6).</summary>
    public void Run()
    {
        if (Def.Confirm && !_ctx.Host.Confirm(SettingsContext.Str(Def.LabelKey), SettingsContext.Str(Def.ConfirmKey))) return;
        Def.Run(_ctx);
        _row.AfterRun();
    }
}

public sealed class InfoVM : ItemVM
{
    public InfoVM(InfoItem def, SettingsContext ctx) : base(def.Id, ctx) => Def = def;
    public InfoItem Def { get; }
    public InfoSeverity Severity => Def.Severity;
    public override bool IsVisible => Def.ShowIf?.Invoke(Ctx.S) ?? true;
    public string Text => Def.Args == null
        ? SettingsContext.Str(Def.TextKey)
        : string.Format(CultureInfo.CurrentCulture, SettingsContext.Str(Def.TextKey), Def.Args(Ctx));
}

public sealed class EditorVM : ItemVM
{
    private FrameworkElement? _element;
    public EditorVM(EditorItem def, SettingsContext ctx) : base(def.Id, ctx) => Def = def;
    public EditorItem Def { get; }
    public override bool IsVisible => Def.ShowIf?.Invoke(Ctx.S) ?? true;
    public FrameworkElement Element => _element ??= Def.Create(Ctx);
    public override void Refresh() { (_element as IRefreshable)?.Refresh(); RaiseAll(); }
}
