using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Workers;

/// <summary>
/// Update a worker's heartbeat. The exporter fires one of these per
/// configurable interval; the tracking DB stores only the most recent
/// timestamp and the current status.
/// </summary>
public sealed record HeartbeatWorkerCommand : ICommand
{
    public required long ExportWorkerId { get; init; }
    public ExportWorkerStatus Status { get; init; } = ExportWorkerStatus.Active;
}

public sealed class HeartbeatWorkerHandler : ICommandHandler<HeartbeatWorkerCommand>
{
    private static readonly HashSet<ExportWorkerStatus> AllowedForHeartbeat =
    [
        ExportWorkerStatus.Active,
        ExportWorkerStatus.Idle,
        ExportWorkerStatus.Stalled,
    ];

    private readonly IExportWorkerRepository _workers;

    public HeartbeatWorkerHandler(IExportWorkerRepository workers)
    {
        _workers = workers;
    }

    public async Task<ApplicationResult> HandleAsync(
        HeartbeatWorkerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExportWorkerId <= 0)
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("WORKER_ID_REQUIRED", "ExportWorkerId must be positive."));
        }
        if (!AllowedForHeartbeat.Contains(command.Status))
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("HEARTBEAT_STATUS",
                    "Heartbeat status must be Active, Idle, or Stalled."));
        }

        try
        {
            await _workers.HeartbeatAsync(command.ExportWorkerId, command.Status, cancellationToken)
                .ConfigureAwait(false);
            return ApplicationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult.Failure(
                ApplicationError.Transient("HEARTBEAT_FAILED", ex.Message));
        }
    }
}
