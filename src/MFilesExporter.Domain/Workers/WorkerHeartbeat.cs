namespace MFilesExporter.Domain.Workers;

/// <summary>
/// A single liveness signal emitted by an <see cref="ExportWorker"/>. The
/// worker publishes one heartbeat per configurable interval; the tracking DB
/// stores only the most recent (<see cref="ExportWorker.LastHeartbeat"/>).
/// This record type is what flows on the wire from worker to tracking DB.
/// </summary>
public sealed record WorkerHeartbeat
{
    /// <summary>Which worker is beating.</summary>
    public required ExportWorkerId WorkerId { get; init; }

    /// <summary>Reported status at the moment of the beat.</summary>
    public required WorkerStatus ReportedStatus { get; init; }

    /// <summary>UTC timestamp at which the worker generated this beat.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>
    /// Optional current-batch pointer — the batch the worker is presently
    /// processing. Useful for detecting workers that are stuck on a single
    /// batch for too long.
    /// </summary>
    public Batches.ExportBatchId? CurrentBatchId { get; init; }

    /// <summary>Optional throughput sample captured with the beat.</summary>
    public double? DocumentsPerSecond { get; init; }

    /// <summary>Optional bytes-per-second sample.</summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>Convenience: age of the beat relative to <paramref name="now"/>.</summary>
    public TimeSpan Age(DateTimeOffset now) => now - ObservedAtUtc;
}
