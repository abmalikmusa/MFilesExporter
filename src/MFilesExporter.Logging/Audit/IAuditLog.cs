namespace MFilesExporter.Logging.Audit;

/// <summary>
/// Append-only audit-log sink. Callers should treat writes as fire-and-forget
/// from a business-logic perspective — the implementation guarantees the event
/// is enqueued to Serilog's Async sink before returning.
/// </summary>
public interface IAuditLog
{
    /// <summary>Record a single audit event.</summary>
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>Convenience overload — builds the record inline.</summary>
    ValueTask WriteAsync(
        string action,
        string actor,
        string subject,
        string outcome,
        IReadOnlyDictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default);
}
