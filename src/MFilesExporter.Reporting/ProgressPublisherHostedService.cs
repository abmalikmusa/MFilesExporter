using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Reporting;

/// <summary>
/// Background service that polls state-store counters and publishes ExportProgress
/// snapshots to the registered <see cref="IProgressReporter"/> on a cadence.
/// </summary>
public sealed class ProgressPublisherHostedService : BackgroundService
{
    private readonly IExportStateStore _stateStore;
    private readonly IProgressReporter _reporter;
    private readonly IClock _clock;
    private readonly PipelineOptions _options;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly ILogger<ProgressPublisherHostedService> _logger;

    public ProgressPublisherHostedService(
        IExportStateStore stateStore,
        IProgressReporter reporter,
        IClock clock,
        PipelineOptions options,
        MFilesSourceOptions sourceOptions,
        ILogger<ProgressPublisherHostedService> logger)
    {
        _stateStore = stateStore;
        _reporter = reporter;
        _clock = clock;
        _options = options;
        _sourceOptions = sourceOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = _clock.UtcNow;
        using var timer = new PeriodicTimer(_options.ProgressReportInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var counters = await _stateStore.GetCountersAsync(stoppingToken).ConfigureAwait(false);
                var checkpoint = await _stateStore.GetCheckpointAsync(_sourceOptions.PartitionKey, stoppingToken).ConfigureAwait(false);

                var progress = new ExportProgress
                {
                    TotalRecorded = counters.TotalRecorded,
                    TotalSucceeded = counters.TotalSucceeded,
                    TotalFailed = counters.TotalFailed,
                    TotalSkipped = counters.TotalSkipped,
                    TotalBytesWritten = counters.TotalBytesWritten,
                    LastCheckpoint = checkpoint == DocumentFileVersionKey.Origin ? null : checkpoint,
                    StartedAtUtc = startedAt,
                    ObservedAtUtc = _clock.UtcNow,
                };

                try
                {
                    await _reporter.ReportAsync(progress, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Progress reporter threw; continuing");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
