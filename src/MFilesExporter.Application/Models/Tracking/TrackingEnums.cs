namespace MFilesExporter.Application.Models.Tracking;

/// <summary>
/// Enum types mirroring the CHECK-constrained Status columns in the
/// MFilesExportTracking database. Kept in the Application layer so the
/// pipeline can compare against them without a Persistence dependency.
/// </summary>

public enum ExportJobStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Archived,
}

public enum ExportWorkerStatus
{
    Registered,
    Active,
    Idle,
    Stalled,
    Stopped,
    Failed,
    Archived,
}

public enum ExportErrorSeverity
{
    Warning,
    Error,
    Critical,
}

public enum ExportErrorCategory
{
    Transient,
    Deterministic,
    Configuration,
    Security,
    Storage,
    Unknown,
}

public enum ExportErrorStatus
{
    New,
    Acknowledged,
    Investigating,
    Resolved,
    Ignored,
    Archived,
}

public enum ExportCheckpointStatus
{
    Active,
    Superseded,
    RolledBack,
    Archived,
}

public enum ExportAuditActor
{
    System,
    Worker,
    Scheduler,
    User,
    Service,
}
