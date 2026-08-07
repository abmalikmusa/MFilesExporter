using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Metadata;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

/// <summary>
/// Implements the application-layer <see cref="IExportPipeline"/> port. Starts
/// each stage concurrently, links their cancellation, and awaits their natural
/// completion order (producer -> content -> sink -> outcome collector).
/// Wraps the run with metadata generator lifecycle: Initialize before the
/// stages start, FinalizeAsync (writes <c>manifest.json</c>) after they drain.
/// </summary>
public sealed class ExportPipeline : IExportPipeline
{
    private readonly ProducerStage _producer;
    private readonly ContentReaderStage _contentReader;
    private readonly SinkStage _sink;
    private readonly OutcomeCollectorStage _outcomeCollector;
    private readonly IMetadataGenerator _metadata;
    private readonly IExportStateStore _stateStore;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly IClock _clock;
    private readonly ILogger<ExportPipeline> _logger;

    public ExportPipeline(
        ProducerStage producer,
        ContentReaderStage contentReader,
        SinkStage sink,
        OutcomeCollectorStage outcomeCollector,
        IMetadataGenerator metadata,
        IExportStateStore stateStore,
        MFilesSourceOptions sourceOptions,
        IClock clock,
        ILogger<ExportPipeline> logger)
    {
        _producer = producer;
        _contentReader = contentReader;
        _sink = sink;
        _outcomeCollector = outcomeCollector;
        _metadata = metadata;
        _stateStore = stateStore;
        _sourceOptions = sourceOptions;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;

        await _metadata.InitializeAsync(cancellationToken).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var producerTask = RunFaultingAsync(nameof(ProducerStage), _producer.RunAsync, linkedCts, ct);
        var contentTask = RunFaultingAsync(nameof(ContentReaderStage), _contentReader.RunAsync, linkedCts, ct);
        var sinkTask = RunFaultingAsync(nameof(SinkStage), _sink.RunAsync, linkedCts, ct);
        var outcomeTask = RunFaultingAsync(nameof(OutcomeCollectorStage), _outcomeCollector.RunAsync, linkedCts, ct);

        try
        {
            // await *sequentially* to preserve original exception ordering, but
            // wait for *every* stage before falling through — if we let
            // sink/outcome tasks run past the top-level throw they can still
            // be writing checkpoints/metadata after the caller has returned,
            // producing races with test code that reads the state store.
            var all = Task.WhenAll(producerTask, contentTask, sinkTask, outcomeTask);
            try { await all.ConfigureAwait(false); }
            catch
            {
                await producerTask.ConfigureAwait(false); // rethrow the natural order
                await contentTask.ConfigureAwait(false);
                await sinkTask.ConfigureAwait(false);
                await outcomeTask.ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            // Finalize metadata even when the pipeline was cancelled — we want
            // the partial manifest so operators can inspect what was written.
            try
            {
                var counters = await _stateStore.GetCountersAsync(CancellationToken.None).ConfigureAwait(false);
                var summary = new ManifestSummary
                {
                    JobId          = 0, // Populated by the orchestrator when it wraps this pipeline.
                    JobName        = "export",
                    PartitionKey   = _sourceOptions.PartitionKey,
                    SourceServer   = "vault",
                    SourceDatabase = "MFilesVault",
                    StartedAtUtc   = startedAt.UtcDateTime,
                    CompletedAtUtc = _clock.UtcNow.UtcDateTime,
                    Totals         = new ManifestTotals(
                        DocumentsExpected: counters.TotalRecorded,
                        DocumentsRecorded: counters.TotalRecorded,
                        Succeeded:         counters.TotalSucceeded,
                        Failed:            counters.TotalFailed,
                        Skipped:           counters.TotalSkipped,
                        TotalBytesWritten: counters.TotalBytesWritten),
                    Artifacts = Array.Empty<ManifestArtifactReference>(),
                };
                await _metadata.FinalizeAsync(summary, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata finalization failed; manifest may be missing");
            }
        }
    }

    private async Task RunFaultingAsync(
        string name,
        Func<CancellationToken, Task> body,
        CancellationTokenSource linkedCts,
        CancellationToken ct)
    {
        try
        {
            await body(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Pipeline stage {Stage} faulted", name);
            linkedCts.Cancel();
            throw;
        }
    }
}
