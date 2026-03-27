using System;
using System.Diagnostics;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>
/// Generates an RTSS (RivaTuner Statistics Server) profile for exefile.exe.
/// Supports: (1) elevated subprocess for auto-install, (2) manual clipboard install.
/// Profile format matches the AHK pre-release exactly — uses IdleLimitTime for background FPS.
/// </summary>
public static class RtssProfileService
{
    private const string RtssDefaultPath = @"C:\Program Files (x86)\RivaTuner Statistics Server";
    private const string ProfileDirName = "Profiles";
    private const string ProfileFileName = "exefile.exe.cfg";

    /// <summary>Detects if RTSS is installed by looking for RTSS.exe.</summary>
    public static bool IsInstalled()
    {
        var dir = GetInstallDir();
        bool found = dir != null && File.Exists(Path.Combine(dir, "RTSS.exe"));
        Debug.WriteLine($"[RTSS:Detect] {(found ? "✅" : "❌")} RTSS installed: {found}");
        return found;
    }

    /// <summary>Gets the RTSS install directory (checks default + Program Files + registry).</summary>
    public static string? GetInstallDir()
    {
        if (Directory.Exists(RtssDefaultPath)) return RtssDefaultPath;

        // Also check x64 Program Files
        var altPath = @"C:\Program Files\RivaTuner Statistics Server";
        if (Directory.Exists(altPath)) return altPath;

        // Check registry (WOW6432Node for 32-bit RTSS on 64-bit OS)
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS");
            var regPath = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrEmpty(regPath) && File.Exists(Path.Combine(regPath, "RTSS.exe")))
                return regPath;
        }
        catch { /* registry not available */ }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Unwinder\RTSS");
            var regPath = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrEmpty(regPath) && File.Exists(Path.Combine(regPath, "RTSS.exe")))
                return regPath;
        }
        catch { /* registry not available */ }

        return null;
    }

    /// <summary>Gets the full path where the RTSS profile should be written.</summary>
    public static string? GetProfilePath()
    {
        var installDir = GetInstallDir();
        if (installDir == null) return null;
        return Path.Combine(installDir, ProfileDirName, ProfileFileName);
    }

    /// <summary>
    /// Generates the full RTSS profile content for exefile.exe.
    /// Uses IdleLimitTime (microseconds) = 1,000,000 / targetFPS for background limiting.
    /// Profile format matches the AHK pre-release exactly.
    /// </summary>
    public static string GenerateProfileContent(int fpsLimit)
    {
        // RTSS uses IdleLimitTime in microseconds: 1,000,000 / targetFPS
        long idleTime = (long)Math.Round(1_000_000.0 / fpsLimit);
        string timestamp = DateTime.Now.ToString("dd-MM-yyyy, HH:mm:ss");

        return
            "[OSD]\n" +
            "EnableOSD=0\n" +
            "EnableBgnd=1\n" +
            "EnableFill=1\n" +
            "EnableStat=0\n" +
            "BaseColor=00FF8000\n" +
            "BgndColor=00000000\n" +
            "FillColor=80000000\n" +
            "PositionX=1\n" +
            "PositionY=1\n" +
            "ZoomRatio=2\n" +
            "CoordinateSpace=0\n" +
            "EnableFrameColorBar=0\n" +
            "FrameColorBarMode=0\n" +
            "RefreshPeriod=500\n" +
            "IntegerFramerate=1\n" +
            "MaximumFrametime=0\n" +
            "EnableFrametimeHistory=0\n" +
            "FrametimeHistoryWidth=-32\n" +
            "FrametimeHistoryHeight=-4\n" +
            "FrametimeHistoryStyle=0\n" +
            "ScaleToFit=0\n" +
            "[Statistics]\n" +
            "FramerateAveragingInterval=1000\n" +
            "PeakFramerateCalc=0\n" +
            "PercentileCalc=0\n" +
            "FrametimeCalc=0\n" +
            "PercentileBuffer=0\n" +
            "[Framerate]\n" +
            "Limit=60\n" +
            "LimitDenominator=1\n" +
            "LimitTime=0\n" +
            "LimitTimeDenominator=1\n" +
            "SyncDisplay=0\n" +
            "SyncScanline0=0\n" +
            "SyncScanline1=0\n" +
            "SyncPeriods=0\n" +
            "SyncLimiter=0\n" +
            "PassiveWait=1\n" +
            "ReflexSleep=0\n" +
            "ReflexSetLatencyMarker=1\n" +
            "EnableIdleMode=1\n" +
            "IdleModeDetectionDelay=2000\n" +
            $"IdleLimitTime={idleTime}\n" +
            "[Hooking]\n" +
            "EnableHooking=1\n" +
            "EnableFloatingInjectionAddress=0\n" +
            "EnableDynamicOffsetDetection=0\n" +
            "HookLoadLibrary=0\n" +
            "HookDirectDraw=1\n" +
            "HookDirect3D8=1\n" +
            "HookDirect3D9=1\n" +
            "HookDirect3DSwapChain9Present=1\n" +
            "HookDXGI=1\n" +
            "HookDirect3D12=1\n" +
            "HookOpenGL=1\n" +
            "HookVulkan=1\n" +
            "InjectionDelay=15000\n" +
            "UseDetours=0\n" +
            "[Font]\n" +
            "Height=-9\n" +
            "Weight=400\n" +
            "Face=Unispace\n" +
            "Load=\n" +
            "[RendererDirect3D8]\n" +
            "Implementation=2\n" +
            "[RendererDirect3D9]\n" +
            "Implementation=2\n" +
            "[RendererDirect3D10]\n" +
            "Implementation=2\n" +
            "[RendererDirect3D11]\n" +
            "Implementation=2\n" +
            "[RendererDirect3D12]\n" +
            "Implementation=2\n" +
            "[RendererOpenGL]\n" +
            "Implementation=2\n" +
            "[RendererVulkan]\n" +
            "Implementation=2\n" +
            "[Info]\n" +
            $"Timestamp={timestamp}\n";
    }

    /// <summary>
    /// Restarts RTSS so it picks up the new profile.
    /// Uses elevated ShellExecute to taskkill and relaunch since RTSS runs elevated.
    /// </summary>
    public static void RestartRtss()
    {
        var installDir = GetInstallDir();
        if (installDir == null) return;

        var rtssExe = Path.Combine(installDir, "RTSS.exe");
        bool isRunning = Process.GetProcessesByName("RTSS").Length > 0;

        if (isRunning)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c taskkill /IM RTSS.exe /F & timeout /t 1 /nobreak >nul & start \"\" \"{rtssExe}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                Debug.WriteLine("[RTSS:Restart] ✅ RTSS restarted");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Debug.WriteLine("[RTSS:Restart] ⚠ User cancelled UAC for restart");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RTSS:Restart] ❌ Error: {ex.Message}");
            }
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = rtssExe, UseShellExecute = true });
                Debug.WriteLine("[RTSS:Restart] ✅ RTSS launched");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RTSS:Restart] ❌ Could not start RTSS: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Attempts to write the RTSS profile directly (works if Profiles dir is writable).
    /// Falls back to spawning an elevated cmd /c copy to write with admin rights.
    /// Does NOT require the main app to run as admin.
    /// On success, attempts to restart RTSS so the new profile takes effect.
    /// </summary>
    public static (bool success, string message) GenerateProfile(int fpsLimit)
    {
        var installDir = GetInstallDir();
        if (installDir == null)
            return (false, "RTSS not found. Install RivaTuner Statistics Server first.");

        var profileDir = Path.Combine(installDir, ProfileDirName);
        var profilePath = Path.Combine(profileDir, ProfileFileName);
        var content = GenerateProfileContent(fpsLimit);

        // ── Try 1: Direct write (works if user has folder permissions) ──
        try
        {
            if (!Directory.Exists(profileDir))
                Directory.CreateDirectory(profileDir);

            // Backup existing profile
            if (File.Exists(profilePath))
            {
                var backup = profilePath + ".bak";
                File.Copy(profilePath, backup, overwrite: true);
                Debug.WriteLine($"[RTSS:Profile] 💾 Backup created: {backup}");
            }

            File.WriteAllText(profilePath, content);
            Debug.WriteLine($"[RTSS:Profile] ✅ Profile written directly: {profilePath} (Idle FPS={fpsLimit})");

            // Restart RTSS to pick up the new profile
            RestartRtss();

            return (true, $"RTSS profile created!\n\n📁 {profilePath}\n⚡ Idle FPS limit: {fpsLimit}\n\nRTSS has been restarted to apply the profile.");
        }
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine("[RTSS:Profile] ⚠ Direct write denied, trying elevated copy...");
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            Debug.WriteLine($"[RTSS:Profile] ❌ Direct write error: {ex.Message}");
            return (false, $"Error writing RTSS profile: {ex.Message}");
        }

        // ── Try 2: Elevated subprocess (UAC prompt for copy only) ──
        try
        {
            // Write content to a temp file, then copy it elevated
            var tempFile = Path.Combine(Path.GetTempPath(), "evex_rtss_profile.cfg");
            File.WriteAllText(tempFile, content);

            // Ensure the Profiles directory exists (elevated mkdir + copy)
            var script =
                $"if not exist \"{profileDir}\" mkdir \"{profileDir}\" & " +
                $"if exist \"{profilePath}\" copy /Y \"{profilePath}\" \"{profilePath}.bak\" & " +
                $"copy /Y \"{tempFile}\" \"{profilePath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {script}",
                Verb = "runas",          // triggers UAC prompt
                UseShellExecute = true,  // required for Verb
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);

            // Clean up temp file
            try { File.Delete(tempFile); } catch { /* ignore */ }

            if (proc?.ExitCode == 0)
            {
                Debug.WriteLine($"[RTSS:Profile] ✅ Profile written via elevation: {profilePath}");

                // Restart RTSS to pick up the new profile
                RestartRtss();

                return (true, $"RTSS profile created!\n\n📁 {profilePath}\n⚡ Idle FPS limit: {fpsLimit}\n\nRTSS has been restarted to apply the profile.");
            }
            else
            {
                Debug.WriteLine($"[RTSS:Profile] ❌ Elevated copy failed (exit={proc?.ExitCode})");
                return (false, $"Elevated copy failed. Use Manual Install instead.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC prompt
            Debug.WriteLine("[RTSS:Profile] ⚠ User cancelled UAC prompt");
            return (false, "Admin permission was declined.\nUse Manual Install to copy the file yourself.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RTSS:Profile] ❌ Elevated copy error: {ex.Message}");
            return (false, $"Error during elevated install: {ex.Message}\nUse Manual Install instead.");
        }
    }
}
