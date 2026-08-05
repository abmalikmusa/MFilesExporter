namespace MFilesExporter.Application.Models.Tracking;

/// <summary>
/// Strongly-typed records mirroring the MFilesExportTracking schema.
/// Records are immutable and safe to share across pipeline stages.
/// </summary>

public sealed record ExportJobRecord(
    long ExportJobId,
    string JobName,
    string SourceServer,
    string SourceDatabase,
    string PartitionKey,
    long? TotalDocumentsExpected,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? CancellationReason,
    ExportJobStatus Status,
    DateTime CreatedDate,
    string CreatedBy,
    DateTime ModifiedDate,
    string ModifiedBy);

public sealed record ExportWorkerRecord(
    long ExportWorkerId,
    long ExportJobId,
    string WorkerName,
    string MachineName,
    int? ProcessId,
    string AssignedPartition,
    int Concurrency,
    DateTime? StartedAtUtc,
    DateTime? LastHeartbeatUtc,
    DateTime? StoppedAtUtc,
    ExportWorkerStatus Status,
    DateTime CreatedDate,
    string CreatedBy);

public sealed record ExportProgressRecord
{
    public long ExportProgressId { get; init; }
    public long ExportJobId { get; init; }
    public long? ExportWorkerId { get; init; }
    public DateTime SnapshotAtUtc { get; init; }
    public long TotalRecorded { get; init; }
    public long TotalSucceeded { get; init; }
    public long TotalFailed { get; init; }
    public long TotalSkipped { get; init; }
    public long TotalBytesWritten { get; init; }
    public decimal? DocumentsPerSecond { get; init; }
    public decimal? MebibytesPerSecond { get; init; }
    public long? LastDocumentFilePartId { get; init; }
    public long? LastVersionPartId { get; init; }
}

public sealed record ExportMetricRecord(
    long ExportJobId,
    long? ExportWorkerId,
    string MetricName,
    double MetricValue,
    string MetricUnit,
    string? TagsJson,
    DateTime CapturedAtUtc);

public sealed record ExportErrorRecord
{
    public long ExportJobId { get; init; }
    public long? ExportWorkerId { get; init; }
    public long? DocumentFilePartId { get; init; }
    public long? VersionPartId { get; init; }
    public long? DataFileVersionId { get; init; }
    public string? IdempotencyKeyHex { get; init; }
    public ExportErrorSeverity Severity { get; init; } = ExportErrorSeverity.Error;
    public ExportErrorCategory Category { get; init; } = ExportErrorCategory.Unknown;
    public string ErrorSource { get; init; } = string.Empty;
    public string? ExceptionType { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record ExportCheckpointRecord(
    long ExportCheckpointId,
    long ExportJobId,
    string PartitionKey,
    long LastDocumentFilePartId,
    long LastVersionPartId,
    long? DocumentsProcessedInPartition,
    DateTime CheckpointAtUtc,
    ExportCheckpointStatus Status);

public sealed record ExportAuditRecord(
    long ExportAuditId,
    long? ExportJobId,
    string EntityType,
    long EntityId,
    string AuditAction,
    string? PreviousStatus,
    string? NewStatus,
    string? ActionDetailsJson,
    string ActorName,
    ExportAuditActor ActorType,
    DateTime OccurredAtUtc);
