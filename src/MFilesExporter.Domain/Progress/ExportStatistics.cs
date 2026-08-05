using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.Progress;

/// <summary>
/// Aggregate lifetime statistics for a single job. Unlike
/// <c>ExportProgress</c> — which is a single snapshot event — this record
/// is intended for the "final summary" surface: run reports, dashboards
/// showing terminal totals, and API responses.
/// </summary>
public sealed record ExportStatistics
{
    /// <summary>Owning job.</summary>
    public required ExportJobId JobId { get; init; }

    /// <summary>UTC start time.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC completion time (or <c>null</c> when the job is still running).</summary>
    public required DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Total terminal outcomes.</summary>
    public required long TotalRecorded { get; init; }

    /// <summary>Successful outcomes.</summary>
    public required long TotalSucceeded { get; init; }

    /// <summary>Failed outcomes.</summary>
    public required long TotalFailed { get; init; }

    /// <summary>Skipped outcomes.</summary>
    public required long TotalSkipped { get; init; }

    /// <summary>Total bytes written to durable storage.</summary>
    public required long TotalBytesWritten { get; init; }

    /// <summary>Peak (highest observed) documents-per-second throughput.</summary>
    public required double PeakDocumentsPerSecond { get; init; }

    /// <summary>Peak (highest observed) MiB/second throughput.</summary>
    public required double PeakMebibytesPerSecond { get; init; }

    /// <summary>Total elapsed time (or <c>null</c> if still running).</summary>
    public TimeSpan? Elapsed =>
        CompletedAtUtc is null ? null : CompletedAtUtc.Value - StartedAtUtc;

    /// <summary>Average documents/second across the run (or <c>null</c> if still running).</summary>
    public double? AverageDocumentsPerSecond =>
        Elapsed is null || Elapsed.Value.TotalSeconds <= 0
            ? null
            : (TotalSucceeded + TotalFailed + TotalSkipped) / Elapsed.Value.TotalSeconds;

    /// <summary>Average MiB/second across the run (or <c>null</c> if still running).</summary>
    public double? AverageMebibytesPerSecond =>
        Elapsed is null || Elapsed.Value.TotalSeconds <= 0
            ? null
            : TotalBytesWritten / Elapsed.Value.TotalSeconds / (1024d * 1024d);

    /// <summary>Ratio of failures to total outcomes; useful for SLO reporting.</summary>
    public double FailureRatio =>
        TotalRecorded == 0 ? 0.0 : (double)TotalFailed / TotalRecorded;
}
