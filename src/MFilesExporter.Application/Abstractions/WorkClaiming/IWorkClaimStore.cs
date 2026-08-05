using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.Abstractions.WorkClaiming;

/// <summary>
/// Port over the distributed work-claiming engine. The implementation lives
/// in the Persistence layer (SQL Server + stored procedures); this
/// interface is what the pipeline stages consume.
/// </summary>
/// <remarks>
/// Every method here is designed to be race-safe under concurrent workers
/// and to preserve the "at-most-once completion" invariant. Callers do NOT
/// need to acquire any local locks — the SQL Server side is the sole
/// authority.
/// </remarks>
public interface IWorkClaimStore
{
    /// <summary>
    /// Insert work items for a job. Duplicate (jobId, idempotencyKey) pairs
    /// are silently ignored — safe to call on a re-enumeration.
    /// </summary>
    /// <returns>Number of new rows actually inserted.</returns>
    Task<int> EnqueueAsync(
        long exportJobId,
        IReadOnlyCollection<WorkItemEnqueueRequest> requests,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claim up to <paramref name="batchSize"/> available items
    /// on behalf of <paramref name="workerId"/>, stamping a fresh
    /// <see cref="ClaimToken"/> on each and setting a lease of
    /// <paramref name="leaseDuration"/>.
    /// </summary>
    Task<IReadOnlyList<ClaimedWorkItem>> ClaimAsync(
        long exportJobId,
        long workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extend the lease on a currently-claimed item. Returns the new
    /// expiry, or <c>null</c> if the token no longer matches (the worker
    /// must abandon and stop writing).
    /// </summary>
    Task<DateTimeOffset?> RenewAsync(
        WorkItemId workItemId,
        ClaimToken token,
        TimeSpan extension,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mark a claim as Completed. Returns <c>false</c> when the token no
    /// longer matches (lease expired and the row was reclaimed) — the
    /// caller MUST treat its work as unofficial and not update aggregate
    /// counters twice.
    /// </summary>
    Task<bool> CompleteAsync(
        WorkItemId workItemId,
        ClaimToken token,
        string outputPath,
        string checksum,
        long bytesWritten,
        CancellationToken cancellationToken);

    /// <summary>Fail a claim with either transient (retryable) or permanent semantics.</summary>
    Task<bool> FailAsync(
        WorkItemId workItemId,
        ClaimToken token,
        string reason,
        bool isPermanent,
        TimeSpan backoff,
        CancellationToken cancellationToken);

    /// <summary>
    /// Return all currently expired-lease rows to Available.
    /// Intended to be invoked by a background sweep (or SQL Agent job).
    /// </summary>
    /// <returns>Number of rows reclaimed.</returns>
    Task<int> ReclaimExpiredAsync(
        TimeSpan retryBackoff,
        int maxRows,
        CancellationToken cancellationToken);
}
