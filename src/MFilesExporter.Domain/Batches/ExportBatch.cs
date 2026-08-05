using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.Batches;

/// <summary>
/// A logical grouping of document descriptors that flow through the pipeline
/// as one unit. Batches are the granularity at which the enumerator pages the
/// source and at which the outcome collector flushes to the tracking DB.
/// </summary>
/// <remarks>
/// A batch is bounded by the two cursors <see cref="FromExclusive"/> and
/// <see cref="ToInclusive"/>. Given the batch's job and cursor range, the
/// batch's descriptor set is fully determined — batches are therefore
/// reproducible: a re-run of the same job with the same cursors sees the
/// same descriptor set (assuming committed source rows are stable).
/// </remarks>
public sealed record ExportBatch
{
    private ExportBatch(
        ExportBatchId id,
        ExportJobId jobId,
        DocumentFileVersionKey fromExclusive,
        DocumentFileVersionKey toInclusive,
        int expectedCount,
        int processedCount,
        int successCount,
        int failureCount,
        int skipCount,
        BatchStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        Id = id;
        JobId = jobId;
        FromExclusive = fromExclusive;
        ToInclusive = toInclusive;
        ExpectedCount = expectedCount;
        ProcessedCount = processedCount;
        SuccessCount = successCount;
        FailureCount = failureCount;
        SkipCount = skipCount;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>Surrogate identifier assigned by the tracking DB.</summary>
    public ExportBatchId Id { get; init; }

    /// <summary>Owning job.</summary>
    public ExportJobId JobId { get; init; }

    /// <summary>Exclusive lower bound of the keyset scan that produced this batch.</summary>
    public DocumentFileVersionKey FromExclusive { get; init; }

    /// <summary>Inclusive upper bound — the last descriptor's key.</summary>
    public DocumentFileVersionKey ToInclusive { get; init; }

    /// <summary>Number of descriptors the enumeration produced for this range.</summary>
    public int ExpectedCount { get; init; }

    /// <summary>Number of descriptors that have reached a terminal outcome.</summary>
    public int ProcessedCount { get; init; }

    /// <summary>Terminal Succeeded outcomes.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Terminal Failed outcomes.</summary>
    public int FailureCount { get; init; }

    /// <summary>Terminal Skipped outcomes.</summary>
    public int SkipCount { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public BatchStatus Status { get; init; }

    /// <summary>UTC time the batch row was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>UTC time the batch started processing.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>UTC time the batch terminated.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>True when every expected descriptor has a terminal outcome.</summary>
    public bool IsFullyAccountedFor => ProcessedCount == ExpectedCount;

    /// <summary>Creates a batch in <see cref="BatchStatus.Created"/>.</summary>
    public static ExportBatch Create(
        ExportJobId jobId,
        DocumentFileVersionKey fromExclusive,
        DocumentFileVersionKey toInclusive,
        int expectedCount,
        DateTimeOffset createdAtUtc)
    {
        if (!jobId.IsAssigned) throw new ArgumentException("Job must be persisted.", nameof(jobId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        if (toInclusive < fromExclusive)
        {
            throw new ArgumentException("ToInclusive must be >= FromExclusive.");
        }

        return new ExportBatch(
            id: ExportBatchId.Unassigned,
            jobId: jobId,
            fromExclusive: fromExclusive,
            toInclusive: toInclusive,
            expectedCount: expectedCount,
            processedCount: 0, successCount: 0, failureCount: 0, skipCount: 0,
            status: BatchStatus.Created,
            createdAtUtc: createdAtUtc,
            startedAtUtc: null,
            completedAtUtc: null);
    }

    public ExportBatch WithAssignedId(ExportBatchId id) => this with { Id = id };

    public ExportBatch MarkEnumerated() => this with { Status = BatchStatus.Enumerated };

    public ExportBatch MarkProcessing(DateTimeOffset at) =>
        this with { Status = BatchStatus.Processing, StartedAtUtc = at };

    /// <summary>
    /// Records a terminal outcome. Returns a new instance with counters
    /// advanced by one and the status potentially promoted to Completed.
    /// </summary>
    public ExportBatch AccrueOutcome(bool succeeded, bool failed, bool skipped)
    {
        var next = this with
        {
            ProcessedCount = ProcessedCount + 1,
            SuccessCount = SuccessCount + (succeeded ? 1 : 0),
            FailureCount = FailureCount + (failed ? 1 : 0),
            SkipCount = SkipCount + (skipped ? 1 : 0),
        };
        if (next.IsFullyAccountedFor && next.Status != BatchStatus.Completed)
        {
            next = next with { Status = BatchStatus.Completed, CompletedAtUtc = DateTimeOffset.UtcNow };
        }
        return next;
    }
}
