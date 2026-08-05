using MFilesExporter.Application.UseCases;
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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ExportHostedService> _logger;

    public ExportHostedService(
        ExportOrchestrator orchestrator,
        RunSummaryReporter summaryReporter,
        IHostApplicationLifetime lifetime,
        ILogger<ExportHostedService> logger)
    {
        _orchestrator = orchestrator;
        _summaryReporter = summaryReporter;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appStarted = new TaskCompletionSource();
        using var reg = _lifetime.ApplicationStarted.Register(() => appStarted.TrySetResult());
        await appStarted.Task.ConfigureAwait(false);

        try
        {
            var summary = await _orchestrator.RunAsync(stoppingToken).ConfigureAwait(false);
            _summaryReporter.Report(summary);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Export cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Export failed");
            Environment.ExitCode = 2;
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
