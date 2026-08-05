namespace MFilesExporter.Application.Abstractions.Dashboard;

/// <summary>
/// Point-in-time source of truth for the console dashboard. Aggregates progress,
/// worker activity, batch state, and system-resource sampling behind a single
/// pull-based facade so the renderer never touches the underlying components.
/// </summary>
public interface IDashboardStateSource
{
    DashboardSnapshot GetSnapshot();
}

/// <summary>
/// Immutable snapshot of everything the dashboard needs to paint one frame.
/// Values that are not yet known are populated with sensible defaults —
/// <c>TotalExpected = 0</c> means the target count is not resolved yet.
/// </summary>
public sealed record DashboardSnapshot
{
    // ---------------------------------------------------------------------
    // Job / progress
    // ---------------------------------------------------------------------
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public TimeSpan Elapsed => ObservedAtUtc - StartedAtUtc;

    public required long TotalExpected { get; init; }
    public required long TotalProcessed { get; init; }
    public required long TotalSucceeded { get; init; }
    public required long TotalFailed { get; init; }
    public required long TotalSkipped { get; init; }
    public required long TotalBytesWritten { get; init; }
    public required long TotalRetries { get; init; }

    public long Remaining => Math.Max(0, TotalExpected - TotalProcessed);

    // ---------------------------------------------------------------------
    // Throughput
    // ---------------------------------------------------------------------
    public double DocumentsPerSecond { get; init; }
    public double MegabytesPerSecond { get; init; }
    public double? EtaSeconds { get; init; }

    // ---------------------------------------------------------------------
    // Batch + worker activity
    // ---------------------------------------------------------------------
    public string? CurrentBatchId { get; init; }
    public long CurrentBatchSize { get; init; }
    public long CurrentBatchProcessed { get; init; }

    public required IReadOnlyList<WorkerActivityEntry> Workers { get; init; }

    // ---------------------------------------------------------------------
    // System resources
    // ---------------------------------------------------------------------
    public required long ProcessMemoryBytes { get; init; }
    public required double CpuUsagePercent { get; init; }
    public long DiskFreeBytes { get; init; }
}
