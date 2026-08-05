namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Root configuration for the enterprise retry engine. Contains one
/// <see cref="RetryPolicyProfile"/> per logical operation.
/// </summary>
/// <remarks>
/// Bound from <c>Exporter:RetryHandling</c>. Any operation missing here falls
/// back to <see cref="Default"/>.
/// </remarks>
public sealed class RetryHandlingOptions
{
    public const string SectionName = "Exporter:RetryHandling";

    /// <summary>Master switch. When false, the executor invokes the delegate once and never retries.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fallback profile used when an operation name is unknown.</summary>
    public RetryPolicyProfile Default { get; set; } = new()
    {
        MaxAttempts = 3, BaseDelayMilliseconds = 250, MaxDelaySeconds = 15, PerAttemptTimeoutSeconds = 60,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>SQL enumeration / small SELECTs.</summary>
    public RetryPolicyProfile SqlRead { get; set; } = new()
    {
        MaxAttempts = 5, BaseDelayMilliseconds = 500, MaxDelaySeconds = 30, PerAttemptTimeoutSeconds = 300,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>SQL streaming BLOB reads — long-running, expensive to fail late, so we grant more attempts.</summary>
    public RetryPolicyProfile SqlBlobRead { get; set; } = new()
    {
        MaxAttempts = 5, BaseDelayMilliseconds = 500, MaxDelaySeconds = 30, PerAttemptTimeoutSeconds = 600,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>Tracking-DB writes / work-claim UPDATEs.</summary>
    public RetryPolicyProfile SqlWrite { get; set; } = new()
    {
        MaxAttempts = 5, BaseDelayMilliseconds = 250, MaxDelaySeconds = 20, PerAttemptTimeoutSeconds = 120,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>File-sink writes: temp-file + rename.</summary>
    public RetryPolicyProfile DiskWrite { get; set; } = new()
    {
        MaxAttempts = 3, BaseDelayMilliseconds = 250, MaxDelaySeconds = 15, PerAttemptTimeoutSeconds = 300,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>Reading local files (checksum verification, WAL replay).</summary>
    public RetryPolicyProfile DiskRead { get; set; } = new()
    {
        MaxAttempts = 3, BaseDelayMilliseconds = 100, MaxDelaySeconds = 5, PerAttemptTimeoutSeconds = 60,
        JitterFactor = 0.25, CircuitBreaker = CircuitBreakerSettings.Disabled(),
    };

    /// <summary>SQLite / state-store operations — cheap, fast, high call rate.</summary>
    public RetryPolicyProfile StateStore { get; set; } = new()
    {
        MaxAttempts = 5, BaseDelayMilliseconds = 100, MaxDelaySeconds = 5, PerAttemptTimeoutSeconds = 30,
        JitterFactor = 0.5, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>Generic outbound network / HTTP.</summary>
    public RetryPolicyProfile Network { get; set; } = new()
    {
        MaxAttempts = 5, BaseDelayMilliseconds = 500, MaxDelaySeconds = 30, PerAttemptTimeoutSeconds = 60,
        JitterFactor = 0.5, CircuitBreaker = CircuitBreakerSettings.EnabledDefault(),
    };

    /// <summary>Per-category overrides. Missing entries fall back to <see cref="Default"/> caps.</summary>
    public CategoryOverrides Categories { get; set; } = new();
}

/// <summary>
/// Retry policy for a single logical operation. Combines exponential back-off,
/// per-attempt timeout, and (optionally) a circuit breaker.
/// </summary>
public sealed class RetryPolicyProfile
{
    /// <summary>Total attempts including the first call. Must be &gt;= 1.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base of the exponential back-off in milliseconds. Delay for attempt <c>n</c> ≈ Base · 2^(n-1).</summary>
    public int BaseDelayMilliseconds { get; set; } = 250;

    /// <summary>Hard ceiling on the sleep between attempts.</summary>
    public int MaxDelaySeconds { get; set; } = 30;

    /// <summary>Cancels a single attempt after this timeout — the executor retries as if the attempt failed.</summary>
    public int PerAttemptTimeoutSeconds { get; set; } = 60;

    /// <summary>Full-jitter multiplier: delay ∈ [(1-J)·planned, (1+J)·planned]. Set to 0 to disable.</summary>
    public double JitterFactor { get; set; } = 0.25;

    /// <summary>Circuit-breaker settings for this profile. Set <see cref="CircuitBreakerSettings.Enabled"/> to false to disable.</summary>
    public CircuitBreakerSettings CircuitBreaker { get; set; } = CircuitBreakerSettings.EnabledDefault();
}

/// <summary>Circuit-breaker configuration. See Polly v8 <c>CircuitBreakerStrategyOptions</c> for semantics.</summary>
public sealed class CircuitBreakerSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Fraction of failures within <see cref="SamplingDurationSeconds"/> that trips the breaker.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Minimum calls in the sample window before the breaker can trip.</summary>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>Sliding window used to evaluate <see cref="FailureRatio"/>.</summary>
    public int SamplingDurationSeconds { get; set; } = 30;

    /// <summary>How long the breaker stays open before transitioning to half-open.</summary>
    public int BreakDurationSeconds { get; set; } = 30;

    public static CircuitBreakerSettings EnabledDefault() => new() { Enabled = true };

    public static CircuitBreakerSettings Disabled() => new() { Enabled = false };
}

/// <summary>
/// Overrides applied per <see cref="MFilesExporter.Application.Abstractions.Retry.FailureCategory"/>.
/// Lets operators clamp behaviour globally, e.g. cap disk-full retries at 1
/// regardless of which operation raised it.
/// </summary>
public sealed class CategoryOverrides
{
    /// <summary>Deadlocks resolve quickly — small delay, no breaker.</summary>
    public CategoryOverride SqlDeadlock { get; set; } = new()
    {
        MaxAttemptsCap = 8,
        BaseDelayMilliseconds = 50,
        MaxDelaySeconds = 2,
        DisableCircuitBreaker = true,
    };

    /// <summary>Disk-full is nearly permanent — one polite retry in case another job frees space.</summary>
    public CategoryOverride DiskFull { get; set; } = new()
    {
        MaxAttemptsCap = 2,
        BaseDelayMilliseconds = 1000,
        MaxDelaySeconds = 5,
    };

    /// <summary>Rate-limited errors: honour a slightly larger back-off.</summary>
    public CategoryOverride RateLimited { get; set; } = new()
    {
        BaseDelayMilliseconds = 1000,
        MaxDelaySeconds = 60,
    };
}

/// <summary>Per-category caps. Any null field means "use the profile's value unchanged".</summary>
public sealed class CategoryOverride
{
    public int? MaxAttemptsCap { get; set; }
    public int? BaseDelayMilliseconds { get; set; }
    public int? MaxDelaySeconds { get; set; }
    public bool DisableCircuitBreaker { get; set; }
}
