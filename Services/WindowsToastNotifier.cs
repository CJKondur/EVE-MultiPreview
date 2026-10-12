using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace EveMultiPreview.Services;

/// <summary>What shows a notification (IDEAS #33). The app's is <see cref="WindowsToastNotifier"/>; a test hands in a
/// recorder, so no test ever shows one.</summary>
public interface IToastNotifier
{
    /// <summary>Shows a notification, or replaces the one with the same tag.</summary>
    void Show(NotificationContent content);
    /// <summary>Takes one of MultiPreview's notifications away, by its tag.</summary>
    void Remove(string tag);
    /// <summary>Takes all of MultiPreview's notifications away.</summary>
    void Clear();
    /// <summary>Whether Windows says it is NOT showing MultiPreview's notifications: they are switched off in
    /// Windows' own settings (for every program, or for MultiPreview), or by a policy. It asks Windows, so it is
    /// never called on the window's thread.</summary>
    bool Blocked();
    /// <summary>A notification or one of its buttons was clicked: the arguments MultiPreview wrote into it. Raised
    /// on a thread of Windows'.</summary>
    event Action<string>? Clicked;
    /// <summary>The player closed a notification: its tag. Raised on a thread of Windows'.</summary>
    event Action<string>? Dismissed;
    /// <summary>Windows could not show a notification it was handed: its error code (an HRESULT), and nothing else.
    /// Raised on a thread of Windows'.</summary>
    event Action<int>? Failed;
}

/// <summary>Windows' own notifications (Windows.UI.Notifications, part of what the app is built on). MultiPreview
/// has no installer, so Windows knows it by the registry key of <see cref="NotificationRegistration"/>. A click
/// reaches MultiPreview through the notification object's own event, which works exactly while MultiPreview runs:
/// each shown object is kept here until it is clicked, closed or taken away. Called on a pool thread (the way's);
/// nothing here waits for the window. No test makes this class.
///
/// App makes it at every start-up, also for a player who never switches notifications on. So no FIELD here names a
/// type of Windows' own, and neither does the constructor: making the class must not load Windows' SDK (24 MB;
/// nothing else in the app loads it at start-up). The methods name those types, and a method is compiled when it is
/// first called, which is when the feature is first used. NotificationsWayTests holds the fields and the
/// constructor to that.</summary>
public sealed class WindowsToastNotifier : IToastNotifier
{
    /// <summary>All of MultiPreview's alert notifications are one group; a tag is unique within it.</summary>
    private const string Group = "alerts";

    private readonly string _appId;
    private readonly object _lock = new();
    // The notification object shown last for each tag (a ToastNotification), and Windows' notifier (a ToastNotifier):
    // held as objects, see above.
    private readonly Dictionary<string, object> _shown = new(StringComparer.Ordinal);
    private object? _notifier;

    public WindowsToastNotifier(string appId) => _appId = appId;

    public event Action<string>? Clicked;
    public event Action<string>? Dismissed;
    public event Action<int>? Failed;

    public void Show(NotificationContent content)
    {
        var xml = new XmlDocument();
        xml.LoadXml(content.Xml);
        var toast = new ToastNotification(xml)
        {
            Tag = content.Tag,
            Group = Group,
            SuppressPopup = !content.Banner,
            ExpirationTime = new DateTimeOffset(DateTime.SpecifyKind(content.ExpiresUtc, DateTimeKind.Utc)),
        };
        toast.Activated += OnActivated;
        toast.Dismissed += OnDismissed;
        toast.Failed += OnFailed;
        lock (_lock) _shown[content.Tag] = toast;
        Notifier().Show(toast);
    }

    public bool Blocked() => Notifier().Setting != NotificationSetting.Enabled;

    /// <summary>Windows' notifier for MultiPreview's identity, made when it is first needed.</summary>
    private ToastNotifier Notifier()
    {
        lock (_lock) return (ToastNotifier)(_notifier ??= ToastNotificationManager.CreateToastNotifier(_appId));
    }

