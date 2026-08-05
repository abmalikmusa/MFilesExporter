namespace MFilesExporter.Application.Batching;

/// <summary>
/// A streaming source of batches. Implementations MUST yield one batch at a
/// time — never materialize the whole set — so memory usage is O(one batch).
/// The stream terminates naturally when the source is exhausted (yield break).
/// </summary>
public interface IBatchSource<T>
{
    /// <summary>
    /// Enumerates batches lazily. The engine consumes one, processes it fully,
    /// then advances the enumerator. Cancellation stops enumeration.
    /// </summary>
    IAsyncEnumerable<Batch<T>> ReadBatchesAsync(
        BatchContext context,
        CancellationToken cancellationToken);
}
