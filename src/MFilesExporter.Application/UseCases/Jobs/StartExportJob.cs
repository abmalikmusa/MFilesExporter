using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Jobs;

/// <summary>
/// Create a new export run and mark it Running. Returns the surrogate id
/// assigned by the tracking database. Idempotent per (job name, partition):
/// invoking a second time with the same pair returns a Conflict error.
/// </summary>
public sealed record StartExportJobCommand : ICommand<long>
{
    public required string JobName { get; init; }
    public required string SourceServer { get; init; }
    public required string SourceDatabase { get; init; }
    public required string PartitionKey { get; init; }
    public long? TotalDocumentsExpected { get; init; }
}

public sealed class StartExportJobHandler : ICommandHandler<StartExportJobCommand, long>
{
    private readonly IExportJobRepository _jobs;
    private readonly ILogger<StartExportJobHandler> _logger;

    public StartExportJobHandler(IExportJobRepository jobs, ILogger<StartExportJobHandler> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<ApplicationResult<long>> HandleAsync(
        StartExportJobCommand command,
        CancellationToken cancellationToken)
    {
        var validation = Validate(command);
        if (validation.IsFailure) return ApplicationResult<long>.Failure(validation.Errors);

        try
        {
            var id = await _jobs.StartAsync(
                command.JobName,
                command.SourceServer,
                command.SourceDatabase,
                command.PartitionKey,
                command.TotalDocumentsExpected,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Export job {JobId} started for partition {Partition}", id, command.PartitionKey);
            return ApplicationResult<long>.Success(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to start export job {JobName}", command.JobName);
            return ApplicationResult<long>.Failure(
                ApplicationError.Unexpected("JOB_START_FAILED", ex.Message));
        }
    }

    private static ApplicationResult Validate(StartExportJobCommand c)
    {
        var errors = new List<ApplicationError>();
        if (string.IsNullOrWhiteSpace(c.JobName))
            errors.Add(ApplicationError.Validation("JOB_NAME_REQUIRED", "JobName is required."));
        if (string.IsNullOrWhiteSpace(c.SourceServer))
            errors.Add(ApplicationError.Validation("SOURCE_SERVER_REQUIRED", "SourceServer is required."));
        if (string.IsNullOrWhiteSpace(c.SourceDatabase))
            errors.Add(ApplicationError.Validation("SOURCE_DB_REQUIRED", "SourceDatabase is required."));
        if (string.IsNullOrWhiteSpace(c.PartitionKey))
            errors.Add(ApplicationError.Validation("PARTITION_REQUIRED", "PartitionKey is required."));
        if (c.TotalDocumentsExpected is < 0)
            errors.Add(ApplicationError.Validation("TOTAL_NEGATIVE", "TotalDocumentsExpected must be >= 0."));

        return errors.Count == 0 ? ApplicationResult.Success() : ApplicationResult.Failure(errors);
    }
}
