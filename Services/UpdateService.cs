using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>
/// Handles automatic update checking, downloading, and self-replacement via GitHub Releases.
/// Flow: CheckForUpdateAsync → DownloadUpdateAsync → ApplyUpdate (exits app, spawns updater, relaunches).
/// </summary>
public sealed class UpdateService
{
    private const string GITHUB_API_URL = "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest";
    private const string EXE_ASSET_NAME = "EVE.MultiPreview.exe";

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _httpClient;

    /// <summary>Current app version from assembly metadata.</summary>
    public string CurrentVersion { get; }

    /// <summary>Where a download is kept until it is installed. Tests give a temp folder of their own.</summary>
    public string DownloadFolder { get; init; } = Path.Combine(Path.GetTempPath(), "EVEMultiPreview_update");

    /// <summary>The SHA-256 GitHub publishes for the exe of the release this service is aimed at (lower-case hex), or
    /// null when GitHub's answer had none. Set by CheckForUpdateAsync and Select.</summary>
    public string? ExpectedSha256 { get; private set; }

    /// <summary>The SHA-256 of the file this service last finished downloading, whether or not GitHub published a
    /// digest. When it did not, this is what the player's second click is held to: the file must still hash to it.
    /// A new download (and a new Select) replaces it; null means nothing was downloaded here.</summary>
    private string? _downloadedSha256;

    /// <summary>The selected release's "digest" as GitHub sent it, kept only when it can't be read (ExpectedSha256 is
    /// then UnreadableDigest), for the log line of the refusal.</summary>
    private string? _digestAsSent;

    /// <summary>Latest version tag from GitHub (set after CheckForUpdateAsync).</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>Release notes body from GitHub (set after CheckForUpdateAsync).</summary>
    public string? ReleaseNotes { get; private set; }

    /// <summary>Direct download URL for the new exe (set after CheckForUpdateAsync).</summary>
    public string? DownloadUrl { get; private set; }

    /// <summary>HTML URL for the release page (set after CheckForUpdateAsync).</summary>
    public string? ReleasePageUrl { get; private set; }

    /// <summary>Whether an update is available (set after CheckForUpdateAsync).</summary>
    public bool UpdateAvailable { get; private set; }

    public UpdateService() : this(null) { }

