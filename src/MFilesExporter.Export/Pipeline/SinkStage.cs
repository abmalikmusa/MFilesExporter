using System.Diagnostics;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Metadata;
using MFilesExporter.Export.Telemetry;
using MFilesExporter.Export.Validation;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Pipeline;

public sealed class SinkStage
{
    private readonly IDocumentSink _sink;
    private readonly PipelineChannels _channels;
    private readonly PipelineOptions _options;
    private readonly IResiliencePipelineProvider _resilience;
    private readonly IClock _clock;
    private readonly IExportValidationPipeline _validation;
    private readonly IMetadataGenerator _metadata;
    private readonly ExportValidationOptions _validationOptions;
    private readonly FileExportOptions _fileExportOptions;
    private readonly ILogger<SinkStage> _logger;

    public SinkStage(
        IDocumentSink sink,
        PipelineChannels channels,
        PipelineOptions options,
        IResiliencePipelineProvider resilience,
        IClock clock,
        IExportValidationPipeline validation,
        IMetadataGenerator metadata,
        ExportValidationOptions validationOptions,
        FileExportOptions fileExportOptions,
        ILogger<SinkStage> logger)
    {
        _sink = sink;
        _channels = channels;
        _options = options;
        _resilience = resilience;
        _clock = clock;
        _validation = validation;
        _metadata = metadata;
        _validationOptions = validationOptions;
        _fileExportOptions = fileExportOptions;
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

                // Post-write validation — file exists / size / extension / checksum / etc.
                // A failed validation downgrades the outcome; retryable failures propagate
                // up to the batch coordinator via IsRetryable, while permanent ones become
                // Failed on this worker.
                var validation = await RunValidationAsync(descriptor, result, cancellationToken)
                    .ConfigureAwait(false);

                if (!validation.IsValid)
                {
                    var reason = string.Join("; ", validation.Failures.Select(f => f.FailureReason ?? f.ValidatorName));
                    _logger.LogWarning(
                        "Worker {WorkerId} validation failed for {Key}: {Reason}",
                        workerId, descriptor.DocumentFileVersionKey, reason);

                    outcome = new ExportOutcome
                    {
                        IdempotencyKey = descriptor.IdempotencyKey,
                        DocumentFileVersionKey = descriptor.DocumentFileVersionKey,
                        DataFileVersionKey = descriptor.DataFileVersionKey,
                        Status = ExportStatus.Failed,
                        BytesWritten = result.BytesWritten,
                        OutputPath = result.OutputPath,
                        Checksum = result.ChecksumHex,
                        FailureReason = $"validation: {reason}",
                        ObservedAtUtc = _clock.UtcNow,
                        AttemptNumber = prepared.AttemptNumber,
                    };
                    PipelineTelemetry.DocumentsFailed.Add(1);
                }
                else
                {
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

                    // Metadata append runs only for validated, successful writes so
                    // the manifest does not record a broken artifact as valid.
                    await AppendMetadataAsync(descriptor, outcome, workerId, cancellationToken)
                        .ConfigureAwait(false);

                    PipelineTelemetry.DocumentsSucceeded.Add(1);
                    PipelineTelemetry.BytesWritten.Add(result.BytesWritten);
                }
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

    private async Task<ExportValidationReport> RunValidationAsync(
        DocumentDescriptor descriptor,
        DocumentSinkResult sinkResult,
        CancellationToken cancellationToken)
    {
        if (!_validationOptions.Enabled)
        {
            return new ExportValidationReport { Checks = Array.Empty<ValidationCheckResult>(), TotalElapsed = TimeSpan.Zero };
        }

        var context = new ExportValidationContext
        {
            Descriptor            = descriptor,
            OutputPath            = sinkResult.OutputPath,
            ExpectedByteCount     = sinkResult.BytesWritten,
            ExpectedChecksumHex   = sinkResult.ChecksumHex ?? string.Empty,
            ExpectedExtension     = descriptor.Extension ?? string.Empty,
            ExpectedRootDirectory = _fileExportOptions.RootPath,
        };

        return await _validation.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendMetadataAsync(
        DocumentDescriptor descriptor,
        ExportOutcome outcome,
        int workerId,
        CancellationToken cancellationToken)
    {
        var record = new MetadataRecord
        {
            DocumentPartId    = descriptor.DocumentFileVersionKey.DocumentFilePartId,
            VersionPart       = descriptor.DocumentFileVersionKey.VersionPartId,
            Title             = descriptor.Title ?? string.Empty,
            Extension         = descriptor.Extension ?? string.Empty,
            LogicalFileSize   = descriptor.LogicalFileSize,
            PhysicalFileSize  = descriptor.PhysicalFileSize,
            LastWriteTime     = descriptor.LastWriteTimeUtc,
            ExportPath        = outcome.OutputPath ?? string.Empty,
            Checksum          = outcome.Checksum ?? string.Empty,
            ExportStatus      = outcome.Status.ToString(),
            ExportDate        = outcome.ObservedAtUtc.UtcDateTime,
            WorkerId          = workerId,
            RetryCount        = outcome.AttemptNumber,
            IdempotencyKey    = descriptor.IdempotencyKey.ToHex(),
            DataFileVersionId = descriptor.DataFileVersionKey.DataFileVersionId,
        };

        await _metadata.AppendAsync(record, cancellationToken).ConfigureAwait(false);
    }
}
