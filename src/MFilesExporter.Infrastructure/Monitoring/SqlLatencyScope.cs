using System.Diagnostics;
using MFilesExporter.Application.Abstractions.Monitoring;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// RAII helper for measuring the latency of a single SQL operation. Emit
/// via <c>using var _ = SqlLatencyScope.Start(_metrics, "sql.enumerate");</c>
/// and mark success via <see cref="Succeed"/> before disposal.
/// </summary>
/// <remarks>
/// Disposal always records — even when the caller throws — so failure
/// latencies show up in the histogram and can be sliced by <c>status</c>.
/// </remarks>
public readonly struct SqlLatencyScope : IDisposable
{
    private readonly IExporterMetrics _metrics;
    private readonly string _operation;
    private readonly long _startTicks;
    private readonly StateBox _state;

    private SqlLatencyScope(IExporterMetrics metrics, string operation)
    {
        _metrics    = metrics;
        _operation  = operation;
        _startTicks = Stopwatch.GetTimestamp();
        _state      = new StateBox();
    }

    public static SqlLatencyScope Start(IExporterMetrics metrics, string operation)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new SqlLatencyScope(metrics, operation);
    }

    /// <summary>Mark the operation as successful. Must be called before disposal to avoid a <c>failed</c> record.</summary>
    public void Succeed() => _state.Succeeded = true;

    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTicks);
        _metrics.RecordSqlLatency(_operation, elapsed, _state.Succeeded);
    }

    private sealed class StateBox
    {
        public bool Succeeded;
    }
}
