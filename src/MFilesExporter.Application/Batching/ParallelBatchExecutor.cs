using System.Diagnostics;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.Batching;

/// <summary>
/// Default <see cref="IBatchExecutor"/> implementation. Runs items in
/// parallel with a bounded <c>Parallel.ForEachAsync</c> and aggregates
/// counters with <c>Interlocked</c> — no locks, no shared mutable state
/// beyond three long counters.
/// </summary>
public sealed class ParallelBatchExecutor : IBatchExecutor
{
    private readonly BatchProcessingOptions _options;
    private readonly ILogger<ParallelBatchExecutor> _logger;

    public ParallelBatchExecutor(
        BatchProcessingOptions options,
        ILogger<ParallelBatchExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<BatchResult> ExecuteAsync<T>(
        Batch<T> batch,
        IBatchItemProcessor<T> processor,
        BatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(processor);

        if (batch.IsEmpty)
        {
            return new BatchResult
            {
                BatchNumber       = batch.BatchNumber,
                Size              = 0,
                SucceededCount    = 0, FailedCount = 0, SkippedCount = 0,
                TotalBytesWritten = 0,
                Elapsed           = TimeSpan.Zero,
            };
        }

        // Linked CTS: outer cancellation + per-batch timeout.
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batchCts.CancelAfter(_options.BatchTimeout);
        var ct = batchCts.Token;

        long succeeded = 0;
        long failed    = 0;
        long skipped   = 0;
        long bytes     = 0;
        var sw = Stopwatch.StartNew();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxParallelismPerBatch,
            CancellationToken      = ct,
        };

        try
        {
            await Parallel.ForEachAsync(batch.Items, parallelOptions, async (item, itemCt) =>
            {
                var itemResult = await SafeProcessAsync(processor, item, context, itemCt).ConfigureAwait(false);

                switch (itemResult.Outcome)
                {
                    case BatchItemOutcome.Succeeded:
                        Interlocked.Increment(ref succeeded);
                        Interlocked.Add(ref bytes, itemResult.BytesWritten);
                        break;

                    case BatchItemOutcome.Failed:
                        Interlocked.Increment(ref failed);
                        if (_options.StopOnFirstFailure)
                        {
                            _logger.LogWarning(
                                "Batch {BatchNumber} aborting on first failure (policy).",
                                batch.BatchNumber);
                            batchCts.Cancel();
                        }
                        break;

                    case BatchItemOutcome.Skipped:
                        Interlocked.Increment(ref skipped);
                        break;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (batchCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Batch {BatchNumber} exceeded timeout {Timeout} (or hit StopOnFirstFailure).",
                batch.BatchNumber, _options.BatchTimeout);
        }
        finally
        {
            sw.Stop();
        }

        var result = new BatchResult
        {
            BatchNumber       = batch.BatchNumber,
            Size              = batch.Size,
            SucceededCount    = (int)Interlocked.Read(ref succeeded),
            FailedCount       = (int)Interlocked.Read(ref failed),
            SkippedCount      = (int)Interlocked.Read(ref skipped),
            TotalBytesWritten = Interlocked.Read(ref bytes),
            Elapsed           = sw.Elapsed,
        };

        _logger.LogInformation(
            "Batch {BatchNumber} finished | size={Size} succeeded={Succeeded} failed={Failed} skipped={Skipped} bytes={Bytes} elapsed={Elapsed} items/s={Rate:F1}",
            result.BatchNumber, result.Size, result.SucceededCount, result.FailedCount,
            result.SkippedCount, result.TotalBytesWritten, result.Elapsed, result.ItemsPerSecond);

        return result;
    }

    /// <summary>
    /// Insulates the executor from throw-happy processors. A thrown exception
    /// is logged and converted to a Failed result so a bad implementation of
    /// <see cref="IBatchItemProcessor{T}"/> does not tear down the whole batch.
    /// </summary>
    private async Task<BatchItemResult> SafeProcessAsync<T>(
        IBatchItemProcessor<T> processor,
        T item,
        BatchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await processor.ProcessAsync(item, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processor threw for a batch item — converting to Failed outcome.");
            return BatchItemResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
