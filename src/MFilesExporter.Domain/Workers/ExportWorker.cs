using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.Workers;

/// <summary>
/// A registered processing instance under a job. Real deployments frequently
/// have one worker per pod; horizontally-scaled setups have several,
/// partitioned by <c>AssignedPartition</c>.
/// </summary>
public sealed record ExportWorker
{
    private ExportWorker(
        ExportWorkerId id,
        ExportJobId jobId,
        string workerName,
        string machineName,
        int? processId,
        string assignedPartition,
        int concurrency,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? stoppedAtUtc,
        WorkerHeartbeat? lastHeartbeat,
        WorkerStatus status)
    {
        Id = id;
        JobId = jobId;
        WorkerName = workerName;
        MachineName = machineName;
        ProcessId = processId;
        AssignedPartition = assignedPartition;
        Concurrency = concurrency;
        RegisteredAtUtc = registeredAtUtc;
        StartedAtUtc = startedAtUtc;
        StoppedAtUtc = stoppedAtUtc;
        LastHeartbeat = lastHeartbeat;
        Status = status;
    }

    /// <summary>Surrogate identifier.</summary>
    public ExportWorkerId Id { get; init; }

    /// <summary>Job this worker runs under.</summary>
    public ExportJobId JobId { get; init; }

    /// <summary>Operator-visible name (usually the pod / hostname).</summary>
    public string WorkerName { get; init; }

    /// <summary>Physical or virtual host running the worker.</summary>
    public string MachineName { get; init; }

    /// <summary>OS process id when known — used to correlate with logs.</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Partition the worker is authorised to enumerate. Two workers with the
    /// same partition share a checkpoint; two workers with different
    /// partitions cannot collide on the same descriptor.
    /// </summary>
    public string AssignedPartition { get; init; }

    /// <summary>Per-stage concurrency the worker was launched with.</summary>
    public int Concurrency { get; init; }

    /// <summary>UTC time the worker row was created.</summary>
    public DateTimeOffset RegisteredAtUtc { get; init; }

    /// <summary>UTC time the worker began processing after registration.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>UTC time the worker cleanly or unexpectedly stopped.</summary>
    public DateTimeOffset? StoppedAtUtc { get; init; }

    /// <summary>Most recent heartbeat, or <c>null</c> if never observed.</summary>
    public WorkerHeartbeat? LastHeartbeat { get; init; }

    /// <summary>Current lifecycle state derived from the most recent heartbeat.</summary>
    public WorkerStatus Status { get; init; }

    /// <summary>Convenience: seconds since the last heartbeat.</summary>
    public TimeSpan? HeartbeatAge(DateTimeOffset now) =>
        LastHeartbeat is null ? null : now - LastHeartbeat.ObservedAtUtc;

    /* ---------------- Factory + transitions ---------------- */

    public static ExportWorker Register(
        ExportJobId jobId,
        string workerName,
        string machineName,
        int? processId,
        string assignedPartition,
        int concurrency,
        DateTimeOffset registeredAtUtc)
    {
        if (!jobId.IsAssigned) throw new ArgumentException("Job must be persisted.", nameof(jobId));
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignedPartition);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        return new ExportWorker(
            id: ExportWorkerId.Unassigned,
            jobId: jobId,
            workerName: workerName,
            machineName: machineName,
            processId: processId,
            assignedPartition: assignedPartition,
            concurrency: concurrency,
            registeredAtUtc: registeredAtUtc,
            startedAtUtc: null,
            stoppedAtUtc: null,
            lastHeartbeat: null,
            status: WorkerStatus.Registered);
    }

    public ExportWorker WithAssignedId(ExportWorkerId id) => this with { Id = id };

    public ExportWorker MarkActive(DateTimeOffset at) => this with
    {
        Status = WorkerStatus.Active,
        StartedAtUtc = StartedAtUtc ?? at,
    };

    public ExportWorker RecordHeartbeat(WorkerHeartbeat heartbeat) => this with
    {
        LastHeartbeat = heartbeat,
        Status = heartbeat.ReportedStatus,
    };

    public ExportWorker MarkStalled() => this with { Status = WorkerStatus.Stalled };

    public ExportWorker MarkStopped(DateTimeOffset at) => this with
    {
        Status = WorkerStatus.Stopped,
        StoppedAtUtc = at,
    };

    public ExportWorker MarkFailed(DateTimeOffset at) => this with
    {
        Status = WorkerStatus.Failed,
        StoppedAtUtc = at,
    };
}
