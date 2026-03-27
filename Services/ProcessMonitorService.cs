using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>
/// Polls per-process CPU and RAM usage for EVE Online clients.
/// Updates every 3 seconds. Thread-safe for cross-thread reads.
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    public record ProcessStats(double CpuPercent, long RamMB);

    private readonly ConcurrentDictionary<int, ProcessStats> _stats = new();
    private readonly ConcurrentDictionary<int, (TimeSpan LastCpu, DateTime LastTime)> _prevCpu = new();
    private System.Threading.Timer? _timer;
    private int _processorCount = Environment.ProcessorCount;

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

                    _prevCpu[pid] = (totalCpu, now);
                    _stats[pid] = new ProcessStats(Math.Round(cpuPercent, 1), ramMB);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcMon:Poll] ⚠ Error reading PID {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose(); // Release native process handle
                }
            }

            // Clean up stale entries for processes no longer running
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

    /// <summary>Get stats for a specific process ID.</summary>
    public ProcessStats? GetStats(int pid)
    {
        return _stats.TryGetValue(pid, out var stats) ? stats : null;
    }

    /// <summary>Format stats as a compact display string.</summary>
    public static string FormatStats(ProcessStats stats)
    {
        return $"CPU: {stats.CpuPercent:F0}% | RAM: {stats.RamMB}MB";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        Debug.WriteLine("[ProcMon:Stop] 🛑 Stopped");
    }
}
