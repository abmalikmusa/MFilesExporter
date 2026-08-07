using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.UseCases.Pipeline;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Logging.Audit;
using MFilesExporter.Logging.Correlation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Console;

/// <summary>
/// Runs one export pass to completion then requests host shutdown. The host
/// process exits with 0 on success, 2 on export failure. Dispatches the
/// full lifecycle command (StartJob → RegisterWorker → Pipeline → StopWorker
/// → CompleteJob) so the tracking DB has authoritative rows for the run.
/// </summary>
internal sealed class ExportHostedService : BackgroundService
{
    private readonly IApplicationDispatcher _dispatcher;
    private readonly ICorrelationIdAccessor _correlation;
    private readonly IAuditLog _audit;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly PipelineOptions _pipelineOptions;
    private readonly TrackingDatabaseOptions _trackingOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ExportHostedService> _logger;

    public ExportHostedService(
        IApplicationDispatcher dispatcher,
        ICorrelationIdAccessor correlation,
        IAuditLog audit,
        MFilesSourceOptions sourceOptions,
        PipelineOptions pipelineOptions,
        TrackingDatabaseOptions trackingOptions,
        IHostApplicationLifetime lifetime,
        ILogger<ExportHostedService> logger)
    {
        _dispatcher = dispatcher;
        _correlation = correlation;
        _audit = audit;
        _sourceOptions = sourceOptions;
        _pipelineOptions = pipelineOptions;
        _trackingOptions = trackingOptions;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appStarted = new TaskCompletionSource();
        using var reg = _lifetime.ApplicationStarted.Register(() => appStarted.TrySetResult());
        await appStarted.Task.ConfigureAwait(false);

        using var _ = _correlation.PushNew(out var correlationId);
        var actor = $"{Environment.MachineName}/{Environment.ProcessId}";

        await _audit.WriteAsync(
            action:  "job.started",
            actor:   actor,
            subject: "export/run",
            outcome: "started",
            data:    new Dictionary<string, object?> { ["Environment"] = Environment.OSVersion.Platform.ToString() },
            cancellationToken: stoppingToken).ConfigureAwait(false);

        var (sourceServer, sourceDatabase) = ParseSourceEndpoint(_sourceOptions.ConnectionString);

        var command = new RunExportCommand
        {
            JobName        = $"mfiles-export-{DateTime.UtcNow:yyyyMMddHHmmss}",
            SourceServer   = sourceServer,
            SourceDatabase = sourceDatabase,
            PartitionKey   = _sourceOptions.PartitionKey,
            WorkerName     = Environment.MachineName,
            MachineName    = Environment.MachineName,
            Concurrency    = _pipelineOptions.SinkConcurrency,
            ProcessId      = Environment.ProcessId,
        };

        try
        {
            var result = await _dispatcher.SendAsync<RunExportCommand, RunExportSummary>(command, stoppingToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                _logger.LogError("Export run failed: {Errors}",
                    string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
                await _audit.WriteAsync(
                    action: "job.failed",
                    actor:  actor,
                    subject: "export/run",
                    outcome: "failure",
                    data: new Dictionary<string, object?>
                    {
                        ["Errors"] = string.Join("; ", result.Errors.Select(e => e.Code)),
                    },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                Environment.ExitCode = 2;
                return;
            }

            var summary = result.Value;
            _logger.LogInformation(
                "Export run completed | jobId={JobId} workerId={WorkerId} elapsed={Elapsed}",
                summary.ExportJobId, summary.ExportWorkerId, summary.CompletedAtUtc - summary.StartedAtUtc);

            await _audit.WriteAsync(
                action:  "job.completed",
                actor:   actor,
                subject: $"export/run/{summary.ExportJobId}",
                outcome: "success",
                data:    new Dictionary<string, object?>
                {
                    ["JobId"]          = summary.ExportJobId,
                    ["WorkerId"]       = summary.ExportWorkerId,
                    ["ElapsedSeconds"] = (summary.CompletedAtUtc - summary.StartedAtUtc).TotalSeconds,
                },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Export cancelled");
            await _audit.WriteAsync("job.cancelled", actor, "export/run", "cancelled",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Export failed");
            await _audit.WriteAsync(
                action:  "job.failed",
                actor:   actor,
                subject: "export/run",
                outcome: "failure",
                data:    new Dictionary<string, object?>
                {
                    ["ExceptionType"] = ex.GetType().Name,
                    ["Message"]       = ex.Message,
                },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            Environment.ExitCode = 2;
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private static (string server, string database) ParseSourceEndpoint(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return (builder.DataSource, builder.InitialCatalog);
        }
        catch
        {
            return ("unknown", "unknown");
        }
    }
}
