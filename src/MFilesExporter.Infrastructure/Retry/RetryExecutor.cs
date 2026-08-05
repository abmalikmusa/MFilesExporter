using System.Collections.Concurrent;
using System.Diagnostics;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Enterprise <see cref="IRetryExecutor"/>. Selects a per-operation
/// <see cref="RetryPolicyProfile"/>, classifies each failure via
/// <see cref="IFailureClassifier"/>, applies category-specific overrides,
/// and short-circuits through a per-operation circuit breaker.
/// </summary>
/// <remarks>
/// <para>
/// The executor deliberately does NOT use Polly's built-in retry strategy —
/// we need per-attempt classification (a deadlock caps at 8 tries even inside
/// a profile that allows only 5) and per-attempt timeout that itself becomes
/// a retryable failure. Building the loop by hand keeps that logic explicit
/// and testable.
/// </para>
/// <para>
/// A single per-operation circuit breaker is layered on top via
/// <see cref="OperationCircuitBreaker"/>. When the breaker is open the
/// executor throws <see cref="CircuitOpenException"/> without invoking
/// the delegate.
/// </para>
/// </remarks>
public sealed class RetryExecutor : IRetryExecutor
{
    private readonly RetryHandlingOptions _options;
    private readonly IFailureClassifier _classifier;
    private readonly IReadOnlyList<IRetryObserver> _observers;
    private readonly ILogger<RetryExecutor> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, OperationCircuitBreaker> _breakers = new(StringComparer.Ordinal);

    public RetryExecutor(
        RetryHandlingOptions options,
        IFailureClassifier classifier,
        IEnumerable<IRetryObserver> observers,
        ILogger<RetryExecutor> logger,
        TimeProvider? time = null)
    {
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _observers  = (observers ?? []).ToArray();
        _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
        _time       = time ?? TimeProvider.System;
    }

    public async ValueTask<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        var profile = ResolveProfile(operationName);

        if (!_options.Enabled)
        {
            using var singleShot = LinkedTimeoutCts(cancellationToken, profile.PerAttemptTimeoutSeconds);
            return await operation(singleShot.Token).ConfigureAwait(false);
        }

        var breaker = _breakers.GetOrAdd(operationName, name => new OperationCircuitBreaker(name, profile.CircuitBreaker, _time, _logger));
        var rng     = _rngLocal.Value!;
        var started = Stopwatch.GetTimestamp();

        Exception? lastException = null;
        FailureCategory lastCategory = FailureCategory.Unknown;
        var attempt = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            breaker.EnsureClosed();

