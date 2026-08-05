using System.Diagnostics;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Telemetry;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

public sealed class SinkStage
{
    private readonly IDocumentSink _sink;
    private readonly PipelineChannels _channels;
    private readonly PipelineOptions _options;
    private readonly IResiliencePipelineProvider _resilience;
    private readonly IClock _clock;
    private readonly ILogger<SinkStage> _logger;

    public SinkStage(
        IDocumentSink sink,
        PipelineChannels channels,
        PipelineOptions options,
        IResiliencePipelineProvider resilience,
        IClock clock,
        ILogger<SinkStage> logger)
    {
        _sink = sink;
        _channels = channels;
        _options = options;
        _resilience = resilience;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = new Task[_options.SinkConcurrency];
        for (var i = 0; i < workers.Length; i++)
        {
            var id = i;
            workers[i] = Task.Run(() => WorkerLoopAsync(id, cancellationToken), cancellationToken);
        }
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            _channels.Outcomes.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channels.Outcomes.Writer.TryComplete(ex);
            throw;
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken cancellationToken)
    {
        await foreach (var prepared in _channels.Content.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var descriptor = prepared.Descriptor;
            var sw = Stopwatch.StartNew();
            ExportOutcome outcome;

            try
            {
                var result = await _resilience.ExecuteAsync(
                    ResiliencePipelineNames.DiskWrite,
                    ct => new ValueTask<DocumentSinkResult>(_sink.WriteAsync(descriptor, prepared.ContentStream.Content, ct)),
                    cancellationToken).ConfigureAwait(false);

                outcome = new ExportOutcome
                {
                    IdempotencyKey = descriptor.IdempotencyKey,
                    DocumentFileVersionKey = descriptor.DocumentFileVersionKey,
                    DataFileVersionKey = descriptor.DataFileVersionKey,
                    Status = ExportStatus.Succeeded,
                    BytesWritten = result.BytesWritten,
                    OutputPath = result.OutputPath,
                    Checksum = result.ChecksumHex,
                    ObservedAtUtc = _clock.UtcNow,
                    AttemptNumber = prepared.AttemptNumber,
                };

                PipelineTelemetry.DocumentsSucceeded.Add(1);
                PipelineTelemetry.BytesWritten.Add(result.BytesWritten);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} sink failed for {Key}", workerId, descriptor.DocumentFileVersionKey);
                outcome = new ExportOutcome
                {
                    IdempotencyKey = descriptor.IdempotencyKey,
                    DocumentFileVersionKey = descriptor.DocumentFileVersionKey,
                    DataFileVersionKey = descriptor.DataFileVersionKey,
                    Status = ExportStatus.Failed,
                    BytesWritten = 0,
                    FailureReason = $"sink: {ex.GetType().Name}: {ex.Message}",
                    ObservedAtUtc = _clock.UtcNow,
                    AttemptNumber = prepared.AttemptNumber,
                };
                PipelineTelemetry.DocumentsFailed.Add(1);
            }
            finally
            {
                await prepared.ContentStream.DisposeAsync().ConfigureAwait(false);
                sw.Stop();
                PipelineTelemetry.DocumentDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            }

            await _channels.Outcomes.Writer.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
    }
}
