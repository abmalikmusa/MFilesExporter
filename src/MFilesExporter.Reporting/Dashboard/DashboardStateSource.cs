using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// Default <see cref="IDashboardStateSource"/>. Aggregates a
/// <see cref="ExportProgress"/> snapshot, worker activity, batch state, and
/// system-resource samples into a single <see cref="DashboardSnapshot"/>
/// the renderer paints without further lookups.
/// </summary>
public sealed class DashboardStateSource : IDashboardStateSource
{
    private readonly IExportStateStore _stateStore;
    private readonly IWorkerActivityFeed _workers;
    private readonly IBatchProgressSource? _batch;
    private readonly SystemResourceSampler _resources;
    private readonly IClock _clock;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly StorageOptions _storage;
    private readonly ITotalExpectedSource? _expected;
    private readonly IRetryCounterSource? _retries;

    private readonly object _startLock = new();
    private DateTimeOffset? _startedAt;

    public DashboardStateSource(
        IExportStateStore stateStore,
        IWorkerActivityFeed workers,
        SystemResourceSampler resources,
        IClock clock,
        MFilesSourceOptions sourceOptions,
        StorageOptions storage,
        IBatchProgressSource? batch = null,
        ITotalExpectedSource? expected = null,
        IRetryCounterSource? retries = null)
    {
        _stateStore    = stateStore;
        _workers       = workers;
        _resources     = resources;
        _clock         = clock;
        _sourceOptions = sourceOptions;
        _storage       = storage;
        _batch         = batch;
        _expected      = expected;
        _retries       = retries;
    }

    public DashboardSnapshot GetSnapshot()
    {
        var startedAt = EnsureStartedAt();
        var now       = _clock.UtcNow;

        // These calls are synchronous by contract for the dashboard path
        // (the store already exposes a synchronous fast path via the
        // in-memory counter view; the async version is used by the
        // publisher hosted service). If your store only supports async,
        // block briefly here — the tick cadence is 1 s and a snapshot
        // read must complete well under that.
        var counters   = _stateStore.GetCountersAsync(CancellationToken.None).GetAwaiter().GetResult();
        var checkpoint = _stateStore.GetCheckpointAsync(_sourceOptions.PartitionKey, CancellationToken.None).GetAwaiter().GetResult();

        var totalRecorded = counters.TotalRecorded;
        var elapsedSec    = Math.Max(0.001, (now - startedAt).TotalSeconds);
        var docsPerSec    = totalRecorded / elapsedSec;
        var mibPerSec     = counters.TotalBytesWritten / elapsedSec / (1024d * 1024d);

        var (mem, cpu) = _resources.Sample();
        var diskFree   = ResolveDiskFree(_storage.RootPath);
        var totalExpected = _expected?.TotalExpected ?? 0;
        var totalRetries  = _retries?.TotalRetries ?? 0;

        var eta = ComputeEta(totalExpected, totalRecorded, docsPerSec);

        return new DashboardSnapshot
        {
            StartedAtUtc          = startedAt,
            ObservedAtUtc         = now,
            TotalExpected         = totalExpected,
            TotalProcessed        = totalRecorded,
            TotalSucceeded        = counters.TotalSucceeded,
            TotalFailed           = counters.TotalFailed,
            TotalSkipped          = counters.TotalSkipped,
            TotalBytesWritten     = counters.TotalBytesWritten,
            TotalRetries          = totalRetries,
            DocumentsPerSecond    = docsPerSec,
            MegabytesPerSecond    = mibPerSec,
            EtaSeconds            = eta,
            CurrentBatchId        = _batch?.CurrentBatchId,
            CurrentBatchSize      = _batch?.CurrentBatchSize ?? 0,
            CurrentBatchProcessed = _batch?.CurrentBatchProcessed ?? 0,
            Workers               = _workers.Snapshot(),
            ProcessMemoryBytes    = mem,
            CpuUsagePercent       = cpu,
            DiskFreeBytes         = diskFree,
        };
    }

    private DateTimeOffset EnsureStartedAt()
    {
        lock (_startLock)
        {
            _startedAt ??= _clock.UtcNow;
            return _startedAt.Value;
        }
    }

    private static double? ComputeEta(long expected, long processed, double docsPerSec)
    {
        if (expected <= 0 || processed <= 0 || docsPerSec <= 0) return null;
        var remaining = expected - processed;
        if (remaining <= 0) return 0;
        return remaining / docsPerSec;
    }

    private static long ResolveDiskFree(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            var info = new DriveInfo(root);
            return info.IsReady ? info.AvailableFreeSpace : 0;
        }
        catch
        {
            return 0;
        }
    }
}