            using var attemptCts = LinkedTimeoutCts(cancellationToken, profile.PerAttemptTimeoutSeconds);
            try
            {
                var result = await operation(attemptCts.Token).ConfigureAwait(false);
                breaker.OnSuccess();
                await NotifyOutcomeAsync(operationName, correlationId, attempt, started, true, FailureCategory.Unknown, null, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await NotifyOutcomeAsync(operationName, correlationId, attempt, started, false, FailureCategory.Cancelled, null, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"Attempt {attempt} of {operationName} exceeded {profile.PerAttemptTimeoutSeconds}s timeout.");
                lastCategory  = FailureCategory.SqlTimeout;
            }
            catch (Exception ex)
            {
                lastException = ex;
                lastCategory  = _classifier.Classify(ex);
            }

            var applied  = ApplyCategoryOverride(profile, lastCategory);
            var breakerOn = applied.CircuitBreakerEnabled;
            var maxAttempts = applied.MaxAttempts;

            if (!lastCategory.IsRetryable() || attempt >= maxAttempts)
            {
                if (breakerOn) breaker.OnFailure();
                await NotifyOutcomeAsync(operationName, correlationId, attempt, started, false, lastCategory, lastException, cancellationToken).ConfigureAwait(false);
                throw lastException!;
            }

            if (breakerOn) breaker.OnFailure();

            var delay = BackoffCalculator.Compute(
                attempt,
                TimeSpan.FromMilliseconds(applied.BaseDelayMilliseconds),
                TimeSpan.FromSeconds(applied.MaxDelaySeconds),
                profile.JitterFactor,
                rng);

            var attemptCtx = new RetryAttemptContext
            {
                OperationName = operationName,
                AttemptNumber = attempt,
                MaxAttempts   = maxAttempts,
                Category      = lastCategory,
                Delay         = delay,
                Exception     = lastException!,
                CorrelationId = correlationId,
            };

            _logger.LogWarning(lastException,
                "[retry] {Operation} attempt {Attempt}/{Max} failed with {Category}; sleeping {Delay}",
                operationName, attempt, maxAttempts, lastCategory, delay);

            await NotifyRetryAsync(attemptCtx, cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await NotifyOutcomeAsync(operationName, correlationId, attempt, started, false, FailureCategory.Cancelled, lastException, cancellationToken).ConfigureAwait(false);
                throw;
            }

            attempt++;
        }
    }

    public async ValueTask ExecuteAsync(
        string operationName,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        await ExecuteAsync<byte>(operationName, async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken, correlationId).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Profile lookup and category overrides
    // ---------------------------------------------------------------------

    private RetryPolicyProfile ResolveProfile(string operationName) => operationName switch
    {
        RetryOperationNames.SqlRead     => _options.SqlRead,
        RetryOperationNames.SqlBlobRead => _options.SqlBlobRead,
        RetryOperationNames.SqlWrite    => _options.SqlWrite,
        RetryOperationNames.DiskWrite   => _options.DiskWrite,
        RetryOperationNames.DiskRead    => _options.DiskRead,
        RetryOperationNames.StateStore  => _options.StateStore,
        RetryOperationNames.Network     => _options.Network,
        _                                => _options.Default,
    };

    private AppliedProfile ApplyCategoryOverride(RetryPolicyProfile profile, FailureCategory category)
    {
        CategoryOverride? o = category switch
        {
            FailureCategory.SqlDeadlock => _options.Categories.SqlDeadlock,
            FailureCategory.DiskFull    => _options.Categories.DiskFull,
            FailureCategory.RateLimited => _options.Categories.RateLimited,
            _ => null,
        };

        var maxAttempts     = profile.MaxAttempts;
        var baseDelayMs     = profile.BaseDelayMilliseconds;
        var maxDelaySeconds = profile.MaxDelaySeconds;
        var breakerOn       = profile.CircuitBreaker.Enabled;

        if (o is not null)
        {
            if (o.MaxAttemptsCap is int cap) maxAttempts = Math.Min(maxAttempts, cap);
            if (o.BaseDelayMilliseconds is int bd) baseDelayMs = bd;
            if (o.MaxDelaySeconds is int md) maxDelaySeconds = md;
            if (o.DisableCircuitBreaker) breakerOn = false;
        }

        return new AppliedProfile(maxAttempts, baseDelayMs, maxDelaySeconds, breakerOn);
    }

    private readonly record struct AppliedProfile(int MaxAttempts, int BaseDelayMilliseconds, int MaxDelaySeconds, bool CircuitBreakerEnabled);

    // ---------------------------------------------------------------------
    // Per-attempt timeout
    // ---------------------------------------------------------------------

    private static CancellationTokenSource LinkedTimeoutCts(CancellationToken outer, int timeoutSeconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        if (timeoutSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return cts;
    }

    // ---------------------------------------------------------------------
    // Observer fan-out — never throws to the caller
    // ---------------------------------------------------------------------

    private async ValueTask NotifyRetryAsync(RetryAttemptContext ctx, CancellationToken ct)
    {
        foreach (var observer in _observers)
        {
            try { await observer.OnRetryAsync(ctx, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Retry observer {Observer} threw; continuing.", observer.GetType().Name);
            }
        }
    }

    private async ValueTask NotifyOutcomeAsync(
        string operationName, string? correlationId, int attempts, long started,
        bool succeeded, FailureCategory category, Exception? exception, CancellationToken ct)
    {
        if (_observers.Count == 0) return;

        var elapsed = Stopwatch.GetElapsedTime(started);
        var outcome = new RetryOutcome
        {
            OperationName  = operationName,
            Succeeded      = succeeded,
            TotalAttempts  = attempts,
            TotalElapsed   = elapsed,
            FinalCategory  = category,
            FinalException = exception,
            CorrelationId  = correlationId,
        };

        foreach (var observer in _observers)
        {
            try { await observer.OnOutcomeAsync(outcome, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Retry observer {Observer} threw; continuing.", observer.GetType().Name);
            }
        }
    }

    private static readonly ThreadLocal<Random> _rngLocal =
        new(() => new Random(Environment.TickCount ^ Environment.CurrentManagedThreadId));
}

/// <summary>Thrown by the executor when the per-operation circuit breaker is open.</summary>
public sealed class CircuitOpenException : Exception
{
    public string OperationName { get; }
    public TimeSpan RetryAfter  { get; }

    public CircuitOpenException(string operationName, TimeSpan retryAfter)
        : base($"Circuit '{operationName}' is open; retry after {retryAfter}.")
    {
        OperationName = operationName;
        RetryAfter    = retryAfter;
    }
}
