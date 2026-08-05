namespace MFilesExporter.Application.Batching;

/// <summary>
/// Processes a single item within a batch. Implementations SHOULD:
/// <list type="bullet">
///   <item><description>Complete quickly (target: seconds, not minutes).</description></item>
///   <item><description>Be stateless — the executor invokes this from multiple threads.</description></item>
///   <item><description>Never throw for expected failures — return a Failed result instead.</description></item>
///   <item><description>Honor <paramref name="cancellationToken"/> — the executor may cancel mid-batch.</description></item>
/// </list>
/// </summary>
public interface IBatchItemProcessor<T>
{
    Task<BatchItemResult> ProcessAsync(
        T item,
        BatchContext context,
        CancellationToken cancellationToken);
}
