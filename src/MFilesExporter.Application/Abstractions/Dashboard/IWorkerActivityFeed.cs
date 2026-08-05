namespace MFilesExporter.Application.Abstractions.Dashboard;

/// <summary>
/// Thread-safe feed of per-worker current activity. Pipeline stages push
/// updates as documents flow through them; the dashboard reads a snapshot
/// on every render tick.
/// </summary>
/// <remarks>
/// Kept behind an interface so the pipeline projects (which cannot reference
/// Spectre.Console) do not have to know how the dashboard renders. The
/// implementation is a small in-memory dictionary keyed by worker id.
/// </remarks>
public interface IWorkerActivityFeed
{
    /// <summary>Records that <paramref name="workerId"/> started processing the given document.</summary>
    void RecordStart(int workerId, string documentKey, long bytesExpected = 0, string? batchId = null);

    /// <summary>Records that <paramref name="workerId"/> finished the current document with the given outcome.</summary>
    void RecordFinish(int workerId, WorkerActivityOutcome outcome, long bytesWritten);

    /// <summary>Marks the worker as idle — waiting for a work item.</summary>
    void RecordIdle(int workerId);

    /// <summary>Returns a point-in-time snapshot of every known worker's state.</summary>
    IReadOnlyList<WorkerActivityEntry> Snapshot();
}

/// <summary>Outcome recorded by <see cref="IWorkerActivityFeed.RecordFinish"/>.</summary>
public enum WorkerActivityOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>Immutable snapshot of a single worker's most recent activity.</summary>
public sealed record WorkerActivityEntry
{
    public required int WorkerId { get; init; }
    public required WorkerActivityState State { get; init; }
    public string? CurrentDocumentKey { get; init; }
    public string? CurrentBatchId { get; init; }
    public long BytesExpected { get; init; }
    public long BytesWritten { get; init; }
    public required DateTimeOffset LastUpdateUtc { get; init; }
    public long DocumentsProcessed { get; init; }
    public long DocumentsFailed { get; init; }
    public WorkerActivityOutcome? LastOutcome { get; init; }
}

/// <summary>High-level worker state used for the dashboard's status column.</summary>
public enum WorkerActivityState
{
    Idle,
    Busy,
    Finished,
}
