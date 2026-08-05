using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Workers;

public sealed record StopWorkerCommand : ICommand
{
    public required long ExportWorkerId { get; init; }
    public string? Reason { get; init; }
}

public sealed class StopWorkerHandler : ICommandHandler<StopWorkerCommand>
{
    private readonly IExportWorkerRepository _workers;
    private readonly ILogger<StopWorkerHandler> _logger;

    public StopWorkerHandler(IExportWorkerRepository workers, ILogger<StopWorkerHandler> logger)
    {
        _workers = workers;
        _logger = logger;
    }

    public async Task<ApplicationResult> HandleAsync(
        StopWorkerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExportWorkerId <= 0)
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("WORKER_ID_REQUIRED", "ExportWorkerId must be positive."));
        }

        try
        {
            await _workers.StopAsync(command.ExportWorkerId, command.Reason, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Worker {WorkerId} stopped ({Reason})", command.ExportWorkerId, command.Reason ?? "no reason");
            return ApplicationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to stop worker {WorkerId}", command.ExportWorkerId);
            return ApplicationResult.Failure(ApplicationError.Unexpected("WORKER_STOP_FAILED", ex.Message));
        }
    }
}
