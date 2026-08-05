using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Progress;

/// <summary>
/// Append one progress snapshot for a job. The exporter emits these on
/// <c>Pipeline.ProgressReportInterval</c>.
/// </summary>
public sealed record RecordProgressSnapshotCommand(ExportProgressRecord Snapshot) : ICommand;

public sealed class RecordProgressSnapshotHandler : ICommandHandler<RecordProgressSnapshotCommand>
{
    private readonly IExportProgressRepository _progress;

    public RecordProgressSnapshotHandler(IExportProgressRepository progress)
    {
        _progress = progress;
    }

    public async Task<ApplicationResult> HandleAsync(
        RecordProgressSnapshotCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Snapshot is null)
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("SNAPSHOT_REQUIRED", "Snapshot is required."));
        }
        if (command.Snapshot.ExportJobId <= 0)
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "Snapshot.ExportJobId must be positive."));
        }

        try
        {
            await _progress.RecordAsync(command.Snapshot, cancellationToken).ConfigureAwait(false);
            return ApplicationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult.Failure(
                ApplicationError.Transient("PROGRESS_RECORD_FAILED", ex.Message));
        }
    }
}
