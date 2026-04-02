using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
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

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Current app version from assembly metadata.</summary>
    public string CurrentVersion { get; }

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

    public UpdateService()
    {
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Query the GitHub Releases API and determine if an update is available.
    /// Returns true if an update is available.
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(bool allowPreRelease = false)
    {
        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EVE-MultiPreview/" + CurrentVersion);

            string apiUrl = allowPreRelease 
                ? "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases" 
                : "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest";

            var json = await _httpClient.GetStringAsync(apiUrl);

            // Parse tag_name
            var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            var urlMatch = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");
            var bodyMatch = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

            if (!tagMatch.Success) return false;

            var latestTag = tagMatch.Groups[1].Value.TrimStart('v', 'V');
            LatestVersion = latestTag;
            ReleasePageUrl = urlMatch.Success ? urlMatch.Groups[1].Value : null;
            ReleaseNotes = bodyMatch.Success ? Regex.Unescape(bodyMatch.Groups[1].Value) : null;

            // Find the exe asset download URL
            // Pattern: "browser_download_url":"https://...EVE.MultiPreview.exe"
            var assetPattern = $"\"browser_download_url\"\\s*:\\s*\"([^\"]*{Regex.Escape(EXE_ASSET_NAME)})\"";
            var assetMatch = Regex.Match(json, assetPattern);
            DownloadUrl = assetMatch.Success ? assetMatch.Groups[1].Value : null;

            if (Version.TryParse(latestTag, out var latest) && Version.TryParse(CurrentVersion, out var current))
            {
                UpdateAvailable = latest > current && DownloadUrl != null;
            }

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

        var tempDir = Path.Combine(Path.GetTempPath(), "EVEMultiPreview_update");
        Directory.CreateDirectory(tempDir);
        var destPath = Path.Combine(tempDir, EXE_ASSET_NAME);

        // Delete any old download
        if (File.Exists(destPath)) File.Delete(destPath);

        using var response = await _httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloadedBytes = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;
            if (totalBytes > 0)
                progress?.Report((double)downloadedBytes / totalBytes);
        }

        progress?.Report(1.0);
        Debug.WriteLine($"[Update] Downloaded {downloadedBytes:N0} bytes to {destPath}");
        return destPath;
    }

    /// <summary>
    /// Spawn the PowerShell updater script that waits for exit, backs up config,
    /// replaces the exe, and relaunches. Then shuts down the current app.
    /// </summary>
    public void ApplyUpdate(string downloadedExePath)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var scriptPath = Path.Combine(Path.GetTempPath(), "EVEMultiPreview_update", "update.ps1");

        var script = $@"
# EVE MultiPreview Auto-Updater
# Wait for the main process to exit
Start-Sleep -Seconds 2
$maxWait = 30; $waited = 0
while ((Get-Process -Name 'EVE MultiPreview' -ErrorAction SilentlyContinue) -and $waited -lt $maxWait) {{
    Start-Sleep -Seconds 1; $waited++
}}

# Backup config file
$appDir = '{EscapePs(appDir)}'
$configFile = Join-Path $appDir 'EVE MultiPreview.json'
if (Test-Path $configFile) {{
    $backupDir = Join-Path $appDir 'Backups'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $timestamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    Copy-Item $configFile (Join-Path $backupDir ""EVE MultiPreview_pre-update_$timestamp.json"")
}}

# Replace the executable
$newExe = '{EscapePs(downloadedExePath)}'
$oldExe = Join-Path $appDir 'EVE MultiPreview.exe'
Copy-Item $newExe $oldExe -Force

# Restart the app
Start-Process $oldExe

# Clean up temp download
Start-Sleep -Seconds 3
Remove-Item -Path (Split-Path $newExe -Parent) -Recurse -Force -ErrorAction SilentlyContinue
";

        File.WriteAllText(scriptPath, script);

        // Launch PowerShell hidden
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);

        Debug.WriteLine("[Update] Updater script launched — shutting down app");
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    private static string EscapePs(string path) => path.Replace("'", "''");
}
