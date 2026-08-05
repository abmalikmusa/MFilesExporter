using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Domain.WorkClaiming;

namespace MFilesExporter.Application.UseCases.WorkClaiming;

/// <summary>Atomically claim the next batch of work items for a worker.</summary>
public sealed record ClaimWorkBatchCommand : ICommand<IReadOnlyList<ClaimedWorkItem>>
{
    public required long ExportJobId { get; init; }
    public required long WorkerId { get; init; }
    public required int BatchSize { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class ClaimWorkBatchHandler : ICommandHandler<ClaimWorkBatchCommand, IReadOnlyList<ClaimedWorkItem>>
{
    private readonly IWorkClaimStore _store;

    public ClaimWorkBatchHandler(IWorkClaimStore store)
    {
        _store = store;
    }

    public async Task<ApplicationResult<IReadOnlyList<ClaimedWorkItem>>> HandleAsync(
        ClaimWorkBatchCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<ApplicationError>();
        if (command.ExportJobId <= 0)
            errors.Add(ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        if (command.WorkerId <= 0)
            errors.Add(ApplicationError.Validation("WORKER_ID_REQUIRED", "WorkerId must be positive."));
        if (command.BatchSize <= 0 || command.BatchSize > 10_000)
            errors.Add(ApplicationError.Validation("BATCH_RANGE", "BatchSize must be between 1 and 10 000."));
        if (command.LeaseDuration <= TimeSpan.Zero || command.LeaseDuration > TimeSpan.FromHours(1))
            errors.Add(ApplicationError.Validation("LEASE_RANGE", "LeaseDuration must be > 0 and ≤ 1 hour."));

        if (errors.Count > 0)
            return ApplicationResult<IReadOnlyList<ClaimedWorkItem>>.Failure(errors);

        try
        {
            var items = await _store.ClaimAsync(
                command.ExportJobId,
                command.WorkerId,
                command.BatchSize,
                command.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            return ApplicationResult<IReadOnlyList<ClaimedWorkItem>>.Success(items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<IReadOnlyList<ClaimedWorkItem>>.Failure(
                ApplicationError.Transient("CLAIM_FAILED", ex.Message));
        }
    }
}
