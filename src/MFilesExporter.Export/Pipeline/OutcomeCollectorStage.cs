using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

public sealed class OutcomeCollectorStage
{
    private readonly PipelineChannels _channels;
    private readonly IExportStateStore _stateStore;
    private readonly IManifestWriter _manifestWriter;
    private readonly PipelineOptions _pipelineOptions;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly IResiliencePipelineProvider _resilience;
    private readonly ILogger<OutcomeCollectorStage> _logger;

    public OutcomeCollectorStage(
        PipelineChannels channels,
        IExportStateStore stateStore,
        IManifestWriter manifestWriter,
        PipelineOptions pipelineOptions,
        MFilesSourceOptions sourceOptions,
        IResiliencePipelineProvider resilience,
        ILogger<OutcomeCollectorStage> logger)
    {
        _channels = channels;
        _stateStore = stateStore;
        _manifestWriter = manifestWriter;
        _pipelineOptions = pipelineOptions;
        _sourceOptions = sourceOptions;
        _resilience = resilience;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _channels.Outcomes.Reader;
        var batch = new List<ExportOutcome>(_pipelineOptions.OutcomeBatchSize);
        var checkpoint = new MonotonicCheckpoint();

        using var batchTimer = new PeriodicTimer(_pipelineOptions.OutcomeBatchFlushInterval);
        using var checkpointTimer = new PeriodicTimer(_pipelineOptions.CheckpointFlushInterval);

        var drain = DrainAsync(reader, batch, checkpoint, cancellationToken);
        var batchTick = TickBatchAsync(batchTimer, batch, cancellationToken);
        var checkpointTick = TickCheckpointAsync(checkpointTimer, checkpoint, cancellationToken);

        try
        {
            await drain.ConfigureAwait(false);
        }
        finally
        {
            batchTimer.Dispose();
            checkpointTimer.Dispose();

            List<ExportOutcome> tail;
            lock (batch)
            {
                tail = new List<ExportOutcome>(batch);
                batch.Clear();
            }
            if (tail.Count > 0)
            {
                await FlushBatchAsync(tail, CancellationToken.None).ConfigureAwait(false);
            }

            var finalCp = checkpoint.Read();
            if (finalCp > DocumentFileVersionKey.Origin)
            {
                await _stateStore.SaveCheckpointAsync(_sourceOptions.PartitionKey, finalCp, CancellationToken.None).ConfigureAwait(false);
            }

            await _manifestWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await AwaitSilently(batchTick).ConfigureAwait(false);
            await AwaitSilently(checkpointTick).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(
        System.Threading.Channels.ChannelReader<ExportOutcome> reader,
        List<ExportOutcome> batch,
        MonotonicCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await foreach (var outcome in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _manifestWriter.AppendAsync(outcome, cancellationToken).ConfigureAwait(false);

            bool shouldFlush;
            lock (batch)
            {
                batch.Add(outcome);
                shouldFlush = batch.Count >= _pipelineOptions.OutcomeBatchSize;
            }
            checkpoint.Advance(outcome.DocumentFileVersionKey);

            if (shouldFlush)
            {
                List<ExportOutcome> snapshot;
                lock (batch)
                {
                    snapshot = new List<ExportOutcome>(batch);
                    batch.Clear();
                }
                await FlushBatchAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TickBatchAsync(PeriodicTimer timer, List<ExportOutcome> batch, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<ExportOutcome> snapshot;
                lock (batch)
                {
                    if (batch.Count == 0) continue;
                    snapshot = new List<ExportOutcome>(batch);
                    batch.Clear();
                }
                await FlushBatchAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickCheckpointAsync(PeriodicTimer timer, MonotonicCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var v = checkpoint.Read();
                if (v > DocumentFileVersionKey.Origin)
                {
                    await _resilience.ExecuteAsync(
                        ResiliencePipelineNames.StateStore,
                        ct => new ValueTask(_stateStore.SaveCheckpointAsync(_sourceOptions.PartitionKey, v, ct)),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushBatchAsync(IReadOnlyCollection<ExportOutcome> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        try
        {
            await _resilience.ExecuteAsync(
                ResiliencePipelineNames.StateStore,
                ct => new ValueTask(_stateStore.RecordOutcomesAsync(batch, ct)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "State store batch write failed with {Count} entries", batch.Count);
            throw;
        }
    }

    private static async Task AwaitSilently(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private sealed class MonotonicCheckpoint
    {
        private readonly object _sync = new();
        private DocumentFileVersionKey _value = DocumentFileVersionKey.Origin;

        public void Advance(DocumentFileVersionKey candidate)
        {
            lock (_sync)
            {
                if (candidate > _value) _value = candidate;
            }
        }

        public DocumentFileVersionKey Read()
        {
            lock (_sync) { return _value; }
        }
    }
}
