using System.Runtime.CompilerServices;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.Batching;

/// <summary>
/// Streams batches by repeatedly calling <see cref="IWorkClaimStore.ClaimAsync"/>.
/// The store returns items in shards (one batch per round-trip) so this
/// source is inherently memory-bounded — never accumulates more than one
/// batch worth of items in the enumerator.
///
/// This is the source that makes the exporter resumable: on restart, work
/// that a crashed worker previously held has been returned to Available by
/// the reaper, and this source picks it up on the next call.
/// </summary>
public sealed class WorkClaimBatchSource : IBatchSource<ClaimedWorkItem>
{
    private readonly IWorkClaimStore _store;
    private readonly BatchProcessingOptions _options;
    private readonly IClock _clock;

    public WorkClaimBatchSource(
        IWorkClaimStore store,
        BatchProcessingOptions options,
        IClock clock)
    {
        _store = store;
        _options = options;
        _clock = clock;
    }

    public async IAsyncEnumerable<Batch<ClaimedWorkItem>> ReadBatchesAsync(
        BatchContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var lease = TimeSpan.FromMinutes(5); // matches recommended default
        long batchNumber = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var items = await _store.ClaimAsync(
                context.ExportJobId,
                context.WorkerId,
                _options.BatchSize,
                lease,
                cancellationToken).ConfigureAwait(false);

            if (items.Count == 0)
            {
                yield break;  // source exhausted — coordinator sees end-of-stream
            }

            batchNumber++;
            yield return new Batch<ClaimedWorkItem>
            {
                BatchNumber  = batchNumber,
                Items        = items,
                FetchedAtUtc = _clock.UtcNow,
            };
        }
    }
}
