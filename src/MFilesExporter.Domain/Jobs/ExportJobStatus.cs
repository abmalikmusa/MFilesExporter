namespace MFilesExporter.Domain.Jobs;

/// <summary>
/// Lifecycle state of an <see cref="ExportJob"/>. Values are stable — new
/// states must be added at the end of the enumeration; existing values must
/// never be renumbered because they are persisted in the tracking DB.
/// </summary>
public enum ExportJobStatus
{
    /// <summary>Created but not yet started (no worker has run).</summary>
    Pending = 0,

    /// <summary>At least one worker is actively processing.</summary>
    Running = 1,

    /// <summary>Explicitly paused by an operator; workers stop after their current batch.</summary>
    Paused = 2,

    /// <summary>All expected documents processed successfully or accounted for.</summary>
    Completed = 3,

    /// <summary>Terminated by an unrecoverable failure.</summary>
    Failed = 4,

    /// <summary>Cancelled by an operator.</summary>
    Cancelled = 5,

    /// <summary>Moved to the archive filegroup — no longer actionable.</summary>
    Archived = 6,
}

/// <summary>Helper for computing legal state transitions.</summary>
public static class ExportJobStatusTransitions
{
    /// <summary>
    /// Returns true when the given transition is permitted. Terminal states
    /// (Completed, Failed, Cancelled, Archived) are absorbing.
    /// </summary>
    public static bool IsAllowed(ExportJobStatus from, ExportJobStatus to) => (from, to) switch
    {
        (ExportJobStatus.Pending, ExportJobStatus.Running) => true,
        (ExportJobStatus.Pending, ExportJobStatus.Cancelled) => true,
        (ExportJobStatus.Running, ExportJobStatus.Paused) => true,
        (ExportJobStatus.Running, ExportJobStatus.Completed) => true,
        (ExportJobStatus.Running, ExportJobStatus.Failed) => true,
        (ExportJobStatus.Running, ExportJobStatus.Cancelled) => true,
        (ExportJobStatus.Paused, ExportJobStatus.Running) => true,
        (ExportJobStatus.Paused, ExportJobStatus.Cancelled) => true,
        (ExportJobStatus.Completed, ExportJobStatus.Archived) => true,
        (ExportJobStatus.Failed, ExportJobStatus.Archived) => true,
        (ExportJobStatus.Cancelled, ExportJobStatus.Archived) => true,
        _ => false,
    };
}
