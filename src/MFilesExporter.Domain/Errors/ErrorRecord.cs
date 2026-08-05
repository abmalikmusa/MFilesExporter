using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;
using MFilesExporter.Domain.Workers;

namespace MFilesExporter.Domain.Errors;

/// <summary>
/// Immutable snapshot of a single error observed during an export run.
/// Records flow from workers into the tracking DB and are the primary
/// forensic surface for operators. Every field exists to answer a specific
/// question at 2 AM: *what failed, where, when, why, and what should I do?*
/// </summary>
public sealed record ErrorRecord
{
    /// <summary>Owning job.</summary>
    public required ExportJobId JobId { get; init; }

    /// <summary>Worker that observed the error (may be <c>null</c> if raised at orchestration level).</summary>
    public ExportWorkerId? WorkerId { get; init; }

    /// <summary>Document identifiers, when the error was scoped to a specific document.</summary>
    public DocumentFileVersionKey? DocumentFileVersionKey { get; init; }

    /// <summary>BLOB identifiers, when the error was scoped to a specific BLOB.</summary>
    public DataFileVersionKey? DataFileVersionKey { get; init; }

    /// <summary>Deterministic idempotency key of the affected document, if known.</summary>
    public IdempotencyKey? IdempotencyKey { get; init; }

    /// <summary>Severity band — Warning / Error / Critical.</summary>
    public required ErrorSeverity Severity { get; init; }

    /// <summary>Category — Transient / Deterministic / Configuration / Security / Storage / Unknown.</summary>
    public required ErrorCategory Category { get; init; }

    /// <summary>Pipeline stage or component that raised the error.</summary>
    public required string Source { get; init; }

    /// <summary>Fully qualified exception type name, if the error originated from an exception.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Human-readable message.</summary>
    public required string Message { get; init; }

    /// <summary>Optional stack trace for forensic drill-down.</summary>
    public string? StackTrace { get; init; }

    /// <summary>1-based attempt number that observed the error.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>UTC observation time.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Constructs an ErrorRecord from a caught .NET exception at a specific stage.</summary>
    public static ErrorRecord FromException(
        ExportJobId jobId,
        ExportWorkerId? workerId,
        Exception exception,
        string source,
        ErrorSeverity severity,
        ErrorCategory category,
        int attemptNumber,
        DateTimeOffset observedAtUtc,
        DocumentFileVersionKey? docKey = null,
        DataFileVersionKey? dataKey = null,
        IdempotencyKey? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return new ErrorRecord
        {
            JobId = jobId,
            WorkerId = workerId,
            DocumentFileVersionKey = docKey,
            DataFileVersionKey = dataKey,
            IdempotencyKey = idempotencyKey,
            Severity = severity,
            Category = category,
            Source = source,
            ExceptionType = exception.GetType().FullName,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            AttemptNumber = attemptNumber,
            OccurredAtUtc = observedAtUtc,
        };
    }
}