    /// <summary>With a handler, every request goes to it and none to the network: the tests' way in.</summary>
    public UpdateService(HttpMessageHandler? handler)
    {
        _httpClient = handler == null ? SharedClient : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>GitHub's answer for a URL, as text. GitHub's API refuses a request without a User-Agent.</summary>
    private async Task<string> GetTextAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("EVE-MultiPreview/" + CurrentVersion);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Query the GitHub Releases API and determine if an update is available.
    /// Returns true if an update is available.
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(bool allowPreRelease = false)
    {
        try
        {
            // The newest release that ships the exe from this repository's own downloads, with the checksum GitHub
            // publishes for that file (IDEAS #53). The answer is read as JSON, so the version, the link and the
            // checksum always belong to the same release.
            // P14: a stable check asks for the list first, so the notes can be those of every version skipped; when the
            // list can't be had or holds nothing usable, it asks for the one latest release, as it always did.
            var latest = allowPreRelease
                ? LatestIn(await GetTextAsync("https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases"))
                : await StableWithSkippedNotesAsync()
                  ?? LatestIn(await GetTextAsync("https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest"));
            if (latest == null) return false;
            Select(latest);

            UpdateAvailable = Version.TryParse(latest.Version, out var newest)
                              && Version.TryParse(CurrentVersion, out var current) && newest > current;

            Debug.WriteLine($"[Update] Current={CurrentVersion}, Latest={LatestVersion}, Available={UpdateAvailable}");
            return UpdateAvailable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Download the new exe to a temp directory. Reports progress 0.0–1.0.
    /// Returns the path to the downloaded file.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
            throw new InvalidOperationException("No download URL available. Call CheckForUpdateAsync first.");
        if (OwnAssetUrl(DownloadUrl) == null)
            throw new InvalidOperationException("The download is not one of this repository's release assets.");
        // A digest GitHub sent but this code can't read: refused here, before 70-odd MB are fetched for nothing.
        if (UnreadableDigestRefusal() is { } unreadable) throw unreadable;
        _downloadedSha256 = null;

        Directory.CreateDirectory(DownloadFolder);
        var destPath = Path.Combine(DownloadFolder, EXE_ASSET_NAME);

        // Delete any old download
        if (File.Exists(destPath)) File.Delete(destPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
        request.Headers.UserAgent.ParseAdd("EVE-MultiPreview/" + CurrentVersion);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloadedBytes = 0;

        // The file is closed before it is checked: the braces end both streams.
        await using (var contentStream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloadedBytes += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((double)downloadedBytes / totalBytes);
            }
        }

        progress?.Report(1.0);
        Debug.WriteLine($"[Update] Downloaded {downloadedBytes:N0} bytes to {destPath}");

        // IDEAS #53: the finished download is always hashed, and the hash is remembered (it is what a second click
        // without a published digest is held to). A download that is not the file GitHub published is removed at once
        // (best effort: a failed delete is not reported, and nothing can install it anyway).
        // The file is read through off the calling (UI) thread: it is some 75 MB.
        var hashed = await Task.Run(() => HashFile(destPath));
        _downloadedSha256 = hashed.Hash;
        if (Judge(hashed, out var refusal) == DownloadCheck.Mismatch)
        {
            try { File.Delete(destPath); } catch { }
            throw refusal!;
        }
        return destPath;
    }

    // ── Checking the download (IDEAS #53) ────────────────────────────

    /// <summary>What comparing a download with GitHub's checksum found.</summary>
    public enum DownloadCheck
    {
        /// <summary>The file's SHA-256 is the one GitHub publishes for it.</summary>
        Verified,
        /// <summary>GitHub's answer held no checksum for the file at all, and the file is exactly the one this service
        /// downloaded (same hash). It installs only after the player's second click.</summary>
        NoDigest,
        /// <summary>Anything else: not the file GitHub published (cut short, damaged, another file), a file that can't
        /// be read, a file that changed after the download, a checksum this code can't read, or nothing downloaded.
        /// Never installed.</summary>
        Mismatch,
    }

    /// <summary>Stands in for a "digest" that GitHub sent but this code can't read (not "sha256:" and 64 hex digits, or
    /// not a string). It is not a SHA-256, so no file ever matches it: such a release is refused before the download
    /// (DownloadUpdateAsync), instead of counting as "no checksum".</summary>
    public const string UnreadableDigest = "unreadable-digest";

    /// <summary>Compares a downloaded file with the checksum of the selected release; with none published, with the
    /// hash this service took of its own download. Every path hashes the file: a missing, locked or changed file is a
    /// Mismatch, never a NoDigest.
    /// What this catches: a download that was cut short, damaged on the way or on disk, or swapped for another file
    /// after GitHub's answer was read. What it can't catch: a release that was replaced at GitHub itself (a taken-over
    /// account), because the checksum comes from the same place as the file; only a signature on the exe covers that.
    /// Nor a program already running as the player that replaces the file in the seconds between the last hash and the
    /// updater script's copy: an updater that runs as the player can't stop that, and it is no worse than before.</summary>
    public DownloadCheck Verify(string downloadedExePath) => Judge(HashFile(downloadedExePath), out _);

    /// <summary>What reading a file for its hash gave: the hash and length, or the TYPE name of the exception that
    /// stopped it (never its message, which holds the path).</summary>
    private readonly record struct Hashed(string? Hash, long Length, string? IoType)
    {
        public static readonly Hashed None = new(null, -1, null);
    }

    private static Hashed HashFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var length = file.Length;
            return new Hashed(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), length, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Hashed(null, -1, ex.GetType().Name);      // a file that can't be read can't be shown to be GitHub's
        }
    }