    /// <summary>Lets go of a notification object when an event of its says it is over, but only if it is still the
    /// one kept for its tag. A newer notification of the same tag has taken its place in the table: an event that
    /// arrives late from the one it replaced must not let go of the newer one, whose clicks would then reach
    /// nobody. True when it was the one kept.</summary>
    private bool LetGo(ToastNotification toast)
    {
        string tag = toast.Tag;
        lock (_lock)
        {
            if (!_shown.TryGetValue(tag, out var kept) || !ReferenceEquals(kept, toast)) return false;
            _shown.Remove(tag);
            return true;
        }
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        LetGo(sender);
        // The click is passed on whichever object it came from: the player clicked, and the words say on what.
        if (args is ToastActivatedEventArgs activated) Clicked?.Invoke(activated.Arguments);
    }

    private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
    {
        // Timed out: it moved from the banner into the Notification Center and can still be clicked. Hidden: this
        // class took it away itself, or a newer one of its tag replaced it.
        if (args.Reason != ToastDismissalReason.UserCanceled) return;
        // Closed by the player. Said only for the notification that is kept for the tag: the closing of one that
        // was replaced meanwhile is not the closing of the one that is there now.
        if (LetGo(sender)) Dismissed?.Invoke(sender.Tag);
    }

    private void OnFailed(ToastNotification sender, ToastFailedEventArgs args)
    {
        LetGo(sender);
        Failed?.Invoke(args.ErrorCode.HResult);
    }

    public void Remove(string tag)
    {
        lock (_lock) _shown.Remove(tag);
        ToastNotificationManager.History.Remove(tag, Group, _appId);
    }

    public void Clear()
    {
        lock (_lock) _shown.Clear();
        ToastNotificationManager.History.Clear(_appId);
    }
}

/// <summary>The current user's part of the registry, for <see cref="NotificationRegistration"/> and for nothing
/// else: it writes and deletes that class's one key and refuses every other path. No test makes this class: tests
/// hand in a dictionary.</summary>
public sealed class UserRegistry : IUserRegistry
{
    public bool Exists(string key)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened != null;
    }

    public string? Read(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened?.GetValue(name) as string;
    }

    public void Write(string key, string name, string value)
    {
        using var made = Registry.CurrentUser.CreateSubKey(Only(key));
        made.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string key) => Registry.CurrentUser.DeleteSubKeyTree(Only(key), throwOnMissingSubKey: false);

    /// <summary>The one key this class writes and deletes, or an exception. Delete removes a key with everything
    /// below it: no mistake anywhere else may hand it another path.</summary>
    private static string Only(string key) =>
        key == NotificationRegistration.Key ? key : throw new ArgumentException("UserRegistry writes and deletes the key of MultiPreview's notifications and no other.", nameof(key));
}

/// <summary>The picture Windows shows beside MultiPreview's notifications: the app's own icon as a PNG file, because
/// the registry key names a file. It is written once, into the settings folder, and only when Windows notifications
/// are switched on.</summary>
public static class NotificationIcon
{
    public const string FileName = "notification-icon.png";

    /// <summary>The file's path, written first if it is not there; null when it can't be written (then Windows
    /// shows a picture of its own). Never through a link, as everything in the settings folder.</summary>
    public static string? Ensure(string folder, Func<string, bool>? isLink = null)
    {
        try
        {
            isLink ??= SettingsHome.IsLink;
            if (!Directory.Exists(folder) || isLink(folder)) return null;
            var path = Path.Combine(folder, FileName);
            if (isLink(path)) return null;
            if (File.Exists(path)) return path;
            using var stream = typeof(NotificationIcon).Assembly.GetManifestResourceStream("EveMultiPreview.Icon.ico");
            if (stream == null) return null;
            using var icon = new System.Drawing.Icon(stream, 64, 64);
            using var picture = icon.ToBitmap();
            picture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
