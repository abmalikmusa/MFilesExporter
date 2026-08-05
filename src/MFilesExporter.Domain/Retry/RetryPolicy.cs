using MFilesExporter.Domain.Validation;

namespace MFilesExporter.Domain.Retry;

/// <summary>
/// Deterministic description of a retry policy. Immutable, serializable, and
/// interpretable by any implementation (Polly, Azure SDK, hand-rolled).
/// </summary>
/// <remarks>
/// The domain never imports a resilience library — this record is the
/// contract. Infrastructure maps it to whatever backend policy engine it
/// chooses. Storing the policy in the domain lets it be versioned alongside
/// the job for full audit.
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>Maximum retry attempts after the initial call. Zero disables retries.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>Initial delay before the first retry.</summary>
    public required TimeSpan InitialDelay { get; init; }

    /// <summary>Ceiling for backoff — no single delay exceeds this.</summary>
    public required TimeSpan MaxDelay { get; init; }

    /// <summary>Multiplier used to grow the delay between attempts (exponential base).</summary>
    public required double BackoffMultiplier { get; init; }

    /// <summary>Whether to apply randomization (jitter) to computed delays.</summary>
    public required bool UseJitter { get; init; }

    /// <summary>Absolute timeout for a single attempt.</summary>
    public required TimeSpan AttemptTimeout { get; init; }

    /// <summary>Cheap default suitable for read-mostly SQL access.</summary>
    public static RetryPolicy Default { get; } = new()
    {
        MaxAttempts = 5,
        InitialDelay = TimeSpan.FromMilliseconds(250),
        MaxDelay = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        UseJitter = true,
        AttemptTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Policy for BLOB streaming — same shape, longer timeout.</summary>
    public static RetryPolicy ForBlobRead { get; } = Default with
    {
        AttemptTimeout = TimeSpan.FromMinutes(10),
        MaxDelay = TimeSpan.FromMinutes(1),
    };

    /// <summary>Computes the delay before attempt <paramref name="attempt"/> (0-based).</summary>
    public TimeSpan ComputeDelay(int attempt)
    {
        if (attempt < 0) return TimeSpan.Zero;
        var raw = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt);
        var clamped = Math.Min(raw, MaxDelay.TotalMilliseconds);
        if (UseJitter)
        {
            // Deterministic sample from the process-scoped RNG. Values sit in [0.75×, 1.25×].
            var jitter = 0.75 + (System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 501) / 1000d);
            clamped *= jitter;
        }
        return TimeSpan.FromMilliseconds(clamped);
    }

    /// <summary>Validates that the policy values form a coherent schedule.</summary>
    public ValidationResult Validate()
    {
        var failures = new List<ValidationFailure>();

        if (MaxAttempts < 0)
            failures.Add(new ValidationFailure(nameof(MaxAttempts), "RANGE", "MaxAttempts must be >= 0."));

        if (InitialDelay < TimeSpan.Zero)
            failures.Add(new ValidationFailure(nameof(InitialDelay), "RANGE", "InitialDelay must be non-negative."));

        if (MaxDelay < InitialDelay)
            failures.Add(new ValidationFailure(nameof(MaxDelay), "RANGE", "MaxDelay must be >= InitialDelay."));

        if (BackoffMultiplier < 1.0)
            failures.Add(new ValidationFailure(nameof(BackoffMultiplier), "RANGE", "BackoffMultiplier must be >= 1.0."));

        if (AttemptTimeout <= TimeSpan.Zero)
            failures.Add(new ValidationFailure(nameof(AttemptTimeout), "RANGE", "AttemptTimeout must be positive."));

        return failures.Count == 0 ? ValidationResult.Valid : ValidationResult.Invalid(failures);
    }
}
