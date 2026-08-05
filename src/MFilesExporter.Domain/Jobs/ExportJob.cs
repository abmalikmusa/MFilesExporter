using MFilesExporter.Domain.Common;

namespace MFilesExporter.Domain.Jobs;

/// <summary>
/// Aggregate root for the export process. One <see cref="ExportJob"/> models
/// exactly one run — a bounded activity that starts, does work, and ends in a
/// terminal state. Multiple concurrent jobs exist only across distinct
/// <c>PartitionKey</c>s.
/// </summary>
/// <remarks>
/// Immutable by design: state transitions produce a new instance (see
/// <see cref="MarkStarted"/>, <see cref="MarkCompleted"/>, etc.). This makes
/// unit tests trivial (no shared mutable state) and lets us reason about a
/// job as a chain of snapshots persisted through the tracking-DB audit
/// trail.
/// </remarks>
public sealed record ExportJob
{
    private ExportJob(
        ExportJobId id,
        string jobName,
        string sourceServer,
        string sourceDatabase,
        ExportConfiguration configuration,
        long? totalDocumentsExpected,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        ExportJobStatus status,
        string? cancellationReason,
        DateTimeOffset createdAtUtc,
        string createdBy)
    {
        Id = id;
        JobName = jobName;
        SourceServer = sourceServer;
        SourceDatabase = sourceDatabase;
        Configuration = configuration;
        TotalDocumentsExpected = totalDocumentsExpected;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Status = status;
        CancellationReason = cancellationReason;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
    }

    /// <summary>Surrogate identifier assigned by the tracking DB.</summary>
    public ExportJobId Id { get; init; }

    /// <summary>Operator-supplied name — need not be unique alone; unique per <c>PartitionKey</c>.</summary>
    public string JobName { get; init; }

    /// <summary>Source SQL Server host — captured for audit.</summary>
    public string SourceServer { get; init; }

    /// <summary>Source database — captured for audit.</summary>
    public string SourceDatabase { get; init; }

    /// <summary>Snapshot of the configuration this job runs under. Immutable.</summary>
    public ExportConfiguration Configuration { get; init; }

    /// <summary>Best-effort count of documents to export, obtained pre-flight. Nullable.</summary>
    public long? TotalDocumentsExpected { get; init; }

    /// <summary>UTC time the job entered <see cref="ExportJobStatus.Running"/>.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>UTC time the job reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public ExportJobStatus Status { get; init; }

    /// <summary>Free-text reason set when the job is Cancelled or Failed.</summary>
    public string? CancellationReason { get; init; }

    /// <summary>UTC time the job row was created (before Running).</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Principal that created the job.</summary>
    public string CreatedBy { get; init; }

    /// <summary>Convenience: elapsed time from Started to (Completed or now).</summary>
    public TimeSpan? Elapsed =>
        StartedAtUtc is null
            ? null
            : (CompletedAtUtc ?? DateTimeOffset.UtcNow) - StartedAtUtc.Value;

    /// <summary>True when the current status is terminal (cannot progress further).</summary>
    public bool IsTerminal =>
        Status is ExportJobStatus.Completed
              or ExportJobStatus.Failed
              or ExportJobStatus.Cancelled
              or ExportJobStatus.Archived;

    /* ---------------- Factories ---------------- */

    /// <summary>Creates a job in <see cref="ExportJobStatus.Pending"/>.</summary>
    public static ExportJob Create(
        string jobName,
        string sourceServer,
        string sourceDatabase,
        ExportConfiguration configuration,
        long? totalDocumentsExpected,
        DateTimeOffset createdAtUtc,
        string createdBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabase);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate().ThrowIfInvalid();

        return new ExportJob(
            id: ExportJobId.Unassigned,
            jobName: jobName,
            sourceServer: sourceServer,
            sourceDatabase: sourceDatabase,
            configuration: configuration,
            totalDocumentsExpected: totalDocumentsExpected,
            startedAtUtc: null,
            completedAtUtc: null,
            status: ExportJobStatus.Pending,
            cancellationReason: null,
            createdAtUtc: createdAtUtc,
            createdBy: createdBy);
    }

    /* ---------------- Transitions (produce a new instance) ---------------- */

    /// <summary>Assigns the DB-generated identity after persistence.</summary>
    public ExportJob WithAssignedId(ExportJobId id)
    {
        if (!id.IsAssigned) throw new ArgumentException("Id must be assigned.", nameof(id));
        return this with { Id = id };
    }

    public ExportJob MarkStarted(DateTimeOffset at) =>
        Transition(ExportJobStatus.Running) with { StartedAtUtc = at };

    public ExportJob MarkPaused(DateTimeOffset at) =>
        Transition(ExportJobStatus.Paused);

    public ExportJob MarkCompleted(DateTimeOffset at) =>
        Transition(ExportJobStatus.Completed) with { CompletedAtUtc = at };

    public ExportJob MarkFailed(DateTimeOffset at, string reason) =>
        Transition(ExportJobStatus.Failed) with
        {
            CompletedAtUtc = at,
            CancellationReason = reason,
        };

    public ExportJob MarkCancelled(DateTimeOffset at, string reason) =>
        Transition(ExportJobStatus.Cancelled) with
        {
            CompletedAtUtc = at,
            CancellationReason = reason,
        };

    private ExportJob Transition(ExportJobStatus to)
    {
        if (!ExportJobStatusTransitions.IsAllowed(Status, to))
        {
            throw new InvalidOperationException(
                $"Illegal ExportJob status transition: {Status} -> {to}.");
        }
        return this with { Status = to };
    }
}
