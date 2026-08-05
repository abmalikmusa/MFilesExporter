namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Pure exponential back-off with full jitter. Extracted so unit tests can
/// exercise it without spinning a Polly pipeline.
/// </summary>
internal static class BackoffCalculator
{
    /// <summary>
    /// Compute the delay for a 1-based attempt number.
    /// Formula: <c>delay = min(base · 2^(attempt-1), maxDelay)</c>, then
    /// scaled uniformly to <c>[1-jitter, 1+jitter]</c>.
    /// </summary>
    public static TimeSpan Compute(
        int attemptNumber,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double jitterFactor,
        Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (attemptNumber < 1) attemptNumber = 1;

        // Cap the exponent so we do not shift into negatives on very high attempts.
        var exponent = Math.Min(attemptNumber - 1, 30);
        var scaled   = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped   = Math.Min(scaled, maxDelay.TotalMilliseconds);

        if (jitterFactor > 0)
        {
            var min = capped * (1 - jitterFactor);
            var max = capped * (1 + jitterFactor);
            capped  = min + rng.NextDouble() * (max - min);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }
}
