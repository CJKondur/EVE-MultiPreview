using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace EveMultiPreview.Services;

/// <summary>The Linux half of UpdateService: the release's Linux download (one tar.gz holding eve-multipreview and its
/// desktop entry) and its install. The checking, download and SHA-256 rules are the shared half's, unchanged.</summary>
public sealed partial class UpdateService
{
    private const string EXE_ASSET_NAME = "EVE.MultiPreview-linux-x64.tar.gz";

    /// <summary>The program inside the download.</summary>
    public const string ProgramEntry = "eve-multipreview/eve-multipreview";

    public enum LinuxInstall { Started, NotWritable, NoProgram }

    /// <summary>Installs a verified download: the program is taken out of the tar into the folder of the running one, made
    /// executable and renamed over it (a rename: the running program keeps its old file until it exits), the settings
    /// file is copied into the Backups folder, and a small shell waits for this process to end and starts the new one. The
    /// caller then ends the app. NotWritable: the program's folder is not this account's to write (installed system-wide),
    /// nothing was changed; the player is sent to the release page instead.</summary>
    public LinuxInstall InstallLinux(string downloadedTarPath, bool allowUnverified = false)
    {
        RequireVerified(downloadedTarPath, allowUnverified);
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program) || Path.GetDirectoryName(program) is not { } dir) return LinuxInstall.NotWritable;
        var staged = Path.Combine(dir, ".eve-multipreview.new");
        try
        {
            using (var gz = new GZipStream(File.OpenRead(downloadedTarPath), CompressionMode.Decompress))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) != null)
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.Name.TrimStart('.', '/') == ProgramEntry)
                        break;
                if (entry?.DataStream == null) return LinuxInstall.NoProgram;
                using var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None);
                entry.DataStream.CopyTo(output);
            }
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (UnauthorizedAccessException)
        {
            try { File.Delete(staged); } catch (Exception) { }
            return LinuxInstall.NotWritable;
        }

        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                Directory.CreateDirectory(AppPaths.BackupsDir);
                File.Copy(AppPaths.SettingsPath, Path.Combine(AppPaths.BackupsDir,
                    $"before-update-to-{LatestVersion ?? "new"}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a backup that fails does not stop the update

        File.Move(staged, program, overwrite: true);   // rename(2): atomic, and the running process keeps its file
        try { File.Delete(downloadedTarPath); } catch (Exception) { }

        // The new program starts once this one has gone (one MultiPreview at a time). The paths travel as arguments of the
        // shell, never inside its script, so no name can be read as a command.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec \"$2\" >/dev/null 2>&1");
        start.ArgumentList.Add("eve-multipreview-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(program);
        Process.Start(start);
        return LinuxInstall.Started;
    }
}
