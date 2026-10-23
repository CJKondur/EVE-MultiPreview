using System;

namespace EveMultiPreview.Services;

/// <summary>The part of the registry MultiPreview writes for Windows notifications (IDEAS #33), as far as this
/// class needs it: values of keys below HKEY_CURRENT_USER. The app's is the real registry; a test's is a
/// dictionary.</summary>
public interface IUserRegistry
{
    bool Exists(string key);
    /// <summary>A text value, or null when the key or the value is not there.</summary>
    string? Read(string key, string name);
    /// <summary>Writes a text value; the key is made if it is not there.</summary>
    void Write(string key, string name, string value);
    /// <summary>Removes a key with everything in it. A key that is not there: nothing.</summary>
    void Delete(string key);
    /// <summary>Removes one value of a key and leaves the key and its other values. A key or value that is not there:
    /// nothing. The clean uninstall (IDEAS P16) is its only caller.</summary>
    void DeleteValue(string key, string name);
}

/// <summary>What Windows needs to know of MultiPreview before it shows a notification of a program that has no
/// installer (IDEAS #33): one key of the current user,
/// HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts, with a name and an icon. It is the one
/// thing this feature writes outside the settings folder, and <see cref="Follow"/> is the one place that writes it:
/// while Windows notifications are on the key is there, and while they are off it is not. No activator and no
/// protocol is registered, so a click on a notification that outlived MultiPreview starts nothing.</summary>
public sealed class NotificationRegistration
{
    /// <summary>The identity Windows shows MultiPreview's notifications under.</summary>
    public const string AppId = "EveMultiPreview.Alerts";

    /// <summary>The key, below HKEY_CURRENT_USER.</summary>
    public const string Key = @"Software\Classes\AppUserModelId\" + AppId;

    /// <summary>The name Windows shows above a notification and in its own notification settings.</summary>
    public const string DisplayName = "EVE MultiPreview";

    private readonly IUserRegistry _registry;
    private readonly Func<string?> _icon;
    private readonly Action<Exception> _failed;
    private readonly Action? _removeIcon;
    private bool _knownGone;

    /// <param name="icon">The path of the icon file, made if it is not there; null for none. Asked only while the
    /// feature is on.</param>
    /// <param name="failed">Is told what the registry threw (App logs it).</param>
    /// <param name="removeIcon">Takes the icon file away (IDEAS P86): asked once the key is gone, each time the feature is
    /// switched off (and once at a start with it off, for a file an earlier version left).</param>
    public NotificationRegistration(IUserRegistry registry, Func<string?> icon, Action<Exception>? failed = null, Action? removeIcon = null)
    {
        _registry = registry;
        _icon = icon;
        _failed = failed ?? (_ => { });
        _removeIcon = removeIcon;
    }

    /// <summary>The key is there, as far as this session knows.</summary>
    public bool Registered { get; private set; }

    /// <summary>Makes the registry say what the switch says, and answers whether the key is there now. On: the
    /// key's two values are written where they differ from what is there. Off: the key is removed if it is there;
    /// once it is known to be gone, the registry is not asked again, and the icon file is taken away with the key
    /// (once; a failure is reported and tried again at the next "off"). Nothing leaves here as an exception.</summary>
    public bool Follow(bool on)
    {
        try
        {
            if (on)
            {
                // Before the first write, not behind the last: a write that is refused half-way leaves a key with
                // one value, and "known to be gone" would then keep the next "off" from removing it.
                _knownGone = false;
                if (_registry.Read(Key, "DisplayName") != DisplayName) _registry.Write(Key, "DisplayName", DisplayName);
                if (_icon() is { Length: > 0 } icon && _registry.Read(Key, "IconUri") != icon) _registry.Write(Key, "IconUri", icon);
                return Registered = true;
            }
            if (!_knownGone)
            {
                if (_registry.Exists(Key)) _registry.Delete(Key);
                _removeIcon?.Invoke();
                _knownGone = true;
            }
            return Registered = false;
        }
        catch (Exception ex)
        {
            // By its type and code, never the exception itself (FY-m6): its message names the key's path.
            try { _failed(AlertWays.Failure("Windows notifications: the registry key", ex)); } catch (Exception) { /* reporting must never cost the settings change */ }
            return Registered = false;
        }
    }
}
