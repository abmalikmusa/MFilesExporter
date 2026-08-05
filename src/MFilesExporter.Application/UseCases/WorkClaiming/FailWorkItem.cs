using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.UseCases.WorkClaiming;

/// <summary>Fail a claim. Returns whether the token was still valid.</summary>
public sealed record FailWorkItemCommand : ICommand<bool>
{
    public required WorkItemId WorkItemId { get; init; }
    public required ClaimToken ClaimToken { get; init; }
    public required string Reason { get; init; }
    public bool IsPermanent { get; init; }
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed class FailWorkItemHandler : ICommandHandler<FailWorkItemCommand, bool>
{
    private readonly IWorkClaimStore _store;

    public FailWorkItemHandler(IWorkClaimStore store)
    {
        _store = store;
    }

    public async Task<ApplicationResult<bool>> HandleAsync(
        FailWorkItemCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<ApplicationError>();
        if (!command.WorkItemId.IsAssigned)
            errors.Add(ApplicationError.Validation("WORK_ITEM_ID_REQUIRED", "WorkItemId must be assigned."));
        if (!command.ClaimToken.IsAssigned)
            errors.Add(ApplicationError.Validation("TOKEN_REQUIRED", "ClaimToken must be assigned."));
        if (string.IsNullOrWhiteSpace(command.Reason))
            errors.Add(ApplicationError.Validation("REASON_REQUIRED", "Reason is required."));
        if (command.Backoff < TimeSpan.Zero || command.Backoff > TimeSpan.FromHours(1))
            errors.Add(ApplicationError.Validation("BACKOFF_RANGE", "Backoff must be between 0 and 1 hour."));

        if (errors.Count > 0)
            return ApplicationResult<bool>.Failure(errors);

        try
        {
            var owned = await _store.FailAsync(
                command.WorkItemId, command.ClaimToken,
                command.Reason, command.IsPermanent, command.Backoff,
                cancellationToken).ConfigureAwait(false);
            return ApplicationResult<bool>.Success(owned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<bool>.Failure(
                ApplicationError.Transient("FAIL_RECORD_FAILED", ex.Message));
        }
    }
}
