namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Batch processing engine tuning. Defaults chosen so a single worker on
/// commodity hardware can push ~500 documents/second at 16-way parallelism
/// without saturating either the source database or the sink volume.
/// </summary>
public sealed class BatchProcessingOptions
{
    public const string SectionName = "Exporter:BatchProcessing";

    /// <summary>
    /// Documents per batch. Sweet spot is 1 000–5 000 — small enough to
    /// keep the working set bounded, large enough to amortize the claim
    /// round-trip.
    /// </summary>
    public int BatchSize { get; set; } = 2_000;

    /// <summary>Concurrent item processors within a single batch.</summary>
    public int MaxParallelismPerBatch { get; set; } = 16;

    /// <summary>Hard timeout for a single batch. Exceeding it cancels the batch and any in-flight items.</summary>
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Pause inserted between batches. Set to zero for continuous throughput.</summary>
    public TimeSpan PauseBetweenBatches { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When the per-batch failure ratio exceeds this value, the coordinator
    /// stops the run rather than tearing through the remaining source with
    /// something clearly wrong. Set to <c>1.0</c> to disable.
    /// </summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>
    /// If <c>true</c>, any Failed item aborts the current batch (still
    /// running items complete). Rarely appropriate; use the failure-rate
    /// threshold for softer control.
    /// </summary>
    public bool StopOnFirstFailure { get; set; }
}
