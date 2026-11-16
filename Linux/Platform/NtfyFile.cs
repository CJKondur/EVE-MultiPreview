namespace EveMultiPreview.Services;

/// <summary>Where ntfy's topic and access token live on Linux, as the Discord link does (WebhookFile): the settings file
/// is readable by every account on the machine and gets copied into backups and bug reports, so its NtfyTopic and
/// NtfyToken hold only a marker, "linuxfile1:" and a random id, and the text itself sits in <c>ntfy-topic</c> or
/// <c>ntfy-token</c> beside the settings file, created with mode 0600 (owner only). The id changes with every save, so
/// PushWay sees the stored text change. Nothing here puts a topic or a token in an exception or a message. A "dpapi1:"
/// value from the Windows app can't be decrypted here and reads as nothing.</summary>
public static class NtfyFile
{
    public const string Marker = "linuxfile1:";
    public const string TopicFile = "ntfy-topic";
    public const string TokenFile = "ntfy-token";

    public static string PathOf(string fileName) => Path.Combine(AppPaths.ConfigDir, fileName);

    /// <summary>The stored text is the Windows app's, encrypted for that Windows user.</summary>
    public static bool IsWindowsEncrypted(string? stored) =>
        stored != null && stored.StartsWith(WebhookFile.WindowsPrefix, StringComparison.Ordinal);

    /// <summary>Writes the text to its file (mode 0600 from the moment the file exists: made under a temporary name with
    /// that mode, then renamed over the old one) and returns the text for the settings field. Throws ArgumentException
    /// (fixed text) for an empty text, IOException when the file can't be written.</summary>
    public static string Save(string fileName, string text)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Nothing to keep.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();   // the 0600 mode below is a Unix thing
        Directory.CreateDirectory(AppPaths.ConfigDir);
        string path = PathOf(fileName), temp = path + ".tmp";
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
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // the umask can only remove bits; this sets it exactly
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(text);
        }
        File.Move(temp, path, overwrite: true);
        return Marker + Guid.NewGuid().ToString("N");
    }

    /// <summary>The text behind a stored marker, or null: no marker, no file, or a file that can't be read. Never throws.</summary>
    public static string? Read(string fileName, string? stored)
    {
        if (stored == null || !stored.StartsWith(Marker, StringComparison.Ordinal)) return null;
        try
        {
            var text = File.ReadAllText(PathOf(fileName)).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception) { return null; }   // missing, unreadable, ...: nothing
    }

    /// <summary>Deletes the file. Never throws.</summary>
    public static void Delete(string fileName)
    {
        try { File.Delete(PathOf(fileName)); }
        catch (Exception) { /* nothing more can be done; the settings field is emptied anyway */ }
    }
}
