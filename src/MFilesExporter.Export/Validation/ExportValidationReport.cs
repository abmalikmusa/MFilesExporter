namespace MFilesExporter.Export.Validation;

/// <summary>
/// Aggregate report from an <see cref="IExportValidationPipeline"/> run.
/// Every check that was executed contributes exactly one
/// <see cref="ValidationCheckResult"/>.
/// </summary>
public sealed record ExportValidationReport
{
    /// <summary>Per-check results in the order they ran.</summary>
    public required IReadOnlyList<ValidationCheckResult> Checks { get; init; }

    /// <summary>Total wall-clock time for the pipeline.</summary>
    public required TimeSpan TotalElapsed { get; init; }

    /// <summary>True when no check reported <see cref="ValidationCheckStatus.Failed"/>.</summary>
    public bool IsValid => !Checks.Any(c => c.Status == ValidationCheckStatus.Failed);

    /// <summary>True when at least one check failed.</summary>
    public bool HasFailures => Checks.Any(c => c.Status == ValidationCheckStatus.Failed);

    /// <summary>True when at least one check reported a warning.</summary>
    public bool HasWarnings => Checks.Any(c => c.Status == ValidationCheckStatus.Warning);

    /// <summary>
    /// True when every failure is retryable. Empty failure set returns true
    /// vacuously. Callers use this to decide between transient-retry vs
    /// permanent-failure paths.
    /// </summary>
    public bool AllFailuresRetryable =>
        !Checks.Any(c => c.Status == ValidationCheckStatus.Failed && !c.IsRetryable);

    /// <summary>Convenience: the list of failed checks.</summary>
    public IEnumerable<ValidationCheckResult> Failures =>
        Checks.Where(c => c.Status == ValidationCheckStatus.Failed);

    /// <summary>Convenience: single-line summary suitable for a log entry.</summary>
    public string ToSummaryLine() =>
        $"passed={Checks.Count(c => c.Status == ValidationCheckStatus.Passed)} " +
        $"warned={Checks.Count(c => c.Status == ValidationCheckStatus.Warning)} " +
        $"skipped={Checks.Count(c => c.Status == ValidationCheckStatus.Skipped)} " +
        $"failed={Checks.Count(c => c.Status == ValidationCheckStatus.Failed)} " +
        $"retryable={AllFailuresRetryable} elapsed={TotalElapsed}";
}
