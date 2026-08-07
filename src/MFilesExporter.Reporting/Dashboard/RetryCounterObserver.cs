using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Application.Abstractions.Retry;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// <see cref="IRetryObserver"/> + <see cref="IRetryCounterSource"/> composite.
/// Registered against both interfaces so the retry executor calls it on
/// every retry and the dashboard reads the running total on every tick.
/// </summary>
public sealed class RetryCounterObserver : IRetryObserver, IRetryCounterSource
{
    private long _totalRetries;

    public long TotalRetries => Volatile.Read(ref _totalRetries);

    public ValueTask OnRetryAsync(RetryAttemptContext attempt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _totalRetries);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutcomeAsync(RetryOutcome outcome, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
