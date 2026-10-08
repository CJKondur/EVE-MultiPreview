using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Factory for SettingDescriptors. The property-path overloads derive Get, Set and
/// Binds from one expression such as <c>s =&gt; s.ThumbnailOpacity</c> or
/// <c>s =&gt; s.ThumbnailTextMargins.X</c>, so a card can't bind one property and write another.</summary>
public static class Setting
{
    public const string ErrNotANumber = "L.S.Err.NotANumber";
    public const string ErrRange = "L.S.Err.Range";
    public const string ErrColor = "L.S.Err.Color";

    public static SettingDescriptor Toggle(string id, Expression<Func<AppSettings, bool>> prop, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Toggle, prop, apply);

    public static SettingDescriptor Choice<T>(string id, Expression<Func<AppSettings, T>> prop, IReadOnlyList<ChoiceOption> options, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Choice, prop, apply) with { Options = options };

    public static SettingDescriptor Segmented<T>(string id, Expression<Func<AppSettings, T>> prop, IReadOnlyList<ChoiceOption> options, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Segmented, prop, apply) with { Options = options };

    public static SettingDescriptor Slider<T>(string id, Expression<Func<AppSettings, T>> prop, double min, double max, double step = 1, string? unitKey = null, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Slider, prop, apply) with { Min = min, Max = max, Step = step, UnitKey = unitKey };

    public static SettingDescriptor Number<T>(string id, Expression<Func<AppSettings, T>> prop, double min, double max, string? unitKey = null, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Number, prop, apply) with { Min = min, Max = max, UnitKey = unitKey, Validate = v => RangeError(v, min, max) };

    public static SettingDescriptor Text(string id, Expression<Func<AppSettings, string>> prop, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Text, prop, apply);

    public static SettingDescriptor Folder(string id, Expression<Func<AppSettings, string>> prop, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.Folder, prop, apply);

    public static SettingDescriptor File(string id, Expression<Func<AppSettings, string>> prop, ApplyScope apply = ApplyScope.Save) =>
        Make(id, SettingKind.File, prop, apply);

    public static SettingDescriptor Color(string id, Expression<Func<AppSettings, string>> prop, ApplyScope apply = ApplyScope.Thumbnails) =>
        Make(id, SettingKind.Color, prop, apply) with { Validate = v => IsColor(v as string) ? null : ErrColor };

    /// <summary>Hotkeys apply through App's re-registration on Applied, so Save is the scope.</summary>
    public static SettingDescriptor Hotkey(string id, Expression<Func<AppSettings, string>> prop)
    {
        var d = Make(id, SettingKind.Hotkey, prop, ApplyScope.Save);
        return d with { UseId = d.Binds[0] };
    }

    /// <summary>A setting whose value is not one AppSettings property (registry key, derived value).</summary>
    public static SettingDescriptor Custom(string id, SettingKind kind, Func<AppSettings, object?> get, Action<AppSettings, object?> set,
                                           IReadOnlyList<string> binds, ApplyScope apply = ApplyScope.Save) =>
        new() { Id = id, Kind = kind, Get = get, Set = set, Binds = binds, Apply = apply };

    internal static string? RangeError(object? value, double min, double max)
    {
        if (value == null || !TryDouble(value, out var d)) return ErrNotANumber;
        return d < min || d > max ? ErrRange : null;
    }

    private static bool TryDouble(object value, out double d)
    {
        try { d = Convert.ToDouble(value, CultureInfo.InvariantCulture); return !double.IsNaN(d); }
        catch { d = 0; return false; }
    }

    internal static bool IsColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            var parsed = AppSettings.ParseColor(s, System.Windows.Media.Colors.Transparent);
            return parsed != System.Windows.Media.Colors.Transparent;
        }
        catch { return false; }   // ParseColor throws on rgb(300,0,0)
    }

    private static SettingDescriptor Make<T>(string id, SettingKind kind, Expression<Func<AppSettings, T>> prop, ApplyScope apply)
    {
        var (get, set, bind) = PropertyPath.Compile(prop);
        return new SettingDescriptor { Id = id, Kind = kind, Get = get, Set = set, Binds = new[] { bind }, Apply = apply };
    }
}

/// <summary>Turns s =&gt; s.A or s =&gt; s.A.B into a getter, a setter and the bound name "AppSettings.A".</summary>
internal static class PropertyPath
{
    public static (Func<AppSettings, object?> Get, Action<AppSettings, object?> Set, string Bind) Compile<T>(Expression<Func<AppSettings, T>> expr)
    {
        var chain = new List<PropertyInfo>();
        Expression? e = expr.Body;
        if (e is UnaryExpression { NodeType: ExpressionType.Convert } u) e = u.Operand;
        while (e is MemberExpression { Member: PropertyInfo pi } me)
        {
            chain.Insert(0, pi);
            e = me.Expression;
        }
        if (e is not ParameterExpression || chain.Count == 0 || chain[^1].SetMethod == null)
            throw new ArgumentException("Expected s => s.Property or s => s.Property.Sub with a setter: " + expr);

        var getter = expr.Compile();
        object? Get(AppSettings s) => getter(s);
        void Set(AppSettings s, object? value)
        {
            object target = s;
            for (int i = 0; i < chain.Count - 1; i++) target = chain[i].GetValue(target)!;
            chain[^1].SetValue(target, ConvertTo(value, chain[^1].PropertyType));
        }
        return (Get, Set, "AppSettings." + chain[0].Name);
    }

    public static object? ConvertTo(object? value, Type type)
    {
        if (value == null) return null;
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsInstanceOfType(value)) return value;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        if (t == typeof(int)) return (int)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);
        if (t == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
        return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }
}
