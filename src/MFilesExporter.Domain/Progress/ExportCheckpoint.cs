using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.Progress;

/// <summary>
/// A durable marker of the highest enumeration cursor observed for a
/// (job, partition) pair. On restart, the exporter reads the active
/// checkpoint and resumes enumeration strictly past this position.
///
/// Monotonic by design: a lower cursor never supersedes a higher one.
/// The <see cref="TryAdvance"/> method enforces this invariant.
/// </summary>
public sealed record ExportCheckpoint
{
    private ExportCheckpoint(
        ExportJobId jobId,
        string partitionKey,
        DocumentFileVersionKey cursor,
        long documentsProcessed,
        DateTimeOffset checkpointAtUtc)
    {
        JobId = jobId;
        PartitionKey = partitionKey;
        Cursor = cursor;
        DocumentsProcessed = documentsProcessed;
        CheckpointAtUtc = checkpointAtUtc;
    }

    /// <summary>Owning job.</summary>
    public ExportJobId JobId { get; }

    /// <summary>Partition scope. Combined with <see cref="JobId"/> is unique among Active checkpoints.</summary>
    public string PartitionKey { get; }

    /// <summary>Highest observed enumeration cursor for this job/partition.</summary>
    public DocumentFileVersionKey Cursor { get; }

    /// <summary>Total documents processed in this partition up to and including this cursor.</summary>
    public long DocumentsProcessed { get; }

    /// <summary>UTC time this checkpoint was created.</summary>
    public DateTimeOffset CheckpointAtUtc { get; }

    /// <summary>Origin sentinel — used before any real checkpoint has been saved.</summary>
    public static ExportCheckpoint Origin(ExportJobId jobId, string partitionKey) =>
        new(jobId, partitionKey, DocumentFileVersionKey.Origin, 0, DateTimeOffset.UtcNow);

    /// <summary>
    /// Attempts to advance the checkpoint to <paramref name="candidate"/>.
    /// If the candidate is not strictly greater than the current cursor,
    /// returns <c>this</c> unchanged (no advancement).
    /// </summary>
    public ExportCheckpoint TryAdvance(
        DocumentFileVersionKey candidate,
        long documentsProcessedInPartition,
        DateTimeOffset at)
    {
        if (candidate <= Cursor)
        {
            return this;
        }
        return new ExportCheckpoint(JobId, PartitionKey, candidate, documentsProcessedInPartition, at);
    }
}
