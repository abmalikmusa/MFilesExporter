using System.Diagnostics.Metrics;
using MFilesExporter.Application.Abstractions.Retry;

namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Emits OpenTelemetry counters for the retry engine:
/// <list type="bullet">
///   <item><description><c>exporter.retry.attempts</c> — every retry sleep, tagged by <c>operation</c> and <c>category</c>.</description></item>
///   <item><description><c>exporter.retry.outcomes</c> — one per terminal outcome, tagged by <c>operation</c> and <c>status</c>.</description></item>
///   <item><description><c>exporter.retry.elapsed_ms</c> — histogram of total elapsed time per outcome.</description></item>
/// </list>
/// </summary>
public sealed class MetricsRetryObserver : IRetryObserver, IDisposable
{
    public const string MeterName = "MFilesExporter.Retry";

    private readonly Meter _meter;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _elapsed;

    public MetricsRetryObserver()
    {
        _meter    = new Meter(MeterName, "1.0.0");
        _attempts = _meter.CreateCounter<long>("exporter.retry.attempts",
            description: "Retry attempts issued by the retry executor.");
        _outcomes = _meter.CreateCounter<long>("exporter.retry.outcomes",
            description: "Terminal outcomes reported by the retry executor.");
        _elapsed  = _meter.CreateHistogram<double>("exporter.retry.elapsed_ms",
            unit: "ms",
            description: "Total elapsed time per retry executor invocation.");
    }

    public ValueTask OnRetryAsync(RetryAttemptContext attempt, CancellationToken cancellationToken)
    {
        _attempts.Add(1,
            new KeyValuePair<string, object?>("operation", attempt.OperationName),
            new KeyValuePair<string, object?>("category",  attempt.Category.ToString()));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutcomeAsync(RetryOutcome outcome, CancellationToken cancellationToken)
    {
        _outcomes.Add(1,
            new KeyValuePair<string, object?>("operation", outcome.OperationName),
            new KeyValuePair<string, object?>("status",    outcome.Succeeded ? "succeeded" : "failed"),
            new KeyValuePair<string, object?>("category",  outcome.FinalCategory.ToString()));

        _elapsed.Record(outcome.TotalElapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", outcome.OperationName),
            new KeyValuePair<string, object?>("status",    outcome.Succeeded ? "succeeded" : "failed"));

        return ValueTask.CompletedTask;
    }

    public void Dispose() => _meter.Dispose();
}
