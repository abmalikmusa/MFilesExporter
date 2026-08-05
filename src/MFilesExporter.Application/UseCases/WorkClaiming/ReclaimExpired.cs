using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.WorkClaiming;

/// <summary>
/// Manually invoke the reaper. In production this runs as a SQL Agent
/// job every 30 s, but this command lets an operator kick it off manually
/// or lets a co-located reaper hosted service call it in DI-managed hosts.
/// </summary>
public sealed record ReclaimExpiredCommand : ICommand<int>
{
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRows { get; init; } = 5_000;
}

public sealed class ReclaimExpiredHandler : ICommandHandler<ReclaimExpiredCommand, int>
{
    private readonly IWorkClaimStore _store;
    private readonly ILogger<ReclaimExpiredHandler> _logger;

    public ReclaimExpiredHandler(IWorkClaimStore store, ILogger<ReclaimExpiredHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<ApplicationResult<int>> HandleAsync(
        ReclaimExpiredCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MaxRows <= 0 || command.MaxRows > 100_000)
            return ApplicationResult<int>.Failure(
                ApplicationError.Validation("MAX_ROWS_RANGE", "MaxRows must be between 1 and 100 000."));

        try
        {
            var reclaimed = await _store.ReclaimExpiredAsync(
                command.RetryBackoff, command.MaxRows, cancellationToken).ConfigureAwait(false);
            if (reclaimed > 0)
            {
                _logger.LogWarning("Reclaimed {Count} expired leases", reclaimed);
            }
            return ApplicationResult<int>.Success(reclaimed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<int>.Failure(
                ApplicationError.Transient("RECLAIM_FAILED", ex.Message));
        }
    }
}
