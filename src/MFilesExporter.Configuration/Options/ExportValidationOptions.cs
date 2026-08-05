namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the post-export validation pipeline. Every exported
/// document runs the pipeline immediately after the sink write; failures
/// are surfaced to the caller as either retryable (transient) or
/// deterministic depending on which check tripped.
/// </summary>
public sealed class ExportValidationOptions
{
    public const string SectionName = "Exporter:Validation";

    /// <summary>Master switch — set to false to disable all post-export validation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Order of execution: cheap checks first, expensive checks last (see the pipeline docs).</summary>
    public ValidationExecutionMode Mode { get; set; } = ValidationExecutionMode.FailFast;

    /// <summary>
    /// Optional allowlist of validator names. Empty = run every registered
    /// validator. Names are case-insensitive.
    /// </summary>
    public HashSet<string> EnabledValidators { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-validator timeout. A validator exceeding this raises a retryable failure.</summary>
    public TimeSpan PerValidatorTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When true, the checksum validator re-computes the file's hash from
    /// disk and compares to the expected value. When false, only the file
    /// hash produced by the sink at write time is trusted.
    /// </summary>
    public bool RerunChecksumFromFile { get; set; } = true;

    /// <summary>
    /// Treat extension mismatch as a warning rather than a failure. Useful
    /// during migrations where the target extension convention differs
    /// from the source.
    /// </summary>
    public bool AllowExtensionMismatch { get; set; }

    /// <summary>Also validate the metadata record when supplied. Off by default because callers may not always populate it.</summary>
    public bool ValidateMetadataConsistency { get; set; } = true;
}

/// <summary>How the pipeline treats a failing validator.</summary>
public enum ValidationExecutionMode
{
    /// <summary>Stop on the first failure — cheapest and most common.</summary>
    FailFast,
    /// <summary>Run every validator and aggregate all failures — best for diagnostics / reruns.</summary>
    RunAll,
}
