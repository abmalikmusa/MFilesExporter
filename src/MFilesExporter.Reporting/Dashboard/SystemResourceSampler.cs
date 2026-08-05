using System.Diagnostics;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// Samples process CPU time and working-set memory. Kept simple — the
/// dashboard's CPU number is a rolling average of CPU-time delta over
/// wall-clock delta divided by <see cref="Environment.ProcessorCount"/>.
/// </summary>
public sealed class SystemResourceSampler
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly int _cpuCount = Environment.ProcessorCount;
    private readonly object _lock = new();

    private DateTimeOffset _lastSampleAt;
    private TimeSpan _lastCpuTime;
    private double _lastCpuPercent;

    public SystemResourceSampler()
    {
        _lastSampleAt = DateTimeOffset.UtcNow;
        _lastCpuTime  = _process.TotalProcessorTime;
    }

    /// <summary>Returns (memoryBytes, cpuPercent). CPU is 0..100 across all cores.</summary>
    public (long MemoryBytes, double CpuPercent) Sample()
    {
        lock (_lock)
        {
            _process.Refresh();
            var now       = DateTimeOffset.UtcNow;
            var cpuNow    = _process.TotalProcessorTime;
            var elapsed   = (now - _lastSampleAt).TotalSeconds;
            var cpuDelta  = (cpuNow - _lastCpuTime).TotalSeconds;

            if (elapsed >= 0.25)   // debounce: recompute only every 250 ms
            {
                _lastCpuPercent = elapsed > 0 && _cpuCount > 0
                    ? Math.Clamp((cpuDelta / elapsed / _cpuCount) * 100.0, 0.0, 100.0)
                    : 0.0;
                _lastSampleAt = now;
                _lastCpuTime  = cpuNow;
            }

            return (_process.WorkingSet64, _lastCpuPercent);
        }
    }
}
