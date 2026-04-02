using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>
/// Polls per-process CPU, RAM, and VRAM usage for EVE Online clients.
/// Updates every 3 seconds. Thread-safe for cross-thread reads.
/// VRAM is queried via Windows Performance Counters (GPU Process Memory category).
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    public record ProcessStats(double CpuPercent, long RamMB, long VramMB);

    private readonly ConcurrentDictionary<int, ProcessStats> _stats = new();
    private readonly ConcurrentDictionary<int, (TimeSpan LastCpu, DateTime LastTime)> _prevCpu = new();
    private System.Threading.Timer? _timer;
    private int _processorCount = Environment.ProcessorCount;

    // VRAM: pre-built PID→bytes map, refreshed once per poll cycle
    private bool _gpuCountersAvailable = true;

    /// <summary>Fires after each poll cycle with updated stats.</summary>
    public event Action? StatsUpdated;

    public void Start()
    {
        _timer = new System.Threading.Timer(PollStats, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Debug.WriteLine($"[ProcMon:Start] ✅ Started — polling every 3s ({_processorCount} cores)");
    }

    private void PollStats(object? state)
    {
        try
        {
            var eveProcesses = Process.GetProcessesByName("exefile");
            var activePids = new HashSet<int>();

            // Build VRAM map ONCE per poll cycle (not per-PID)
            var vramMap = BuildVramMap();

            foreach (var proc in eveProcesses)
            {
                try
                {
                    int pid = proc.Id;
                    activePids.Add(pid);

                    var now = DateTime.UtcNow;
                    var totalCpu = proc.TotalProcessorTime;
                    long ramMB = proc.WorkingSet64 / (1024 * 1024);

                    double cpuPercent = 0;
                    if (_prevCpu.TryGetValue(pid, out var prev))
                    {
                        double elapsed = (now - prev.LastTime).TotalSeconds;
                        if (elapsed > 0)
                        {
                            double cpuUsed = (totalCpu - prev.LastCpu).TotalSeconds;
                            cpuPercent = (cpuUsed / elapsed / _processorCount) * 100;
                            cpuPercent = Math.Min(cpuPercent, 100); // clamp
                        }
                    }

                    // Lookup VRAM from pre-built map
                    long vramMB = vramMap.TryGetValue(pid, out var vramBytes)
                        ? vramBytes / (1024 * 1024)
                        : 0;

                    _prevCpu[pid] = (totalCpu, now);
                    _stats[pid] = new ProcessStats(Math.Round(cpuPercent, 1), ramMB, vramMB);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcMon:Poll] ⚠ Error reading PID {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }

            // Clean up stale entries
            foreach (var pid in _stats.Keys.Where(p => !activePids.Contains(p)).ToList())
            {
                _stats.TryRemove(pid, out _);
                _prevCpu.TryRemove(pid, out _);
            }

            StatsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcMon:Poll] ❌ Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerate ALL GPU Process Memory counter instances once and build
    /// a PID → total dedicated VRAM (bytes) map. This is much more reliable
    /// than per-PID lookups because we scan all instances in one pass.
    /// </summary>
    private Dictionary<int, long> BuildVramMap()
    {
        var map = new Dictionary<int, long>();
        if (!_gpuCountersAvailable) return map;

        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            var instances = category.GetInstanceNames();

            foreach (var instance in instances)
            {
                // Instance format: "pid_12345_luid_0x00000000_0x0000XXXX_phys_0"
                // Extract PID from between "pid_" and next "_"
                if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                    continue;

                int underscoreIdx = instance.IndexOf('_', 4);
                if (underscoreIdx < 0) continue;

                if (!int.TryParse(instance.AsSpan(4, underscoreIdx - 4), out int pid))
                    continue;

                try
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, true);
                    long bytes = (long)counter.NextValue();

                    // Sum across all GPU adapters for this PID
                    if (map.TryGetValue(pid, out var existing))
                        map[pid] = Math.Max(existing, bytes); // Take the max (primary adapter)
                    else
                        map[pid] = bytes;
                }
                catch
                {
                    // Individual counter read failure — skip this instance
                }
            }
        }
        catch (InvalidOperationException)
        {
            _gpuCountersAvailable = false;
            Debug.WriteLine("[ProcMon:VRAM] ⚠ GPU Process Memory counters not available on this system");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcMon:VRAM] ⚠ VRAM enumeration failed: {ex.Message}");
        }

        return map;
    }

    /// <summary>Get stats for a specific process ID.</summary>
    public ProcessStats? GetStats(int pid)
    {
        return _stats.TryGetValue(pid, out var stats) ? stats : null;
    }

    /// <summary>Format stats as a compact display string.</summary>
    public static string FormatStats(ProcessStats stats)
    {
        if (stats.VramMB > 0)
            return $"CPU: {stats.CpuPercent:F0}%\nRAM: {stats.RamMB}MB\nVRAM:{stats.VramMB}MB";
        return $"CPU: {stats.CpuPercent:F0}%\nRAM: {stats.RamMB}MB";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        Debug.WriteLine("[ProcMon:Stop] 🛑 Stopped");
    }
}
