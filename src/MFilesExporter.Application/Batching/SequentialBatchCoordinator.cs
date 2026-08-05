using System.Diagnostics;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.Batching;

/// <summary>
/// Default <see cref="IBatchCoordinator"/>. Consumes the source one batch
/// at a time — <c>await foreach</c> makes the enumeration inherently
/// streaming. Delegates parallelism strictly to the executor so the
/// coordinator itself is single-threaded.
/// </summary>
public sealed class SequentialBatchCoordinator : IBatchCoordinator
{
    private readonly IBatchExecutor _executor;
    private readonly BatchProcessingOptions _options;
    private readonly ILogger<SequentialBatchCoordinator> _logger;

    public SequentialBatchCoordinator(
        IBatchExecutor executor,
        BatchProcessingOptions options,
        ILogger<SequentialBatchCoordinator> logger)
    {
        _executor = executor;
        _options = options;
        _logger = logger;
    }

    public async Task<BatchProcessingSummary> RunAsync<T>(
        IBatchSource<T> source,
        IBatchItemProcessor<T> processor,
        BatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(context);

        long totalBatches = 0;
        long totalItems = 0;
        long totalSucceeded = 0;
        long totalFailed = 0;
        long totalSkipped = 0;
        long totalBytes = 0;
        var abortedOnThreshold = false;
        var exhaustedSource = false;

        var runSw = Stopwatch.StartNew();

        try
        {
            await foreach (var batch in source.ReadBatchesAsync(context, cancellationToken).ConfigureAwait(false))
            {
                if (batch.IsEmpty)
                {
                    // Source signalled exhaustion.
                    exhaustedSource = true;
                    break;
                }

                _logger.LogInformation(
                    "Starting batch {BatchNumber} | size={Size}",
                    batch.BatchNumber, batch.Size);

                var result = await _executor.ExecuteAsync(batch, processor, context, cancellationToken)
                    .ConfigureAwait(false);

                totalBatches++;
                totalItems      += result.Size;
                totalSucceeded  += result.SucceededCount;
                totalFailed     += result.FailedCount;
                totalSkipped    += result.SkippedCount;
                totalBytes      += result.TotalBytesWritten;

                // Failure-rate policy — stop the run if a batch is clearly wrong.
                if (result.Size > 0
                    && _options.FailureRateThreshold < 1.0
                    && result.FailureRate > _options.FailureRateThreshold)
                {
                    _logger.LogError(
                        "Batch {BatchNumber} failure rate {Rate:P1} exceeded threshold {Threshold:P1} — aborting run.",
                        result.BatchNumber, result.FailureRate, _options.FailureRateThreshold);
                    abortedOnThreshold = true;
                    break;
                }

                if (_options.PauseBetweenBatches > TimeSpan.Zero)
                {
                    await Task.Delay(_options.PauseBetweenBatches, cancellationToken).ConfigureAwait(false);
                }
            }

            // Natural end of the async enumeration — no empty batch sentinel required.
            if (!abortedOnThreshold) exhaustedSource = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Batch coordinator cancelled after {Batches} batches.", totalBatches);
        }
        finally
        {
            runSw.Stop();
        }

        var summary = new BatchProcessingSummary
        {
            TotalBatches       = totalBatches,
            TotalItems         = totalItems,
            TotalSucceeded     = totalSucceeded,
            TotalFailed        = totalFailed,
            TotalSkipped       = totalSkipped,
            TotalBytesWritten  = totalBytes,
            Elapsed            = runSw.Elapsed,
            ExhaustedSource    = exhaustedSource,
            AbortedOnThreshold = abortedOnThreshold,
        };

        _logger.LogInformation(
            "Batch run finished | batches={Batches} items={Items} succeeded={Succeeded} failed={Failed} skipped={Skipped} elapsed={Elapsed} items/s={Rate:F1}",
            summary.TotalBatches, summary.TotalItems, summary.TotalSucceeded,
            summary.TotalFailed, summary.TotalSkipped, summary.Elapsed, summary.ItemsPerSecond);

        return summary;
    }
}
