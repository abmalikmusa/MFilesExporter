namespace MFilesExporter.Export.Validation;

/// <summary>Terminal state of a single validation check.</summary>
public enum ValidationCheckStatus
{
    /// <summary>Check ran and passed.</summary>
    Passed = 0,
    /// <summary>Check ran and failed.</summary>
    Failed = 1,
    /// <summary>Check was intentionally skipped (missing precondition).</summary>
    Skipped = 2,
    /// <summary>Check surfaced a concern but did not fail.</summary>
    Warning = 3,
}

/// <summary>Result of one validator's run. Immutable, JSON-serializable.</summary>
public sealed record ValidationCheckResult
{
    /// <summary>Canonical name of the validator (used for enable-lists and logging).</summary>
    public required string ValidatorName { get; init; }

    /// <summary>Terminal state.</summary>
    public required ValidationCheckStatus Status { get; init; }

    /// <summary>
    /// Human-readable explanation for Failed / Warning / Skipped outcomes.
    /// Null on Passed.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// When true, callers should retry the whole export. When false, the
    /// failure is deterministic and retrying will not help.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>Optional structured hint attached by the validator.</summary>
    public string? Detail { get; init; }

    /// <summary>Time spent inside the validator.</summary>
    public required TimeSpan Elapsed { get; init; }

    public static ValidationCheckResult Passed(string name, TimeSpan elapsed, string? detail = null) =>
        new()
        {
            ValidatorName = name,
            Status        = ValidationCheckStatus.Passed,
            IsRetryable   = false,
            Detail        = detail,
            Elapsed       = elapsed,
        };

    public static ValidationCheckResult Failed(
        string name, TimeSpan elapsed, string reason, bool isRetryable) =>
        new()
        {
            ValidatorName = name,
            Status        = ValidationCheckStatus.Failed,
            FailureReason = reason,
            IsRetryable   = isRetryable,
            Elapsed       = elapsed,
        };

    public static ValidationCheckResult Skipped(string name, TimeSpan elapsed, string reason) =>
        new()
        {
            ValidatorName = name,
            Status        = ValidationCheckStatus.Skipped,
            FailureReason = reason,
            IsRetryable   = false,
            Elapsed       = elapsed,
        };

    public static ValidationCheckResult Warning(string name, TimeSpan elapsed, string reason) =>
        new()
        {
            ValidatorName = name,
            Status        = ValidationCheckStatus.Warning,
            FailureReason = reason,
            IsRetryable   = false,
            Elapsed       = elapsed,
        };
}
