using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Errors;

public sealed record ResolveErrorCommand : ICommand
{
    public required long ExportErrorId { get; init; }
    public ExportErrorStatus NewStatus { get; init; } = ExportErrorStatus.Resolved;
    public string? Notes { get; init; }
    public string? ActorName { get; init; }
}

public sealed class ResolveErrorHandler : ICommandHandler<ResolveErrorCommand>
{
    private static readonly HashSet<ExportErrorStatus> AllowedTerminals =
    [
        ExportErrorStatus.Resolved,
        ExportErrorStatus.Ignored,
    ];

    private readonly IExportErrorRepository _errors;
    private readonly ILogger<ResolveErrorHandler> _logger;

    public ResolveErrorHandler(IExportErrorRepository errors, ILogger<ResolveErrorHandler> logger)
    {
        _errors = errors;
        _logger = logger;
    }

    public async Task<ApplicationResult> HandleAsync(
        ResolveErrorCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExportErrorId <= 0)
            return ApplicationResult.Failure(
                ApplicationError.Validation("ERROR_ID_REQUIRED", "ExportErrorId must be positive."));
        if (!AllowedTerminals.Contains(command.NewStatus))
            return ApplicationResult.Failure(
                ApplicationError.Validation("BAD_TERMINAL", "NewStatus must be Resolved or Ignored."));

        try
        {
            await _errors.ResolveAsync(
                command.ExportErrorId, command.NewStatus, command.Notes, command.ActorName, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Error {ErrorId} resolved as {Status}", command.ExportErrorId, command.NewStatus);
            return ApplicationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to resolve error {ErrorId}", command.ExportErrorId);
            return ApplicationResult.Failure(ApplicationError.Unexpected("ERROR_RESOLVE_FAILED", ex.Message));
        }
    }
}
