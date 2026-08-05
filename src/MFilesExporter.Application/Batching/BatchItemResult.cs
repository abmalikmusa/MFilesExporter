namespace MFilesExporter.Application.Batching;

/// <summary>
/// Result of processing a single batch item. Returned by an
/// <see cref="IBatchItemProcessor{T}"/> to the executor so the executor can
/// aggregate per-batch counts without inspecting side effects.
/// </summary>
public sealed record BatchItemResult
{
    /// <summary>Terminal state.</summary>
    public required BatchItemOutcome Outcome { get; init; }

    /// <summary>Optional reason surfaced by the processor.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Bytes written for this item (0 when Skipped/Failed).</summary>
    public long BytesWritten { get; init; }

    public static BatchItemResult Succeeded(long bytes) =>
        new() { Outcome = BatchItemOutcome.Succeeded, BytesWritten = bytes };

    public static BatchItemResult Failed(string reason) =>
        new() { Outcome = BatchItemOutcome.Failed, FailureReason = reason };

    public static BatchItemResult Skipped(string reason) =>
        new() { Outcome = BatchItemOutcome.Skipped, FailureReason = reason };
}
