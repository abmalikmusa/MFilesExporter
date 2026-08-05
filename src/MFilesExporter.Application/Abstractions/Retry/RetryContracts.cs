namespace MFilesExporter.Application.Abstractions.Retry;

/// <summary>
/// Immutable record describing a single retry attempt as observed by the executor.
/// Emitted to <see cref="IRetryObserver"/> and to structured logs.
/// </summary>
public sealed record RetryAttemptContext
{
    /// <summary>Logical operation name, e.g. <c>sql-read</c>, <c>disk-write</c>.</summary>
    public required string OperationName { get; init; }

    /// <summary>Attempt number, 1-based (1 = first retry after the initial failure).</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Total attempts allowed for this category (initial call + retries).</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>Category the classifier assigned to the failure.</summary>
    public required FailureCategory Category { get; init; }

    /// <summary>Delay the executor is about to sleep before the next attempt.</summary>
    public required TimeSpan Delay { get; init; }

    /// <summary>The exception that caused the retry.</summary>
    public required Exception Exception { get; init; }

    /// <summary>Correlation id if the caller supplied one — otherwise <c>null</c>.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Terminal outcome of an <see cref="IRetryExecutor.ExecuteAsync{T}"/> call.
/// Success cases are surfaced via the returned value; failure cases raise the exception —
/// this record is populated for the observer regardless.
/// </summary>
public sealed record RetryOutcome
{
    public required string OperationName { get; init; }
    public required bool Succeeded { get; init; }
    public required int TotalAttempts { get; init; }
    public required TimeSpan TotalElapsed { get; init; }
    public FailureCategory FinalCategory { get; init; }
    public Exception? FinalException { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Optional sink for retry telemetry. Register any number of implementations —
/// they are called sequentially and MUST NOT throw. Faulty observers are logged and skipped.
/// </summary>
public interface IRetryObserver
{
    /// <summary>Invoked before every retry sleep. Do not block.</summary>
    ValueTask OnRetryAsync(RetryAttemptContext attempt, CancellationToken cancellationToken);

    /// <summary>Invoked exactly once per <see cref="IRetryExecutor.ExecuteAsync{T}"/> call — on success or terminal failure.</summary>
    ValueTask OnOutcomeAsync(RetryOutcome outcome, CancellationToken cancellationToken);
}
