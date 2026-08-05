using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.UseCases.WorkClaiming;

/// <summary>Extend an active lease. Fresh expiry returned on success.</summary>
public sealed record RenewLeaseCommand : ICommand<DateTimeOffset?>
{
    public required WorkItemId WorkItemId { get; init; }
    public required ClaimToken ClaimToken { get; init; }
    public TimeSpan Extension { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class RenewLeaseHandler : ICommandHandler<RenewLeaseCommand, DateTimeOffset?>
{
    private readonly IWorkClaimStore _store;

    public RenewLeaseHandler(IWorkClaimStore store)
    {
        _store = store;
    }

    public async Task<ApplicationResult<DateTimeOffset?>> HandleAsync(
        RenewLeaseCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.WorkItemId.IsAssigned)
            return ApplicationResult<DateTimeOffset?>.Failure(
                ApplicationError.Validation("WORK_ITEM_ID_REQUIRED", "WorkItemId must be assigned."));
        if (!command.ClaimToken.IsAssigned)
            return ApplicationResult<DateTimeOffset?>.Failure(
                ApplicationError.Validation("TOKEN_REQUIRED", "ClaimToken must be assigned."));
        if (command.Extension <= TimeSpan.Zero || command.Extension > TimeSpan.FromHours(1))
            return ApplicationResult<DateTimeOffset?>.Failure(
                ApplicationError.Validation("EXTENSION_RANGE", "Extension must be > 0 and ≤ 1 hour."));

        try
        {
            var next = await _store.RenewAsync(
                command.WorkItemId, command.ClaimToken, command.Extension, cancellationToken)
                .ConfigureAwait(false);
            return ApplicationResult<DateTimeOffset?>.Success(next);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<DateTimeOffset?>.Failure(
                ApplicationError.Transient("RENEW_FAILED", ex.Message));
        }
    }
}
