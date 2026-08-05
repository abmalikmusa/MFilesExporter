namespace MFilesExporter.Logging.Performance;

/// <summary>
/// Records latency and throughput of enterprise operations in a shape that
/// dashboards can slice by <c>operation</c>, <c>outcome</c>, and <c>correlationId</c>.
/// </summary>
/// <remarks>
/// Prefer <see cref="Begin"/> for RAII-style measurement:
/// <code>
/// using var scope = _perf.Begin("sink.write");
/// scope.SetTag("path", path);
/// await sink.WriteAsync(...);
/// scope.Complete(bytesWritten: bytes);
/// </code>
/// The scope emits a single log line on <see cref="IDisposable.Dispose"/> —
/// even when the operation throws, so failure latencies are captured too.
/// </remarks>
public interface IPerformanceLogger
{
    /// <summary>Start measuring a named operation. Dispose to emit the log.</summary>
    PerformanceScope Begin(string operation);

    /// <summary>Measure an async delegate. Automatically tags with outcome and any exception.</summary>
    ValueTask<T> TimeAsync<T>(
        string operation,
        Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken);

    /// <summary>Measure an async delegate that returns no value.</summary>
    ValueTask TimeAsync(
        string operation,
        Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken);
}
