namespace MFilesExporter.Application.Batching;

/// <summary>
/// Executes one batch, in parallel across items. Only ONE batch runs at a
/// time from the coordinator's perspective; the parallelism is bounded per
/// batch (default 16) so memory + external-service load stay predictable.
/// </summary>
public interface IBatchExecutor
{
    Task<BatchResult> ExecuteAsync<T>(
        Batch<T> batch,
        IBatchItemProcessor<T> processor,
        BatchContext context,
        CancellationToken cancellationToken);
}
