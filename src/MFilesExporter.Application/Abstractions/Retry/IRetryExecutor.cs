namespace MFilesExporter.Application.Abstractions.Retry;

/// <summary>
/// High-level retry surface. Callers hand over a named operation and a delegate;
/// the executor selects the correct <see cref="Retry.RetryPolicyProfile"/> by
/// classifying the exception via <see cref="IFailureClassifier"/>, applies
/// exponential back-off with jitter, and short-circuits through the category's
/// circuit breaker when the downstream is deemed unhealthy.
/// </summary>
public interface IRetryExecutor
{
    /// <summary>Execute a value-returning operation under the profile named <paramref name="operationName"/>.</summary>
    ValueTask<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        string? correlationId = null);

    /// <summary>Execute a void operation under the profile named <paramref name="operationName"/>.</summary>
    ValueTask ExecuteAsync(
        string operationName,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken,
        string? correlationId = null);
}

/// <summary>Canonical operation names understood by the retry executor.</summary>
public static class RetryOperationNames
{
    public const string SqlRead     = "sql-read";
    public const string SqlBlobRead = "sql-blob-read";
    public const string SqlWrite    = "sql-write";
    public const string DiskWrite   = "disk-write";
    public const string DiskRead    = "disk-read";
    public const string StateStore  = "state-store";
    public const string Network     = "network";
}
