using MFilesExporter.Application.UseCases;
using MFilesExporter.Logging.Audit;
using MFilesExporter.Logging.Correlation;
using MFilesExporter.Reporting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Console;

/// <summary>
/// Runs one export pass to completion then requests host shutdown. The host
/// process exits with 0 on success, 2 on export failure.
/// </summary>
internal sealed class ExportHostedService : BackgroundService
{
    private readonly ExportOrchestrator _orchestrator;
    private readonly RunSummaryReporter _summaryReporter;
    private readonly ICorrelationIdAccessor _correlation;
    private readonly IAuditLog _audit;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ExportHostedService> _logger;

    public ExportHostedService(
        ExportOrchestrator orchestrator,
        RunSummaryReporter summaryReporter,
        ICorrelationIdAccessor correlation,
        IAuditLog audit,
        IHostApplicationLifetime lifetime,
        ILogger<ExportHostedService> logger)
    {
        _orchestrator = orchestrator;
        _summaryReporter = summaryReporter;
        _correlation = correlation;
        _audit = audit;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appStarted = new TaskCompletionSource();
        using var reg = _lifetime.ApplicationStarted.Register(() => appStarted.TrySetResult());
        await appStarted.Task.ConfigureAwait(false);

        // One correlation id per top-level run — every log line, every audit
        // event, and every performance record inside the pipeline inherits
        // this id via AsyncLocal + Serilog LogContext.
        using var _ = _correlation.PushNew(out var correlationId);

        var actor = $"{Environment.MachineName}/{Environment.ProcessId}";

        await _audit.WriteAsync(
            action:  "job.started",
            actor:   actor,
            subject: "export/run",
            outcome: "started",
            data:    new Dictionary<string, object?> { ["Environment"] = Environment.OSVersion.Platform.ToString() },
            cancellationToken: stoppingToken).ConfigureAwait(false);

        try
        {
            var summary = await _orchestrator.RunAsync(stoppingToken).ConfigureAwait(false);
            _summaryReporter.Report(summary);

            await _audit.WriteAsync(
                action:  "job.completed",
                actor:   actor,
                subject: "export/run",
                outcome: "success",
                data:    new Dictionary<string, object?>
                {
                    ["ElapsedSeconds"]     = summary.Elapsed.TotalSeconds,
                    ["Succeeded"]          = summary.TotalSucceeded,
                    ["Failed"]             = summary.TotalFailed,
                    ["Skipped"]            = summary.TotalSkipped,
                    ["BytesWritten"]       = summary.TotalBytesWritten,
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
}
