using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Reporting;

public sealed class LoggingProgressReporter : IProgressReporter
{
    private readonly ILogger<LoggingProgressReporter> _logger;

    public LoggingProgressReporter(ILogger<LoggingProgressReporter> logger)
    {
        _logger = logger;
    }

    public Task ReportAsync(ExportProgress progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Progress | recorded={Recorded} succeeded={Succeeded} failed={Failed} skipped={Skipped} bytes={Bytes} docs/s={Rate:F1} MiB/s={Bw:F2} checkpoint={Checkpoint} elapsed={Elapsed}",
            progress.TotalRecorded,
            progress.TotalSucceeded,
            progress.TotalFailed,
            progress.TotalSkipped,
            progress.TotalBytesWritten,
            progress.DocumentsPerSecond,
            progress.MebibytesPerSecond,
            progress.LastCheckpoint?.ToString() ?? "-",
            progress.Elapsed);
        return Task.CompletedTask;
    }
}
