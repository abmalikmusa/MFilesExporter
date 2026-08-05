namespace MFilesExporter.Application.Batching;

/// <summary>
/// Top-level driver: pulls batches from an <see cref="IBatchSource{T}"/>,
/// runs each through an <see cref="IBatchExecutor"/>, and stops when the
/// source is exhausted or a policy threshold trips.
/// </summary>
public interface IBatchCoordinator
{
    Task<BatchProcessingSummary> RunAsync<T>(
        IBatchSource<T> source,
        IBatchItemProcessor<T> processor,
        BatchContext context,
        CancellationToken cancellationToken);
}