    private DownloadCheck Judge(Hashed hashed, out UpdateIntegrityException? refusal)
    {
        refusal = null;
        var expected = ExpectedSha256;
        if (expected == UnreadableDigest) return Refused(IntegrityCause.UnreadableDigest, hashed, out refusal);
        if (hashed.Hash == null) return Refused(IntegrityCause.FileUnreadable, hashed, out refusal);
        if (expected != null)
            return hashed.Hash.Equals(expected, StringComparison.OrdinalIgnoreCase)
                ? DownloadCheck.Verified : Refused(IntegrityCause.Mismatch, hashed, out refusal);
        // No digest published: the only thing to hold the file to is the hash taken when it finished downloading.
        if (_downloadedSha256 == null) return Refused(IntegrityCause.NothingDownloaded, hashed, out refusal);
        return hashed.Hash.Equals(_downloadedSha256, StringComparison.Ordinal)
            ? DownloadCheck.NoDigest : Refused(IntegrityCause.FileChanged, hashed, out refusal);
    }

    private DownloadCheck Refused(IntegrityCause cause, Hashed hashed, out UpdateIntegrityException? refusal)
    {
        refusal = Refuse(cause, hashed);
        return DownloadCheck.Mismatch;
    }

    /// <summary>The refusal, with the facts a bug report needs and nothing private: the cause, the version, the digest
    /// GitHub gave (or that it gave none / an unreadable one), the hash and length of what was found, and the TYPE of an
    /// IO exception. Hashes and lengths are public values; no exception message and no path (a temp path holds the
    /// Windows user name). The message is what the error log records.</summary>
    private UpdateIntegrityException Refuse(IntegrityCause cause, Hashed hashed)
    {
        var expected = ExpectedSha256 switch { null => "none published", UnreadableDigest => "unreadable", var hex => hex };
        var text = $"Update refused ({cause}): version={LatestVersion ?? "?"}; expected={expected}; computed={hashed.Hash ?? "none"}; "
                   + $"length={(hashed.Length >= 0 ? hashed.Length.ToString() : "unknown")}"
                   + (cause == IntegrityCause.FileChanged ? $"; downloaded={_downloadedSha256}" : "")
                   + (hashed.IoType != null ? $"; io={hashed.IoType}" : "")
                   // What GitHub sent, so a format this code doesn't know yet can be seen in a bug report.
                   + (cause == IntegrityCause.UnreadableDigest ? $"; digest={ForLog(_digestAsSent)}" : "");
        return new UpdateIntegrityException(text, cause);
    }

