namespace EveMultiPreview.Services;

/// <summary>Where the Discord webhook link lives on Linux. The link is the whole credential, and the settings file is
/// readable by every account on the machine (and gets copied into backups and bug reports), so the settings field
/// holds only a marker, "linuxfile1:" and a random id, and the link itself sits in <c>discord-webhook</c> beside the
/// settings file, created with mode 0600 (owner only). The id changes with every saved link: PushWay starts the sending
/// anew when the stored text changes, and a constant marker would never tell it that the link behind it did.
/// Nothing here puts the link in an exception or a message. A "dpapi1:" value from the Windows app can't be decrypted
/// here and reads as no link.</summary>
public static class WebhookFile
{
    public const string Marker = "linuxfile1:";
    public const string FileName = "discord-webhook";
    /// <summary>What the Windows app's WebhookSecret puts in front of its DPAPI text (that class is Windows-only).</summary>
    public const string WindowsPrefix = "dpapi1:";

    public static string FilePath => Path.Combine(AppPaths.ConfigDir, FileName);

    /// <summary>The stored text is a link of the Windows app, encrypted for that Windows user.</summary>
    public static bool IsWindowsEncrypted(string? stored) =>
        stored != null && stored.StartsWith(WindowsPrefix, StringComparison.Ordinal);

    /// <summary>Writes the link to its file (mode 0600 from the moment the file exists: it is made under a temporary
    /// name with that mode, then renamed over the old one) and returns the text for the settings field. Throws
    /// ArgumentException (fixed text) for a link that isn't sendable, IOException when the file can't be written.</summary>
    public static string Save(Uri webhook)
    {
        if (!DiscordWebhook.IsSendable(webhook)) throw new ArgumentException("Not a sendable Discord webhook link.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();   // the 0600 mode below is a Unix thing
        Directory.CreateDirectory(AppPaths.ConfigDir);
        string temp = FilePath + ".tmp";
        File.Delete(temp);   // a leftover from a crash may have another mode; CreateNew below then makes it fresh
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(temp, options))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // the umask can only remove bits from the create mode; this sets it exactly, before any content is written
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(webhook.AbsoluteUri);
        }
        File.Move(temp, FilePath, overwrite: true);
        return Marker + Guid.NewGuid().ToString("N");
    }

    /// <summary>The link behind a stored text, or null: no marker, no file, a file that can't be read or holds
    /// something that isn't Discord's webhook link. Never throws.</summary>
    public static Uri? Read(string? stored)
    {
        if (stored == null || !stored.StartsWith(Marker, StringComparison.Ordinal)) return null;
        try
        {
            var text = File.ReadAllText(FilePath).Trim();
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && DiscordWebhook.IsSendable(uri) ? uri : null;
        }
        catch (Exception) { return null; }   // missing, unreadable, ...: no link
    }

    /// <summary>Deletes the file. Never throws.</summary>
    public static void Delete()
    {
        try { File.Delete(FilePath); }
        catch (Exception) { /* nothing more can be done; the settings field is emptied anyway */ }
    }
}
