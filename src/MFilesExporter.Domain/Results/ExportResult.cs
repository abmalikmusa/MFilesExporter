using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;
using MFilesExporter.Domain.Workers;

namespace MFilesExporter.Domain.Results;

/// <summary>
/// Per-document terminal outcome. This is the atom of everything else — one
/// <see cref="ExportResult"/> per document, then all downstream aggregates
/// (statistics, manifest entries, checkpoints) are derived from the stream.
///
/// The type is distinct from <c>ExportOutcome</c> (which is optimized for
/// pipeline throughput and omits some audit fields) because operator-facing
/// consumers want the fuller record with job/worker context.
/// </summary>
public sealed record ExportResult
{
    /// <summary>Owning job.</summary>
    public required ExportJobId JobId { get; init; }

    /// <summary>Worker that produced the outcome, if known.</summary>
    public required ExportWorkerId? WorkerId { get; init; }

    /// <summary>Unique idempotency key for the source triple.</summary>
    public required IdempotencyKey IdempotencyKey { get; init; }

    /// <summary>Metadata source cursor.</summary>
    public required DocumentFileVersionKey DocumentFileVersionKey { get; init; }

    /// <summary>BLOB source key.</summary>
    public required DataFileVersionKey DataFileVersionKey { get; init; }

    /// <summary>Terminal state.</summary>
    public required ExportStatus Status { get; init; }

    /// <summary>Bytes written by the sink; 0 for Skipped/Failed.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Final path of the sink artifact (or <c>null</c> when nothing was written).</summary>
    public string? OutputPath { get; init; }

    /// <summary>Hex-encoded SHA-256 of the written payload (or <c>null</c>).</summary>
    public string? PayloadChecksum { get; init; }

    /// <summary>Reason string for Failed / Skipped outcomes.</summary>
    public string? FailureReason { get; init; }

    /// <summary>1-based attempt that produced this outcome.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>UTC time the outcome was observed by the pipeline.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>True when this outcome represents a durable artifact.</summary>
    public bool IsArtifactBearing => Status == ExportStatus.Succeeded && OutputPath is not null;
}
