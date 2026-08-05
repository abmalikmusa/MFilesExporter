using MFilesExporter.Application.UseCases;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Reporting;

public sealed class RunSummaryReporter
{
    private readonly ILogger<RunSummaryReporter> _logger;

    public RunSummaryReporter(ILogger<RunSummaryReporter> logger)
    {
        _logger = logger;
    }

    public void Report(ExportRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _logger.LogInformation(
            "== Run Summary == started={Start:O} completed={End:O} elapsed={Elapsed} recorded={Recorded} succeeded={Succeeded} failed={Failed} skipped={Skipped} bytes={Bytes}",
            summary.StartedAtUtc,
            summary.CompletedAtUtc,
            summary.Elapsed,
            summary.TotalRecorded,
            summary.TotalSucceeded,
            summary.TotalFailed,
            summary.TotalSkipped,
            summary.TotalBytesWritten);
    }
}
