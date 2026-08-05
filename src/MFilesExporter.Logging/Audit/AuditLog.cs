using MFilesExporter.Logging.Correlation;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Logging.Audit;

/// <summary>
/// Default <see cref="IAuditLog"/>. Emits at <see cref="LogLevel.Information"/>
/// through a Serilog logger scoped to <c>Category=Audit</c>. The message
/// template is stable so a WORM-compatible sink can parse without regex.
/// </summary>
public sealed class AuditLog : IAuditLog
{
    private readonly ILogger _logger;
    private readonly ICorrelationIdAccessor _correlation;

    public AuditLog(ILoggerFactory loggerFactory, ICorrelationIdAccessor correlation)
    {
        _logger      = loggerFactory.CreateLogger("MFilesExporter.Audit");
        _correlation = correlation;
    }

    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var correlationId = auditEvent.CorrelationId ?? _correlation.Current ?? "no-scope";

        // BeginScope pushes Data.* as structured properties without collapsing to strings.
        using (_logger.BeginScope(auditEvent.Data))
        {
            _logger.LogInformation(
                "audit.event action={Action} actor={Actor} subject={Subject} outcome={Outcome} " +
                "timestamp={Timestamp:O} correlationId={CorrelationId} category={Category}",
                auditEvent.Action, auditEvent.Actor, auditEvent.Subject, auditEvent.Outcome,
                auditEvent.TimestampUtc, correlationId, LogCategories.Audit);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(
        string action,
        string actor,
        string subject,
        string outcome,
        IReadOnlyDictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default)
    {
        var evt = new AuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Action       = action,
            Actor        = actor,
            Subject      = subject,
            Outcome      = outcome,
            Data         = data ?? new Dictionary<string, object?>(0),
        };
        return WriteAsync(evt, cancellationToken);
    }
}
