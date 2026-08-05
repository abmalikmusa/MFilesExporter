using System.Diagnostics.Metrics;
using MFilesExporter.Application.Abstractions.Monitoring;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// Registers the observable gauges that describe the exporter's running
/// state — queue depths, worker utilization, disk free bytes, and the
/// projected ETA. The gauges pull their values from the injected providers
/// every time the OpenTelemetry meter reader ticks.
/// </summary>
/// <remarks>
/// <para>
/// Registration is idempotent — a singleton in DI. Callbacks are pull-based
/// so we never hold a scheduler thread; the meter reader chooses the cadence.
/// </para>
/// <para>
/// If no <see cref="IQueueDepthProvider"/> or <see cref="IWorkerUtilizationProvider"/>
/// is registered, the corresponding gauge simply emits no measurements —
/// this keeps the class safe to add to tests without dragging in the
/// full pipeline.
/// </para>
/// </remarks>
public sealed class ObservableGaugeRegistry : IDisposable
{
    private readonly ExporterMetrics _metrics;
    private readonly IReadOnlyList<IQueueDepthProvider> _queues;
    private readonly IWorkerUtilizationProvider? _workers;
    private readonly IProgressSnapshotProvider? _progress;
    private readonly StorageOptions _storage;

    public ObservableGaugeRegistry(
        ExporterMetrics metrics,
        IEnumerable<IQueueDepthProvider> queues,
        StorageOptions storage,
        IWorkerUtilizationProvider? workers = null,
        IProgressSnapshotProvider? progress = null)
    {
        _metrics  = metrics  ?? throw new ArgumentNullException(nameof(metrics));
        _queues   = (queues  ?? []).ToArray();
        _storage  = storage  ?? throw new ArgumentNullException(nameof(storage));
        _workers  = workers;
        _progress = progress;

        var meter = _metrics.Meter;

        meter.CreateObservableGauge(
            "mfilesexporter.queue.depth",
            ObserveQueueDepth,
            unit: "{item}",
            description: "Current number of items buffered per named queue.");

        meter.CreateObservableGauge(
            "mfilesexporter.queue.capacity_ratio",
            ObserveQueueRatio,
            unit: "1",
            description: "Depth / capacity — 1.0 means the producer is fully back-pressured.");

        meter.CreateObservableGauge(
            "mfilesexporter.workers.busy",
            () => _workers is null ? Array.Empty<Measurement<int>>() : new[] { new Measurement<int>(_workers.BusyWorkers) },
            unit: "{worker}",
            description: "Workers currently executing a work item.");

        meter.CreateObservableGauge(
            "mfilesexporter.workers.utilization",
            ObserveWorkerUtilization,
            unit: "1",
            description: "Busy / configured — 0.0 idle, 1.0 fully loaded.");

        meter.CreateObservableGauge(
            "mfilesexporter.workers.stalled",
            () => _workers is null ? Array.Empty<Measurement<int>>() : new[] { new Measurement<int>(_workers.StalledWorkers) },
            unit: "{worker}",
            description: "Workers with no heartbeat within the stalled threshold.");

        meter.CreateObservableGauge(
            "mfilesexporter.disk.free_bytes",
            ObserveDiskFree,
            unit: "By",
            description: "Free bytes on the output volume.");

        meter.CreateObservableGauge(
            "mfilesexporter.disk.free_ratio",
            ObserveDiskFreeRatio,
            unit: "1",
            description: "Free bytes / total bytes — early-warning for disk exhaustion.");

        meter.CreateObservableGauge(
            "mfilesexporter.eta.seconds",
            ObserveEtaSeconds,
            unit: "s",
            description: "Estimated seconds remaining based on rolling documents-per-second.");
    }

    // ---------------------------------------------------------------
    // Callbacks
    // ---------------------------------------------------------------

    private IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        foreach (var q in _queues)
        {
            yield return new Measurement<int>(q.Depth,
                new KeyValuePair<string, object?>("queue", q.Name));
        }
    }

    private IEnumerable<Measurement<double>> ObserveQueueRatio()
    {
        foreach (var q in _queues)
        {
            if (q.Capacity is not int cap || cap <= 0) continue;
            yield return new Measurement<double>((double)q.Depth / cap,
                new KeyValuePair<string, object?>("queue", q.Name));
        }
    }

    private IEnumerable<Measurement<double>> ObserveWorkerUtilization()
    {
        if (_workers is null) yield break;
        var cap = _workers.WorkerCount;
        var ratio = cap > 0 ? (double)_workers.BusyWorkers / cap : 0.0;
        yield return new Measurement<double>(ratio);
    }

    private IEnumerable<Measurement<long>> ObserveDiskFree()
    {
        var root = ResolveVolumeRoot(_storage.RootPath);
        if (root is null) yield break;

        var info = new DriveInfo(root);
        if (!info.IsReady) yield break;

        yield return new Measurement<long>(info.AvailableFreeSpace,
            new KeyValuePair<string, object?>("volume", info.Name));
    }

    private IEnumerable<Measurement<double>> ObserveDiskFreeRatio()
    {
        var root = ResolveVolumeRoot(_storage.RootPath);
        if (root is null) yield break;

        var info = new DriveInfo(root);
        if (!info.IsReady || info.TotalSize <= 0) yield break;

        yield return new Measurement<double>((double)info.AvailableFreeSpace / info.TotalSize,
            new KeyValuePair<string, object?>("volume", info.Name));
    }

    private IEnumerable<Measurement<double>> ObserveEtaSeconds()
    {
        if (_progress is null) yield break;

        var seconds = EtaCalculator.EstimateSeconds(_progress);
        if (seconds is null) yield break;

        yield return new Measurement<double>(seconds.Value);
    }

    private static string? ResolveVolumeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose() { /* meter owned by ExporterMetrics */ }
}
