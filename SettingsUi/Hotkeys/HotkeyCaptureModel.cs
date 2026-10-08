using System;
using System.Collections.Generic;
using System.Windows.Input;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The capture dialog's logic, kept free of UI so it can be tested.</summary>
public sealed class HotkeyCaptureModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<HotkeyUse>> _findClashes;

    public HotkeyCaptureModel(string? current, Func<string, IReadOnlyList<HotkeyUse>> findClashes)
    {
        Candidate = current ?? "";
        _findClashes = findClashes;
    }

    public string Candidate { get; private set; }
    public bool HasCandidate => Candidate.Length > 0;
    public string Display => HotkeyText.ToDisplay(Candidate);
    public IReadOnlyList<HotkeyUse> Clashes { get; private set; } = Array.Empty<HotkeyUse>();
    public bool HasClashes => Clashes.Count > 0;
    public bool Cancelled { get; private set; }

    /// <summary>The last key pressed is one HotkeyService can't bind (HotkeyText.IsUsable).</summary>
    public bool Unusable { get; private set; }
    public bool CanAccept => HasCandidate && !Unusable;

    /// <summary>Returns true when the key was consumed (the dialog marks it handled).</summary>
    public bool OnKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Escape) { Cancelled = true; return true; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return true;   // a modifier alone: wait for the real key
        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed or Key.System)
            return true;   // unresolved key: swallow it, never store it
        Take(HotkeyText.ModifierPrefix(mods) + HotkeyText.KeyToAhkName(key));
        return true;
    }

    /// <summary>Middle, X1 and X2 bind; left and right return false so the dialog's buttons still work.</summary>
    public bool OnMouse(MouseButton button, ModifierKeys mods)
    {
        var name = HotkeyText.MouseButtonName(button);
        if (name == null) return false;
        Take(HotkeyText.ModifierPrefix(mods) + name);
        return true;
    }

    private void Take(string ahk)
    {
        Candidate = ahk;
        Unusable = !HotkeyText.IsUsable(ahk);
        Clashes = Unusable ? Array.Empty<HotkeyUse>() : _findClashes(ahk);
        RaiseAll();
    }
}
