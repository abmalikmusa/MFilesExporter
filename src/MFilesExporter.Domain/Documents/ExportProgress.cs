namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Point-in-time snapshot of an export run's progress. Immutable and safe to
/// serialize into a manifest, a metrics payload, or a dashboard tile.
///
/// One <see cref="ExportProgress"/> represents *one* publication event (one
/// row appended to the tracking DB's <c>ExportProgress</c> table). For
/// aggregate lifetime statistics see <c>ExportStatistics</c>.
/// </summary>
public sealed record ExportProgress
{
    /// <summary>Total number of terminal outcomes recorded (Succeeded + Failed + Skipped).</summary>
    public required long TotalRecorded { get; init; }

    /// <summary>Terminal Succeeded outcomes since the job started.</summary>
    public required long TotalSucceeded { get; init; }

    /// <summary>Terminal Failed outcomes since the job started.</summary>
    public required long TotalFailed { get; init; }

    /// <summary>Terminal Skipped outcomes since the job started.</summary>
    public required long TotalSkipped { get; init; }

    /// <summary>Total bytes written to durable storage since the job started.</summary>
    public required long TotalBytesWritten { get; init; }

    /// <summary>
    /// Last-observed enumeration checkpoint. <c>null</c> means the pipeline
    /// has not yet advanced past origin.
    /// </summary>
    public required DocumentFileVersionKey? LastCheckpoint { get; init; }

    /// <summary>UTC start time of the containing job.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC time of this observation.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Time elapsed since the job started.</summary>
    public TimeSpan Elapsed => ObservedAtUtc - StartedAtUtc;

    /// <summary>Documents/second averaged from job start to <see cref="ObservedAtUtc"/>.</summary>
    public double DocumentsPerSecond =>
        Elapsed.TotalSeconds > 0
            ? (TotalSucceeded + TotalFailed + TotalSkipped) / Elapsed.TotalSeconds
            : 0;

    /// <summary>MiB/second averaged from job start to <see cref="ObservedAtUtc"/>.</summary>
    public double MebibytesPerSecond =>
        Elapsed.TotalSeconds > 0
            ? TotalBytesWritten / Elapsed.TotalSeconds / (1024d * 1024d)
            : 0;
}
