using System.Threading.Channels;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Monitoring;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

public sealed class ProducerStage
{
    private readonly IDocumentEnumerator _enumerator;
    private readonly IExportStateStore _stateStore;
    private readonly PipelineChannels _channels;
    private readonly PipelineOptions _pipelineOptions;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly IResiliencePipelineProvider _resilience;
    private readonly IExporterMetrics _metrics;
    private readonly ILogger<ProducerStage> _logger;

    public ProducerStage(
        IDocumentEnumerator enumerator,
        IExportStateStore stateStore,
        PipelineChannels channels,
        PipelineOptions pipelineOptions,
        MFilesSourceOptions sourceOptions,
        IResiliencePipelineProvider resilience,
        IExporterMetrics metrics,
        ILogger<ProducerStage> logger)
    {
        _enumerator = enumerator;
        _stateStore = stateStore;
        _channels = channels;
        _pipelineOptions = pipelineOptions;
        _sourceOptions = sourceOptions;
        _resilience = resilience;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var partitionKey = _sourceOptions.PartitionKey;
        var checkpoint = await _stateStore.GetCheckpointAsync(partitionKey, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Producer starting for partition {Partition} from {Checkpoint}", partitionKey, checkpoint);

        var writer = _channels.Enumeration.Writer;
        try
        {
            var pending = new List<DocumentDescriptor>(_sourceOptions.EnumerationBatchSize);
            await foreach (var descriptor in _enumerator.EnumerateAsync(checkpoint, cancellationToken).ConfigureAwait(false))
            {
                if (_pipelineOptions.MaxDocumentSizeMb > 0 &&
                    descriptor.LogicalFileSize > (long)_pipelineOptions.MaxDocumentSizeMb * 1024L * 1024L)
                {
                    _logger.LogWarning("Skipping oversize {Key} bytes={Size}",
                        descriptor.DocumentFileVersionKey, descriptor.LogicalFileSize);
                    continue;
                }

                _metrics.RecordEnumerated();
                pending.Add(descriptor);
                if (pending.Count >= _sourceOptions.EnumerationBatchSize)
                {
                    await DrainAsync(writer, pending, cancellationToken).ConfigureAwait(false);
                    pending.Clear();
                }
            }
            if (pending.Count > 0)
            {
                await DrainAsync(writer, pending, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer failed");
            writer.TryComplete(ex);
            throw;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DrainAsync(
        ChannelWriter<DocumentDescriptor> writer,
        IReadOnlyList<DocumentDescriptor> batch,
        CancellationToken cancellationToken)
    {
        var keys = new List<IdempotencyKey>(batch.Count);
        foreach (var d in batch) keys.Add(d.IdempotencyKey);

        var statuses = await _resilience.ExecuteAsync(
            ResiliencePipelineNames.StateStore,
            ct => new ValueTask<IReadOnlyDictionary<IdempotencyKey, ExportStatus>>(
                _stateStore.GetStatusesAsync(keys, ct)),
            cancellationToken).ConfigureAwait(false);

        foreach (var d in batch)
        {
            if (statuses.TryGetValue(d.IdempotencyKey, out var s)
                && (s == ExportStatus.Succeeded || s == ExportStatus.Skipped))
            {
                continue;
            }
            await writer.WriteAsync(d, cancellationToken).ConfigureAwait(false);
        }
    }
}
