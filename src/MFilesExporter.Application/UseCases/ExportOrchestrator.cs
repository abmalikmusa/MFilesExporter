using MFilesExporter.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases;

/// <summary>
/// Top-level use case. Prepares the state store, runs the export pipeline
/// to completion, and returns an aggregate summary.
///
/// The Application layer intentionally does NOT know how the pipeline is
/// wired internally — <see cref="IExportPipeline"/> is a port implemented
/// in MFilesExporter.Export.
/// </summary>
public sealed class ExportOrchestrator
{
    private readonly IExportPipeline _pipeline;
    private readonly IExportStateStore _stateStore;
    private readonly IClock _clock;
    private readonly ILogger<ExportOrchestrator> _logger;

    public ExportOrchestrator(
        IExportPipeline pipeline,
        IExportStateStore stateStore,
        IClock clock,
        ILogger<ExportOrchestrator> logger)
    {
        _pipeline = pipeline;
        _stateStore = stateStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ExportRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        _logger.LogInformation("Export run starting at {StartedAt:O}", startedAt);

        await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.RunAsync(cancellationToken).ConfigureAwait(false);

        var counters = await _stateStore.GetCountersAsync(CancellationToken.None).ConfigureAwait(false);
        var completedAt = _clock.UtcNow;

        var summary = new ExportRunSummary(
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            TotalRecorded: counters.TotalRecorded,
            TotalSucceeded: counters.TotalSucceeded,
            TotalFailed: counters.TotalFailed,
            TotalSkipped: counters.TotalSkipped,
            TotalBytesWritten: counters.TotalBytesWritten);

        _logger.LogInformation(
            "Export run completed | elapsed={Elapsed} succeeded={Succeeded} failed={Failed} skipped={Skipped} bytes={Bytes}",
            summary.Elapsed,
            summary.TotalSucceeded,
            summary.TotalFailed,
            summary.TotalSkipped,
            summary.TotalBytesWritten);

        return summary;
    }
}

public sealed record ExportRunSummary(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long TotalRecorded,
    long TotalSucceeded,
    long TotalFailed,
    long TotalSkipped,
    long TotalBytesWritten)
{
    public TimeSpan Elapsed => CompletedAtUtc - StartedAtUtc;
}
