using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.UseCases.WorkClaiming;

/// <summary>
/// Mark a claim Completed. Returns a boolean: <c>true</c> when the caller's
/// token still owns the row, <c>false</c> when the lease had already
/// expired and been reclaimed. A <c>false</c> outcome is NOT an error —
/// it is a signal that the worker did wasted work and must NOT increment
/// aggregate counters.
/// </summary>
public sealed record CompleteWorkItemCommand : ICommand<bool>
{
    public required WorkItemId WorkItemId { get; init; }
    public required ClaimToken ClaimToken { get; init; }
    public required string OutputPath { get; init; }
    public required string Checksum { get; init; }
    public required long BytesWritten { get; init; }
}

public sealed class CompleteWorkItemHandler : ICommandHandler<CompleteWorkItemCommand, bool>
{
    private readonly IWorkClaimStore _store;

    public CompleteWorkItemHandler(IWorkClaimStore store)
    {
        _store = store;
    }

    public async Task<ApplicationResult<bool>> HandleAsync(
        CompleteWorkItemCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<ApplicationError>();
        if (!command.WorkItemId.IsAssigned)
            errors.Add(ApplicationError.Validation("WORK_ITEM_ID_REQUIRED", "WorkItemId must be assigned."));
        if (!command.ClaimToken.IsAssigned)
            errors.Add(ApplicationError.Validation("TOKEN_REQUIRED", "ClaimToken must be assigned."));
        if (string.IsNullOrWhiteSpace(command.OutputPath))
            errors.Add(ApplicationError.Validation("OUTPUT_REQUIRED", "OutputPath is required."));
        if (string.IsNullOrWhiteSpace(command.Checksum))
            errors.Add(ApplicationError.Validation("CHECKSUM_REQUIRED", "Checksum is required."));
        if (command.BytesWritten < 0)
            errors.Add(ApplicationError.Validation("BYTES_NEGATIVE", "BytesWritten must be >= 0."));

        if (errors.Count > 0)
            return ApplicationResult<bool>.Failure(errors);

        try
        {
            var owned = await _store.CompleteAsync(
                command.WorkItemId, command.ClaimToken,
                command.OutputPath, command.Checksum, command.BytesWritten,
                cancellationToken).ConfigureAwait(false);
            return ApplicationResult<bool>.Success(owned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<bool>.Failure(
                ApplicationError.Transient("COMPLETE_FAILED", ex.Message));
        }
    }
}
