using Serilog.Context;

namespace MFilesExporter.Logging.Correlation;

/// <summary>
/// Default <see cref="ICorrelationIdAccessor"/>. Stores the current value in
/// an <see cref="AsyncLocal{T}"/> so it flows across async boundaries, and
/// pushes it into the Serilog <c>LogContext</c> so log-line enrichment is
/// automatic — no caller has to remember to add the property.
/// </summary>
public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    /// <summary>Serilog property name applied to every event inside a scope.</summary>
    public const string PropertyName = "CorrelationId";

    private static readonly AsyncLocal<string?> _current = new();

    public string? Current => _current.Value;

    public string NewId() => Guid.NewGuid().ToString("N");

    public IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var previous = _current.Value;
        _current.Value = correlationId;

        // Serilog LogContext.PushProperty appends to the enricher stack for this async flow.
        var serilogScope = LogContext.PushProperty(PropertyName, correlationId);
        return new PopScope(previous, serilogScope);
    }

    public IDisposable PushNew(out string correlationId)
    {
        correlationId = NewId();
        return Push(correlationId);
    }

    private sealed class PopScope : IDisposable
    {
        private readonly string? _previous;
        private readonly IDisposable _serilogScope;
        private int _disposed;

        public PopScope(string? previous, IDisposable serilogScope)
        {
            _previous     = previous;
            _serilogScope = serilogScope;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _current.Value = _previous;
            _serilogScope.Dispose();
        }
    }
}
