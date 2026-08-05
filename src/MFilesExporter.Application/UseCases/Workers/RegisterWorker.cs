using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Workers;

/// <summary>Register a worker under a job and mark it Active.</summary>
public sealed record RegisterWorkerCommand : ICommand<long>
{
    public required long ExportJobId { get; init; }
    public required string WorkerName { get; init; }
    public required string MachineName { get; init; }
    public int? ProcessId { get; init; }
    public required string AssignedPartition { get; init; }
    public int Concurrency { get; init; } = 1;
}

public sealed class RegisterWorkerHandler : ICommandHandler<RegisterWorkerCommand, long>
{
    private readonly IExportWorkerRepository _workers;
    private readonly ILogger<RegisterWorkerHandler> _logger;

    public RegisterWorkerHandler(IExportWorkerRepository workers, ILogger<RegisterWorkerHandler> logger)
    {
        _workers = workers;
        _logger = logger;
    }

    public async Task<ApplicationResult<long>> HandleAsync(
        RegisterWorkerCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<ApplicationError>();
        if (command.ExportJobId <= 0)
            errors.Add(ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        if (string.IsNullOrWhiteSpace(command.WorkerName))
            errors.Add(ApplicationError.Validation("WORKER_NAME_REQUIRED", "WorkerName is required."));
        if (string.IsNullOrWhiteSpace(command.MachineName))
            errors.Add(ApplicationError.Validation("MACHINE_NAME_REQUIRED", "MachineName is required."));
        if (string.IsNullOrWhiteSpace(command.AssignedPartition))
            errors.Add(ApplicationError.Validation("PARTITION_REQUIRED", "AssignedPartition is required."));
        if (command.Concurrency < 1 || command.Concurrency > 256)
            errors.Add(ApplicationError.Validation("CONCURRENCY_RANGE", "Concurrency must be between 1 and 256."));

        if (errors.Count > 0)
            return ApplicationResult<long>.Failure(errors);

        try
        {
            var id = await _workers.RegisterAsync(
                command.ExportJobId,
                command.WorkerName,
                command.MachineName,
                command.ProcessId,
                command.AssignedPartition,
                command.Concurrency,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Worker {WorkerId} registered under job {JobId} on partition {Partition}",
                id, command.ExportJobId, command.AssignedPartition);

            return ApplicationResult<long>.Success(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to register worker {WorkerName}", command.WorkerName);
            return ApplicationResult<long>.Failure(
                ApplicationError.Unexpected("WORKER_REGISTER_FAILED", ex.Message));
        }
    }
}
