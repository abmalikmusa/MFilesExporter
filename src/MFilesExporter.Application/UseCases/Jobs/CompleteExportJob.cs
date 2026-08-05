using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Jobs;

/// <summary>Marks a job as Completed, Failed, or Cancelled.</summary>
public sealed record CompleteExportJobCommand : ICommand
{
    public required long ExportJobId { get; init; }
    public required ExportJobStatus TerminalStatus { get; init; }
    public string? Reason { get; init; }
}

public sealed class CompleteExportJobHandler : ICommandHandler<CompleteExportJobCommand>
{
    private static readonly HashSet<ExportJobStatus> ValidTerminals =
    [
        ExportJobStatus.Completed,
        ExportJobStatus.Failed,
        ExportJobStatus.Cancelled,
    ];

    private readonly IExportJobRepository _jobs;
    private readonly ILogger<CompleteExportJobHandler> _logger;

    public CompleteExportJobHandler(IExportJobRepository jobs, ILogger<CompleteExportJobHandler> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<ApplicationResult> HandleAsync(
        CompleteExportJobCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExportJobId <= 0)
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        }
        if (!ValidTerminals.Contains(command.TerminalStatus))
        {
            return ApplicationResult.Failure(
                ApplicationError.Validation("BAD_TERMINAL", $"'{command.TerminalStatus}' is not a valid terminal status."));
        }

        try
        {
            await _jobs.CompleteAsync(
                command.ExportJobId, command.TerminalStatus, command.Reason, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} completed with status {Status}", command.ExportJobId, command.TerminalStatus);
            return ApplicationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to complete job {JobId}", command.ExportJobId);
            return ApplicationResult.Failure(ApplicationError.Unexpected("JOB_COMPLETE_FAILED", ex.Message));
        }
    }
}
