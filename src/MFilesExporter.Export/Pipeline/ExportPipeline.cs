using MFilesExporter.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

/// <summary>
/// Implements the application-layer <see cref="IExportPipeline"/> port. Starts
/// each stage concurrently, links their cancellation, and awaits their natural
/// completion order (producer -> content -> sink -> outcome collector).
/// </summary>
public sealed class ExportPipeline : IExportPipeline
{
    private readonly ProducerStage _producer;
    private readonly ContentReaderStage _contentReader;
    private readonly SinkStage _sink;
    private readonly OutcomeCollectorStage _outcomeCollector;
    private readonly ILogger<ExportPipeline> _logger;

    public ExportPipeline(
        ProducerStage producer,
        ContentReaderStage contentReader,
        SinkStage sink,
        OutcomeCollectorStage outcomeCollector,
        ILogger<ExportPipeline> logger)
    {
        _producer = producer;
        _contentReader = contentReader;
        _sink = sink;
        _outcomeCollector = outcomeCollector;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var producerTask = RunFaultingAsync(nameof(ProducerStage), _producer.RunAsync, linkedCts, ct);
        var contentTask = RunFaultingAsync(nameof(ContentReaderStage), _contentReader.RunAsync, linkedCts, ct);
        var sinkTask = RunFaultingAsync(nameof(SinkStage), _sink.RunAsync, linkedCts, ct);
        var outcomeTask = RunFaultingAsync(nameof(OutcomeCollectorStage), _outcomeCollector.RunAsync, linkedCts, ct);

        await producerTask.ConfigureAwait(false);
        await contentTask.ConfigureAwait(false);
        await sinkTask.ConfigureAwait(false);
        await outcomeTask.ConfigureAwait(false);
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
