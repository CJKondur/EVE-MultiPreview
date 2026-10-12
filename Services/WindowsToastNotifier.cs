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
    /// <summary>A notification or one of its buttons was clicked: the arguments MultiPreview wrote into it. Raised
    /// on a thread of Windows'.</summary>
    event Action<string>? Clicked;
    /// <summary>The player closed a notification: its tag. Raised on a thread of Windows'.</summary>
    event Action<string>? Dismissed;
}

/// <summary>Windows' own notifications (Windows.UI.Notifications, part of what the app is built on). MultiPreview
/// has no installer, so Windows knows it by the registry key of <see cref="NotificationRegistration"/>. A click
/// reaches MultiPreview through the notification object's own event, which works exactly while MultiPreview runs:
/// each shown object is kept here until it is clicked, closed or taken away. Called on the pump's thread; nothing
/// here waits for the window. No test makes this class.</summary>
public sealed class WindowsToastNotifier : IToastNotifier
{
    /// <summary>All of MultiPreview's alert notifications are one group; a tag is unique within it.</summary>
    private const string Group = "alerts";

    private readonly string _appId;
    private readonly object _lock = new();
    private readonly Dictionary<string, ToastNotification> _shown = new(StringComparer.Ordinal);
    private ToastNotifier? _notifier;

    public WindowsToastNotifier(string appId) => _appId = appId;

    public event Action<string>? Clicked;
    public event Action<string>? Dismissed;

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
        ToastNotifier notifier;
        lock (_lock)
        {
            _shown[content.Tag] = toast;
            notifier = _notifier ??= ToastNotificationManager.CreateToastNotifier(_appId);
        }
        notifier.Show(toast);
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        lock (_lock) _shown.Remove(sender.Tag);
        if (args is ToastActivatedEventArgs activated) Clicked?.Invoke(activated.Arguments);
    }

    private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
    {
        // Timed out: it moved from the banner into the Notification Center and can still be clicked. Hidden: this
        // class took it away itself.
        if (args.Reason != ToastDismissalReason.UserCanceled) return;
        lock (_lock) _shown.Remove(sender.Tag);
        Dismissed?.Invoke(sender.Tag);
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
/// else. No test makes this class: tests hand in a dictionary.</summary>
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
        using var made = Registry.CurrentUser.CreateSubKey(key);
        made.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
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
