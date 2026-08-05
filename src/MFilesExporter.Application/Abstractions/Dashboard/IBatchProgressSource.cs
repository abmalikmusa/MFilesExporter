namespace MFilesExporter.Application.Abstractions.Dashboard;

/// <summary>
/// Optional source for the "Current Batch" panel. Any component that owns a
/// batch identity — the batch processing engine, the checkpoint engine —
/// may implement this to expose the current active batch to the dashboard.
/// </summary>
/// <remarks>
/// The dashboard treats <c>null</c> as "no active batch" (which prints as a
/// dimmed placeholder) so this signal is safe to leave unregistered on
/// non-batched workloads.
/// </remarks>
public interface IBatchProgressSource
{
    string? CurrentBatchId { get; }
    long CurrentBatchSize { get; }
    long CurrentBatchProcessed { get; }
}
