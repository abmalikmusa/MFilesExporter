using Serilog.Core;
using Serilog.Events;

namespace MFilesExporter.Logging.Correlation;

/// <summary>
/// Belt-and-braces enricher that stamps <c>CorrelationId</c> on every event
/// even if the caller forgot to <see cref="ICorrelationIdAccessor.Push"/>.
/// </summary>
/// <remarks>
/// <see cref="CorrelationIdAccessor.Push"/> already writes the property via
/// <c>LogContext.PushProperty</c>, so in normal use this enricher is a no-op —
/// it only fires when a log call escapes the scope. In that case it stamps a
/// fresh ambient id so the event isn't dropped from correlation queries.
/// </remarks>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    private readonly ICorrelationIdAccessor _accessor;

    public CorrelationIdEnricher(ICorrelationIdAccessor accessor) => _accessor = accessor;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.ContainsKey(CorrelationIdAccessor.PropertyName)) return;

        var value = _accessor.Current ?? "no-scope";
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(CorrelationIdAccessor.PropertyName, value));
    }
}
