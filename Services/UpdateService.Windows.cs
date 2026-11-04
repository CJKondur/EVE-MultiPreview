using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>The Windows half of UpdateService: the exe asset it downloads, and the PowerShell updater that replaces the exe
/// after MultiPreview exits. The Linux app has its own half (Linux/UpdateService.Linux.cs).</summary>
public sealed partial class UpdateService
{
    private const string EXE_ASSET_NAME = "EVE.MultiPreview.exe";

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
        long verifiedLength = RequireVerified(downloadedExePath, allowUnverified);
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
        foreach (var (name, value) in UpdaterEnvironment(appDir, AppPaths.SettingsPath, AppPaths.BackupsDir, downloadedExePath, Environment.ProcessId, Environment.ProcessPath, verifiedLength))
            psi.Environment[name] = value;
        // IDEAS P60 (4)(5): the next start reads this to tell the player when the install did not happen. The script
        // knows nothing of it, and a start that throws leaves no marker: nothing was started that could fail.
        PendingUpdate.Write(AppPaths.ConfigDir, CurrentVersion, LatestVersion ?? "?", CheckWas, DateTime.UtcNow);
        try
        {
            Process.Start(psi);
        }
        catch
        {
            PendingUpdate.Clear(AppPaths.ConfigDir);
            throw;
        }

        Debug.WriteLine("[Update] Updater script launched — shutting down app");
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
        return handBack;
    }

    /// <summary>What the updater script reads (as $env:EMP_…): the four paths, the id of the process it must wait for
    /// (this one) and the path of the exe that is running (this one's; "" when not known), which it starts again when
    /// the new exe could not be put in place, and the length of the downloaded exe as it was verified (EMP_LEN, a plain
    /// decimal number), which the script compares with the file's length before it copies anything. Environment variables are Unicode and never parsed as code, so any
    /// folder name works and none can inject a command.</summary>
    public static IReadOnlyList<(string Name, string Value)> UpdaterEnvironment(string appDir, string settingsFile, string backupDir,
        string downloadedExePath, int processId, string? runningExe, long verifiedLength) => new[]
    {
        ("EMP_APPDIR", Path.TrimEndingDirectorySeparator(appDir)), ("EMP_CONFIG", settingsFile), ("EMP_BACKUPS", backupDir), ("EMP_NEWEXE", downloadedExePath),
        ("EMP_PID", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("EMP_EXE", runningExe ?? ""),
        ("EMP_LEN", verifiedLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
# was checked, changed after it was checked, or the exe in the app folder is still locked. What it answers
# decides the rest. The length of the download must be the length that was verified (EMP_LEN); a missing or
# different one copies nothing. The copy goes to a second name beside the old exe, its length is checked
# again, and only then does a rename put it over the old exe: a copy that dies half way leaves the old exe whole.
# The rename is NOT atomic: in Windows PowerShell 5.1 Move-Item -Force over an existing file deletes the old
# file and then renames. If the old exe is still locked the delete fails: the old exe stays and is restarted.
# If the delete succeeds and the rename then fails (rare), there is no exe, only 'EVE MultiPreview.exe.new'.
$oldExe = Join-Path $appDir 'EVE MultiPreview.exe'
$dotExe = Join-Path $appDir 'EVE.MultiPreview.exe'
$dotPdb = Join-Path $appDir 'EVE.MultiPreview.pdb'
$tmpExe = Join-Path $appDir 'EVE MultiPreview.exe.new'
$got = (Get-Item -LiteralPath $newExe -ErrorAction SilentlyContinue).Length
if (-not $env:EMP_LEN -or $got -ne $env:EMP_LEN) {
    $copied = $false
} else {
    Copy-Item -LiteralPath $newExe -Destination $tmpExe -Force
    if (-not $? -or (Get-Item -LiteralPath $tmpExe -ErrorAction SilentlyContinue).Length -ne $env:EMP_LEN) {
        $copied = $false
    } else {
        Move-Item -LiteralPath $tmpExe -Destination $oldExe -Force
        $copied = $?
    }
}

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
    if (-not $startExe -or -not (Test-Path -LiteralPath $startExe -PathType Leaf)) { $startExe = $oldExe }
    if (-not (Test-Path -LiteralPath $startExe -PathType Leaf)) { $startExe = $dotExe }
}

# Restart the app (a bracketed current folder breaks Start-Process on 5.1, so start from TEMP)
if (Test-Path -LiteralPath $startExe -PathType Leaf) {
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
}