    /// <summary>Text from outside for a log line: its first 80 characters, with everything that isn't printable ASCII
    /// (a line break, a control character, any other script) as "?". The error log is a file players attach to bug
    /// reports, and this text is whatever an API answer held.</summary>
    public static string ForLog(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var shown = text.Length > 80 ? text[..80] : text;
        return string.Create(shown.Length, shown, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = source[i] is >= ' ' and <= '~' ? source[i] : '?';
        });
    }

    /// <summary>The refusal of the selected release when the checksum GitHub sent for it can't be read; null otherwise.
    /// No download of such a release can ever be checked, so the dialog asks this when it opens.</summary>
    public UpdateIntegrityException? UnreadableDigestRefusal() =>
        ExpectedSha256 == UnreadableDigest ? Refuse(IntegrityCause.UnreadableDigest, Hashed.None) : null;

    /// <summary>The checksum in GitHub's "digest" field ("sha256:" and 64 hex digits) as lower-case hex; null for
    /// anything else (the caller then refuses the release, see ReadRelease).</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>ApplyUpdate's first step: throws unless the file is the one GitHub published, or GitHub published no
    /// checksum, the file is exactly the one downloaded here, and the player has agreed (the dialog's second click) to
    /// install it unchecked. The file is hashed again every time.</summary>
    public void RequireVerified(string downloadedExePath, bool allowUnverified)
    {
        var check = Judge(HashFile(downloadedExePath), out var refusal);
        if (check == DownloadCheck.Mismatch) throw refusal!;
        if (check == DownloadCheck.NoDigest && !allowUnverified)
            throw new UpdateIntegrityException(
                $"Update refused ({IntegrityCause.NotConfirmed}): version={LatestVersion ?? "?"}; expected=none published", IntegrityCause.NotConfirmed);
    }

    /// <summary>
    /// Spawn the PowerShell updater script that waits for exit, backs up the settings file, replaces the exe, and
    /// relaunches. Then shuts down the current app. Before a version that reads its settings beside the exe (up to
    /// 2.3.42) it hands the current settings to the exe folder (IDEAS #57); when that fails nothing is installed and the
    /// result says why, so the dialog can warn the player.
    /// Calling it again with <paramref name="installAnyway"/> installs regardless.
    /// The file is checked against GitHub's checksum first (RequireVerified, IDEAS #53): a file that doesn't match is
    /// never installed, and one that can't be checked only with <paramref name="allowUnverified"/>.
    /// </summary>
    public HandBackResult ApplyUpdate(string downloadedExePath, bool installAnyway = false, bool allowUnverified = false)
    {
        RequireVerified(downloadedExePath, allowUnverified);
        var handBack = HandBackIfNeeded(AppPaths.SettingsPath, AppPaths.ExeDir, DateTime.Now);
        if (handBack.State == HandBackState.Failed && !installAnyway) return handBack;

        var appDir = AppPaths.ExeDir;
        var scriptPath = Path.Combine(Path.GetTempPath(), "EVEMultiPreview_update", "update.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        // A BOM, so Windows PowerShell 5.1 never reads the script in the ANSI code page (it is ASCII anyway).
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Launch PowerShell hidden. The paths travel in the environment, never in the script text.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in UpdaterEnvironment(appDir, AppPaths.SettingsPath, AppPaths.BackupsDir, downloadedExePath, Environment.ProcessId, Environment.ProcessPath))
            psi.Environment[name] = value;
        Process.Start(psi);

        Debug.WriteLine("[Update] Updater script launched — shutting down app");
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
        return handBack;
    }

    /// <summary>True when <paramref name="target"/> is an older version than <paramref name="current"/> (a downgrade).</summary>
    public static bool IsOlder(string? target, string current) =>
        Version.TryParse(target, out var t) && Version.TryParse(current, out var c) && t < c;

    /// <summary>The hand-back, when the chosen release reads its settings beside the exe (SettingsHome.ReadsExeFolder:
    /// every version up to 2.3.42, whatever version this one is); nothing to do otherwise.</summary>
    public HandBackResult HandBackIfNeeded(string settingsFile, string exeDir, DateTime now) =>
        SettingsHome.ReadsExeFolder(LatestVersion) ? SettingsHome.HandBack(settingsFile, exeDir, now) : default;

    /// <summary>What the updater script reads (as $env:EMP_…): the four paths, the id of the process it must wait for
    /// (this one) and the path of the exe that is running (this one's; "" when not known), which it starts again when
    /// the new exe could not be put in place. Environment variables are Unicode and never parsed as code, so any
    /// folder name works and none can inject a command.</summary>
    public static IReadOnlyList<(string Name, string Value)> UpdaterEnvironment(string appDir, string settingsFile, string backupDir,
        string downloadedExePath, int processId, string? runningExe) => new[]
    {
        ("EMP_APPDIR", Path.TrimEndingDirectorySeparator(appDir)), ("EMP_CONFIG", settingsFile), ("EMP_BACKUPS", backupDir), ("EMP_NEWEXE", downloadedExePath),
        ("EMP_PID", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("EMP_EXE", runningExe ?? ""),
    };

    /// <summary>The updater: waits for MultiPreview to exit, copies the settings file into the Backups folder of the
    /// settings folder (refusing a Backups folder that is a link), replaces the exe in $env:EMP_APPDIR and starts it again.
    /// It waits for the process that started it by that process's id (IDEAS P52): the name is no use for that, because
    /// a freshly downloaded copy runs as EVE.MultiPreview and a renamed copy as anything, and a new exe started while
    /// the old one is still closing finds it running, says so and quits. The wait by name stays behind it, as before.
    /// The copy of the new exe can fail (the download quarantined or cleaned away after it was checked, the exe in the
    /// app folder locked), so its answer is kept: the dot-named exe and pdb of a freshly downloaded install are removed
    /// only behind a copy that succeeded, and after one that failed nothing is removed and the exe that was running is
    /// started again. It used to remove them whatever the copy answered, which left such an install with no exe at all.
    /// A constant, ASCII only, holding no path (see UpdaterEnvironment). Cmdlets and property access only, no .NET calls,
    /// so it also runs in Constrained Language mode (AppLocker/WDAC); only forms that Windows PowerShell 5.1 has.</summary>
    public const string UpdaterScript = @"
# EVE MultiPreview Auto-Updater (paths come from the EMP_* environment variables)
$appDir = $env:EMP_APPDIR
$configFile = $env:EMP_CONFIG
$backupDir = $env:EMP_BACKUPS
$newExe = $env:EMP_NEWEXE
if (-not $appDir -or -not $newExe) { exit 1 }

# Wait for the main process to exit: the one that started this script, whatever its exe is named.
# It goes on at once when that process is already gone, and after 30 seconds whatever happens.
Wait-Process -Id $env:EMP_PID -Timeout 30 -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$maxWait = 30; $waited = 0
while ((Get-Process -Name 'EVE MultiPreview' -ErrorAction SilentlyContinue) -and $waited -lt $maxWait) {
    Start-Sleep -Seconds 1; $waited++
}

# Back up the settings file (in the settings folder: AppData, or beside the exe when portable)
try {
    if ($configFile -and $backupDir -and (Test-Path -LiteralPath $configFile)) {
        $isLink = $false
        if (Test-Path -LiteralPath $backupDir) {
            $item = Get-Item -LiteralPath $backupDir -Force
            if ($item.LinkType) { $isLink = $true }   # a junction or symbolic link; a OneDrive folder has no LinkType
        }
        if (-not $isLink) {
            New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
            $timestamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
            $backupFile = Join-Path $backupDir ('EVE MultiPreview_pre-update_' + $timestamp + '.json')
            Copy-Item -LiteralPath $configFile -Destination $backupFile
        }
    }
} catch { }

# Replace the executable. The copy can fail: the download was quarantined or cleaned away after it
# was checked, or the exe in the app folder is still locked. What the copy answers decides the rest.
$oldExe = Join-Path $appDir 'EVE MultiPreview.exe'
$dotExe = Join-Path $appDir 'EVE.MultiPreview.exe'
$dotPdb = Join-Path $appDir 'EVE.MultiPreview.pdb'
Copy-Item -LiteralPath $newExe -Destination $oldExe -Force
$copied = $?

if ($copied) {
    # The new exe is in place, under the name with a space. The GitHub release ships the file as
    # 'EVE.MultiPreview.exe' (with a dot, GitHub-friendly), so a freshly downloaded install runs under
    # that name: its exe and pdb are the old version now, and beside the new exe they show up as
    # confusing duplicates in the install folder (issue #42, bug #3). They are removed only here.
    if (Test-Path -LiteralPath $dotExe) {
        Remove-Item -LiteralPath $dotExe -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $dotPdb) {
        Remove-Item -LiteralPath $dotPdb -Force -ErrorAction SilentlyContinue
    }
    $startExe = $oldExe
} else {
    # Nothing was replaced, so nothing is removed: on a freshly downloaded install the dot-named exe
    # is the only exe there is. The exe that was running is started again: it is known to work and it
    # is the version the player had. When it isn't known, whichever of the two names is there.
    $startExe = $env:EMP_EXE
    if (-not $startExe -or -not (Test-Path -LiteralPath $startExe)) { $startExe = $oldExe }
    if (-not (Test-Path -LiteralPath $startExe)) { $startExe = $dotExe }
}

# Restart the app (a bracketed current folder breaks Start-Process on 5.1, so start from TEMP)
if (Test-Path -LiteralPath $startExe) {
    Start-Process -FilePath $startExe -WorkingDirectory $env:TEMP
}

# Clean up temp download
Start-Sleep -Seconds 3
# (never the exe folder itself, whatever trailing separators either path has)
$downloadDir = Split-Path -LiteralPath $newExe
if ($downloadDir -and ($downloadDir.TrimEnd('\', '/') -ne $appDir.TrimEnd('\', '/'))) {
    Remove-Item -LiteralPath $downloadDir -Recurse -Force -ErrorAction SilentlyContinue
}
";

    // ── Install a specific version (upgrade or downgrade) ───────────

    /// <summary>One installable GitHub release. Sha256 is the checksum GitHub publishes for its exe (lower-case hex),
    /// or null when GitHub's answer had none. RawDigest is what GitHub sent when that can't be read (Sha256 is then
    /// UnreadableDigest); it is only ever written to the log.</summary>
    public sealed record ReleaseInfo(string Version, string DownloadUrl, string? Notes, string? PageUrl, bool PreRelease, string? Sha256 = null,
        string? RawDigest = null);

    private const string RELEASES_URL = "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases?per_page=100";
    // Only ever this repository's own release assets, whatever the API returns.
    private const string ASSET_URL_PREFIX = "https://github.com/CJKondur/EVE-MultiPreview/releases/download/";

    /// <summary>Every release that ships the exe, newest first. Pre-releases only when
    /// <paramref name="includePreReleases"/> (the About tab's pre-release opt-in).</summary>
    public async Task<List<ReleaseInfo>> ListReleasesAsync(bool includePreReleases)
    {
        using var doc = JsonDocument.Parse(await GetTextAsync(RELEASES_URL));
        return doc.RootElement.EnumerateArray()
            .Select(ReadRelease)
            .Where(r => r != null && (includePreReleases || !r.PreRelease))
            .OrderByDescending(r => System.Version.Parse(r!.Version))
            .ToList()!;
    }

    /// <summary>One release of GitHub's API answer, or null when it is a draft, has no version for a tag, or doesn't
    /// ship the exe from this repository's own download path (whatever the API returns, nothing else is installable).</summary>
    public static ReleaseInfo? ReadRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var tag = Text(release, "tag_name")?.TrimStart('v', 'V');
        if (!System.Version.TryParse(tag, out _)) return null;

        string? url = null, sha256 = null, asSent = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (Text(asset, "name") == EXE_ASSET_NAME)
                {
                    url = OwnAssetUrl(Text(asset, "browser_download_url"));
                    (sha256, asSent) = DigestOf(asset);      // the exe's own checksum, never another file's
                }
        if (url == null) return null;

        bool pre = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
        return new ReleaseInfo(tag!, url, Text(release, "body"), Text(release, "html_url"), pre, sha256, asSent);
    }

    /// <summary>The normalised form of an address when it is one of this repository's own release downloads, else
    /// null: https, host github.com, default port, and the normalised AbsoluteUri (dot segments and percent-encoded
    /// dots resolved) still starts with ASSET_URL_PREFIX. The raw string is never trusted: "…/download/../../x" starts
    /// with the prefix but resolves elsewhere. The address that is checked is the one that is fetched.</summary>
    public static string? OwnAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri.StartsWith(ASSET_URL_PREFIX, StringComparison.Ordinal) ? uri.AbsoluteUri : null;
    }

    /// <summary>The checksum of one asset (lower-case hex): null only when the asset has no "digest" (absent or JSON
    /// null). A digest that is there but isn't "sha256:" and 64 hex digits, or isn't a string, is UnreadableDigest,
    /// which no file matches and which is refused before the download; with it comes what GitHub sent (the string, or
    /// the JSON text of anything else), for the log.</summary>
    private static (string? Sha256, string? AsSent) DigestOf(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind == JsonValueKind.Null) return (null, null);
        var read = digest.ValueKind == JsonValueKind.String ? ParseDigest(digest.GetString()) : null;
        if (read != null) return (read, null);
        return (UnreadableDigest, digest.ValueKind == JsonValueKind.String ? digest.GetString() : digest.GetRawText());
    }

    private static string? Text(JsonElement of, string property) =>
        of.ValueKind == JsonValueKind.Object && of.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>The newest installable release in GitHub's answer: the answer of releases/latest (one release) or of
    /// releases (a list, when pre-releases are wanted). Null when there is none.</summary>
    public static ReleaseInfo? LatestIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return ReadRelease(doc.RootElement);
        return doc.RootElement.EnumerateArray().Select(ReadRelease).Where(r => r != null)
            .OrderByDescending(r => System.Version.Parse(r!.Version)).FirstOrDefault();
    }

    private const string STABLE_LIST_URL = "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases?per_page=30";

    /// <summary>The newest stable release, carrying the notes of every stable version newer than the running one; null
    /// when the list can't be had or holds no installable stable release (the caller then asks for the latest).</summary>
    private async Task<ReleaseInfo?> StableWithSkippedNotesAsync()
    {
        try { return StableWithSkippedNotes(await GetTextAsync(STABLE_LIST_URL), CurrentVersion); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Release list failed, asking for the latest: {ex.Message}");
            return null;
        }
    }

    /// <summary>The newest stable release in GitHub's list answer, its Notes replaced by the combined notes of every
    /// stable release newer than <paramref name="currentVersion"/> (newest first). The release itself (version,
    /// download, checksum) is the one the single-release check would give. Null when the answer is no list or holds no
    /// installable stable release. Notes are text only: nothing in them becomes a link.</summary>
    public static ReleaseInfo? StableWithSkippedNotes(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var stable = doc.RootElement.EnumerateArray().Select(ReadRelease).Where(r => r != null && !r.PreRelease).Select(r => r!)
            .OrderByDescending(r => System.Version.Parse(r.Version)).ToList();
        if (stable.Count == 0) return null;
        if (!System.Version.TryParse(currentVersion, out var running)) return stable[0];
        var notes = Services.ReleaseNotes.Combine(
            stable.Select(r => (System.Version.Parse(r.Version), r.Version, r.Notes, false, false)), running, includePrereleases: false);
        return notes.Length == 0 ? stable[0] : stable[0] with { Notes = notes };
    }

    /// <summary>Aim this service at a chosen release, so the normal DownloadUpdateAsync →
    /// ApplyUpdate path (config backup, exe swap, relaunch) installs it.</summary>
    public void Select(ReleaseInfo release)
    {
        LatestVersion = release.Version;
        DownloadUrl = release.DownloadUrl;
        ReleaseNotes = release.Notes;
        ReleasePageUrl = release.PageUrl;
        ExpectedSha256 = release.Sha256;
        _digestAsSent = release.RawDigest;
        _downloadedSha256 = null;      // a hash taken of another release's download says nothing about this one
    }
}

/// <summary>Why an update was refused (IDEAS #53).</summary>
public enum IntegrityCause
{
    /// <summary>The file's hash differs from the digest GitHub published.</summary>
    Mismatch,
    /// <summary>GitHub sent a digest this code can't read.</summary>
    UnreadableDigest,
    /// <summary>The file is missing, locked or otherwise can't be read.</summary>
    FileUnreadable,
    /// <summary>No digest was published and the file no longer hashes to what was downloaded.</summary>
    FileChanged,
    /// <summary>No digest was published and this service has no download to hold the file to.</summary>
    NothingDownloaded,
    /// <summary>No digest was published and the player has not confirmed installing unchecked.</summary>
    NotConfirmed,
}

/// <summary>An update that must not be installed: the download isn't the file GitHub published for the release, it
/// can't be read or changed after the download, or it can't be checked and installing it unchecked was not confirmed
/// (IDEAS #53). The Message is a diagnostic line for the error log: facts only, no exception message, no path.</summary>
public sealed class UpdateIntegrityException : Exception
{
    public IntegrityCause Cause { get; }

    public UpdateIntegrityException(string message, IntegrityCause cause) : base(message) => Cause = cause;
}
