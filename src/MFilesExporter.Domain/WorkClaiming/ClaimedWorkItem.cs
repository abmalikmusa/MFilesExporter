using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.WorkClaiming;

/// <summary>
/// A single work item successfully claimed by a worker. Carries everything
/// the worker needs to (a) fetch the BLOB, (b) prove its ownership on
/// complete/fail/renew, and (c) know when its lease expires.
/// </summary>
public sealed record ClaimedWorkItem
{
    /// <summary>Surrogate id of the underlying work-item row.</summary>
    public required WorkItemId WorkItemId { get; init; }

    /// <summary>Owning job.</summary>
    public required ExportJobId JobId { get; init; }

    /// <summary>Deterministic idempotency key for the source triple.</summary>
    public required IdempotencyKey IdempotencyKey { get; init; }

    /// <summary>Source metadata cursor.</summary>
    public required DocumentFileVersionKey DocumentFileVersionKey { get; init; }

    /// <summary>BLOB source key.</summary>
    public required DataFileVersionKey DataFileVersionKey { get; init; }

    /// <summary>Fencing token the worker must present on every subsequent call.</summary>
    public required ClaimToken ClaimToken { get; init; }

    /// <summary>UTC time at which the lease expires and the reaper may reclaim.</summary>
    public required DateTimeOffset LeaseExpiresAtUtc { get; init; }

    /// <summary>1-based attempt number (incremented at claim time by the SP).</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Maximum attempts before the item goes to DeadLettered.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>Convenience: does this claim have retries left after failure?</summary>
    public bool HasRetriesLeftAfterFailure => AttemptNumber < MaxAttempts;
}
