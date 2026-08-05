using MFilesExporter.Domain.Validation;

namespace MFilesExporter.Domain.Jobs;

/// <summary>
/// Domain-level configuration for a single export run. Snapshotted at job
/// start and never mutated — a mid-run configuration change would break
/// resumability guarantees.
/// </summary>
/// <remarks>
/// This is the domain projection of the Application-layer options tree. It
/// contains only the fields that participate in business rules; connection
/// strings, file paths, and other infrastructure concerns stay in the
/// options classes.
/// </remarks>
public sealed record ExportConfiguration
{
    /// <summary>
    /// Partition key that scopes the enumeration cursor. Two jobs with the
    /// same key share a resumption checkpoint; two jobs with different keys
    /// are independent.
    /// </summary>
    public required string PartitionKey { get; init; }

    /// <summary>Rows fetched per enumeration query. Sweet spot: 1 000–5 000.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Concurrent BLOB fetchers.</summary>
    public required int ContentReaderConcurrency { get; init; }

    /// <summary>Concurrent sink writers.</summary>
    public required int SinkConcurrency { get; init; }

    /// <summary>Documents whose logical size exceeds this are skipped; <c>0</c> disables the guard.</summary>
    public required int MaxDocumentSizeMb { get; init; }

    /// <summary>How often progress snapshots are published.</summary>
    public required TimeSpan ProgressReportInterval { get; init; }

    /// <summary>How often the enumeration checkpoint is flushed to the tracking DB.</summary>
    public required TimeSpan CheckpointFlushInterval { get; init; }

    /// <summary>
    /// Enumeration query isolation. <c>true</c> = READ UNCOMMITTED (recommended
    /// for live vaults so we do not block M-Files sessions).
    /// </summary>
    public required bool UseReadUncommittedForEnumeration { get; init; }

    /// <summary>Retry policy applied to every I/O boundary.</summary>
    public required Retry.RetryPolicy Retry { get; init; }

    /// <summary>
    /// Validates the configuration against business rules. Returns
    /// <see cref="ValidationResult.Valid"/> when configuration is coherent.
    /// </summary>
    public ValidationResult Validate()
    {
        var failures = new List<ValidationFailure>();

        if (string.IsNullOrWhiteSpace(PartitionKey))
            failures.Add(new ValidationFailure(nameof(PartitionKey), "REQUIRED", "PartitionKey is required."));

        if (BatchSize < 50 || BatchSize > 100_000)
            failures.Add(new ValidationFailure(nameof(BatchSize), "RANGE", "BatchSize must be between 50 and 100 000."));

        if (ContentReaderConcurrency < 1 || ContentReaderConcurrency > 256)
            failures.Add(new ValidationFailure(nameof(ContentReaderConcurrency), "RANGE", "Concurrency must be between 1 and 256."));

        if (SinkConcurrency < 1 || SinkConcurrency > 256)
            failures.Add(new ValidationFailure(nameof(SinkConcurrency), "RANGE", "Concurrency must be between 1 and 256."));

        if (MaxDocumentSizeMb < 0)
            failures.Add(new ValidationFailure(nameof(MaxDocumentSizeMb), "RANGE", "MaxDocumentSizeMb must be >= 0."));

        if (ProgressReportInterval <= TimeSpan.Zero)
            failures.Add(new ValidationFailure(nameof(ProgressReportInterval), "RANGE", "Must be a positive TimeSpan."));

        if (CheckpointFlushInterval <= TimeSpan.Zero)
            failures.Add(new ValidationFailure(nameof(CheckpointFlushInterval), "RANGE", "Must be a positive TimeSpan."));

        return failures.Count == 0 ? ValidationResult.Valid : ValidationResult.Invalid(failures);
    }
}
