using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Errors;

/// <summary>Record a single error observation. Returns the new error id.</summary>
public sealed record LogErrorCommand(ExportErrorRecord Error) : ICommand<long>;

public sealed class LogErrorHandler : ICommandHandler<LogErrorCommand, long>
{
    private readonly IExportErrorRepository _errors;

    public LogErrorHandler(IExportErrorRepository errors)
    {
        _errors = errors;
    }

    public async Task<ApplicationResult<long>> HandleAsync(
        LogErrorCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Error is null)
        {
            return ApplicationResult<long>.Failure(
                ApplicationError.Validation("ERROR_REQUIRED", "Error record is required."));
        }
        if (command.Error.ExportJobId <= 0)
        {
            return ApplicationResult<long>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "Error.ExportJobId must be positive."));
        }
        if (string.IsNullOrWhiteSpace(command.Error.ErrorSource))
        {
            return ApplicationResult<long>.Failure(
                ApplicationError.Validation("SOURCE_REQUIRED", "Error.ErrorSource is required."));
        }

        try
        {
            var id = await _errors.LogAsync(command.Error, cancellationToken).ConfigureAwait(false);
            return ApplicationResult<long>.Success(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<long>.Failure(
                ApplicationError.Transient("ERROR_LOG_FAILED", ex.Message));
        }
    }
}
