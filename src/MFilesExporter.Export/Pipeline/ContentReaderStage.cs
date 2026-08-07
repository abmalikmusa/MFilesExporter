using System.Diagnostics;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Monitoring;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Exceptions;
using MFilesExporter.Export.Telemetry;
using MFilesExporter.Logging.Workers;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

public sealed class ContentReaderStage
{
    private readonly IDocumentContentReader _reader;
    private readonly PipelineChannels _channels;
    private readonly PipelineOptions _options;
    private readonly IRetryExecutor _resilience;
    private readonly IClock _clock;
    private readonly IExporterMetrics _metrics;
    private readonly ILogger<ContentReaderStage> _logger;

    public ContentReaderStage(
        IDocumentContentReader reader,
        PipelineChannels channels,
        PipelineOptions options,
        IRetryExecutor resilience,
        IClock clock,
        IExporterMetrics metrics,
        ILogger<ContentReaderStage> logger)
    {
        _reader = reader;
        _channels = channels;
        _options = options;
        _resilience = resilience;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = new Task[_options.ContentReaderConcurrency];
        for (var i = 0; i < workers.Length; i++)
        {
            var id = i;
            workers[i] = Task.Run(() => WorkerLoopAsync(id, cancellationToken), cancellationToken);
        }
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            _channels.Content.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channels.Content.Writer.TryComplete(ex);
            throw;
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken cancellationToken)
    {
        using var _workerScope = WorkerLogScope.Enter(workerId, workerName: $"content-{workerId}");

        var enumeration = _channels.Enumeration.Reader;
        var outContent = _channels.Content.Writer;

        await foreach (var descriptor in enumeration.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var sqlSw = Stopwatch.StartNew();
            var sqlOk = false;
            try
            {
                var stream = await _resilience.ExecuteAsync(
                    RetryOperationNames.SqlBlobRead,
                    ct => new ValueTask<DocumentContentStream>(_reader.OpenAsync(descriptor.DataFileVersionKey, ct)),
                    cancellationToken).ConfigureAwait(false);

                sqlOk = true;
                _metrics.RecordSqlLatency("sql.blob-read", sqlSw.Elapsed, succeeded: true);
                await outContent.WriteAsync(new PreparedDocument(descriptor, stream, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentContentMissingException missing)
            {
                _logger.LogWarning(missing, "Content missing for {Key}", missing.Key);
                _metrics.RecordSqlLatency("sql.blob-read", sqlSw.Elapsed, succeeded: false);
                await _channels.Outcomes.Writer.WriteAsync(new ExportOutcome
                {
                    IdempotencyKey = descriptor.IdempotencyKey,
                    DocumentFileVersionKey = descriptor.DocumentFileVersionKey,
                    DataFileVersionKey = descriptor.DataFileVersionKey,
                    Status = ExportStatus.Skipped,
                    BytesWritten = 0,
                    FailureReason = "Committed BLOB not present",
                    ObservedAtUtc = _clock.UtcNow,
                    AttemptNumber = 1,
                }, cancellationToken).ConfigureAwait(false);
                PipelineTelemetry.DocumentsSkipped.Add(1);
                _metrics.RecordOutcome(DocumentOutcome.Skipped, bytesWritten: 0, sqlSw.Elapsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} content-fetch failed for {Key}", workerId, descriptor.DataFileVersionKey);
                if (!sqlOk) _metrics.RecordSqlLatency("sql.blob-read", sqlSw.Elapsed, succeeded: false);
                await _channels.Outcomes.Writer.WriteAsync(new ExportOutcome
                {
                    IdempotencyKey = descriptor.IdempotencyKey,
                    DocumentFileVersionKey = descriptor.DocumentFileVersionKey,
                    DataFileVersionKey = descriptor.DataFileVersionKey,
                    Status = ExportStatus.Failed,
                    BytesWritten = 0,
                    FailureReason = $"content-fetch: {ex.GetType().Name}: {ex.Message}",
                    ObservedAtUtc = _clock.UtcNow,
                    AttemptNumber = 1,
                }, cancellationToken).ConfigureAwait(false);
                PipelineTelemetry.DocumentsFailed.Add(1);
                _metrics.RecordOutcome(DocumentOutcome.Failed, bytesWritten: 0, sqlSw.Elapsed);
            }
        }
    }
}
