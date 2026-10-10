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
            string apiUrl = allowPreRelease
                ? "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases"
                : "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest";

            // The newest release that ships the exe from this repository's own downloads, with the checksum GitHub
            // publishes for that file (IDEAS #53). The answer is read as JSON, so the version, the link and the
            // checksum always belong to the same release.
            var latest = LatestIn(await GetTextAsync(apiUrl));
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

        // IDEAS #53: a download that is not the file GitHub published is never kept, so nothing can install it.
        // The file is read through off the calling (UI) thread: it is some 75 MB.
        if (await Task.Run(() => Verify(destPath)) == DownloadCheck.Mismatch)
        {
            try { File.Delete(destPath); } catch { }
            throw new UpdateIntegrityException("The download doesn't match the checksum GitHub publishes for this release. It was deleted.");
        }
        return destPath;
    }

    // ── Checking the download (IDEAS #53) ────────────────────────────

    /// <summary>What comparing a download with GitHub's checksum found.</summary>
    public enum DownloadCheck
    {
        /// <summary>The file's SHA-256 is the one GitHub publishes for it.</summary>
        Verified,
        /// <summary>GitHub's answer held no checksum for the file at all: it can't be checked.</summary>
        NoDigest,
        /// <summary>The file is not the one GitHub published (cut short, damaged, another file), it can't be read, or
        /// GitHub's checksum is in a form this code doesn't understand. Never installed.</summary>
        Mismatch,
    }

    /// <summary>Stands in for a "digest" that GitHub sent but this code can't read. It is not a SHA-256, so no file
    /// ever matches it: such a release fails closed instead of counting as "no checksum".</summary>
    public const string UnreadableDigest = "unreadable-digest";

    /// <summary>Compares a downloaded file with the checksum of the selected release.
    /// What this catches: a download that was cut short, damaged on the way or on disk, or swapped for another file
    /// after GitHub's answer was read. What it can't catch: a release that was replaced at GitHub itself (a taken-over
    /// account), because the checksum comes from the same place as the file. Only a signature on the exe covers that.</summary>
    public DownloadCheck Verify(string downloadedExePath) => VerifyFile(downloadedExePath, ExpectedSha256);

    public static DownloadCheck VerifyFile(string path, string? expectedSha256)
    {
        if (string.IsNullOrEmpty(expectedSha256)) return DownloadCheck.NoDigest;
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? DownloadCheck.Verified : DownloadCheck.Mismatch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadCheck.Mismatch;      // a file that can't be read can't be shown to be GitHub's
        }
    }

    /// <summary>The checksum in GitHub's "digest" field ("sha256:" and 64 hex digits) as lower-case hex; null for
    /// anything else.</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>ApplyUpdate's first step: throws unless the file is the one GitHub published, or it can't be checked
    /// because GitHub gave no checksum and the player has agreed (the dialog's second click) to install it unchecked.</summary>
    public void RequireVerified(string downloadedExePath, bool allowUnverified)
    {
        var check = Verify(downloadedExePath);
        if (check == DownloadCheck.Mismatch)
            throw new UpdateIntegrityException("The downloaded file doesn't match the checksum GitHub publishes for this release. Nothing was installed.");
        if (check == DownloadCheck.NoDigest && !allowUnverified)
            throw new UpdateIntegrityException("GitHub gave no checksum for this release, and installing without one was not confirmed. Nothing was installed.");
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
        foreach (var (name, value) in UpdaterEnvironment(appDir, AppPaths.SettingsPath, AppPaths.BackupsDir, downloadedExePath))
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

    /// <summary>The paths the updater script reads (as $env:EMP_…). Environment variables are Unicode and never parsed
    /// as code, so any folder name works and none can inject a command.</summary>
    public static IReadOnlyList<(string Name, string Value)> UpdaterEnvironment(string appDir, string settingsFile, string backupDir,
        string downloadedExePath) => new[]
    {
        ("EMP_APPDIR", Path.TrimEndingDirectorySeparator(appDir)), ("EMP_CONFIG", settingsFile), ("EMP_BACKUPS", backupDir), ("EMP_NEWEXE", downloadedExePath),
    };

    /// <summary>The updater: waits for MultiPreview to exit, copies the settings file into the Backups folder of the
    /// settings folder (refusing a Backups folder that is a link), replaces the exe in $env:EMP_APPDIR and starts it again.
    /// A constant, ASCII only, holding no path (see UpdaterEnvironment). Cmdlets and property access only, no .NET calls,
    /// so it also runs in Constrained Language mode (AppLocker/WDAC); only forms that Windows PowerShell 5.1 has.</summary>
    public const string UpdaterScript = @"
# EVE MultiPreview Auto-Updater (paths come from the EMP_* environment variables)
$appDir = $env:EMP_APPDIR
$configFile = $env:EMP_CONFIG
$backupDir = $env:EMP_BACKUPS
$newExe = $env:EMP_NEWEXE
if (-not $appDir -or -not $newExe) { exit 1 }

# Wait for the main process to exit
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

# Replace the executable
$oldExe = Join-Path $appDir 'EVE MultiPreview.exe'
Copy-Item -LiteralPath $newExe -Destination $oldExe -Force

# Clean up any stale dot-named exe / pdb left over from earlier installs
# that pre-date the AssemblyName rename. The GitHub release ships the file
# as 'EVE.MultiPreview.exe' (with a dot, GitHub-friendly), but the local
# install is 'EVE MultiPreview.exe' (with a space). Old dot-named files
# from previous versions sit alongside the space-named live exe and show
# up as confusing duplicates in the install folder (issue #42, bug #3).
$dotExe = Join-Path $appDir 'EVE.MultiPreview.exe'
if (Test-Path -LiteralPath $dotExe) {
    Remove-Item -LiteralPath $dotExe -Force -ErrorAction SilentlyContinue
}
$dotPdb = Join-Path $appDir 'EVE.MultiPreview.pdb'
if (Test-Path -LiteralPath $dotPdb) {
    Remove-Item -LiteralPath $dotPdb -Force -ErrorAction SilentlyContinue
}

# Restart the app (a bracketed current folder breaks Start-Process on 5.1, so start from TEMP)
Start-Process -FilePath $oldExe -WorkingDirectory $env:TEMP

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
    /// or null when GitHub's answer had none.</summary>
    public sealed record ReleaseInfo(string Version, string DownloadUrl, string? Notes, string? PageUrl, bool PreRelease, string? Sha256 = null);

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

        string? url = null, sha256 = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (Text(asset, "name") == EXE_ASSET_NAME)
                {
                    url = Text(asset, "browser_download_url");
                    sha256 = DigestOf(asset);      // the exe's own checksum, never another file's
                }
        if (url == null || !url.StartsWith(ASSET_URL_PREFIX, StringComparison.Ordinal)) return null;

        bool pre = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
        return new ReleaseInfo(tag!, url, Text(release, "body"), Text(release, "html_url"), pre, sha256);
    }

    /// <summary>The checksum of one asset: null only when the asset has no "digest" (absent or JSON null). A digest that
    /// is there but isn't "sha256:" and 64 hex digits is UnreadableDigest, which no file matches.</summary>
    private static string? DigestOf(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind == JsonValueKind.Null) return null;
        return (digest.ValueKind == JsonValueKind.String ? ParseDigest(digest.GetString()) : null) ?? UnreadableDigest;
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

    /// <summary>Aim this service at a chosen release, so the normal DownloadUpdateAsync →
    /// ApplyUpdate path (config backup, exe swap, relaunch) installs it.</summary>
    public void Select(ReleaseInfo release)
    {
        LatestVersion = release.Version;
        DownloadUrl = release.DownloadUrl;
        ReleaseNotes = release.Notes;
        ReleasePageUrl = release.PageUrl;
        ExpectedSha256 = release.Sha256;
    }
}

/// <summary>An update that must not be installed: the download isn't the file GitHub published for the release, or it
/// can't be checked and installing it unchecked was not confirmed (IDEAS #53).</summary>
public sealed class UpdateIntegrityException : Exception
{
    public UpdateIntegrityException(string message) : base(message) { }
}
