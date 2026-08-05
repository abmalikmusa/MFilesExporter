using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Progress;

public sealed record GetActiveCheckpointQuery(long ExportJobId, string PartitionKey) : IQuery<ExportCheckpointRecord>;

public sealed class GetActiveCheckpointHandler : IQueryHandler<GetActiveCheckpointQuery, ExportCheckpointRecord>
{
    private readonly IExportCheckpointRepository _checkpoints;

    public GetActiveCheckpointHandler(IExportCheckpointRepository checkpoints)
    {
        _checkpoints = checkpoints;
    }

    public async Task<ApplicationResult<ExportCheckpointRecord>> HandleAsync(
        GetActiveCheckpointQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ExportJobId <= 0)
        {
            return ApplicationResult<ExportCheckpointRecord>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        }
        if (string.IsNullOrWhiteSpace(query.PartitionKey))
        {
            return ApplicationResult<ExportCheckpointRecord>.Failure(
                ApplicationError.Validation("PARTITION_REQUIRED", "PartitionKey is required."));
        }

        var cp = await _checkpoints.GetActiveAsync(query.ExportJobId, query.PartitionKey, cancellationToken)
            .ConfigureAwait(false);
        if (cp is null)
        {
            return ApplicationResult<ExportCheckpointRecord>.Failure(
                ApplicationError.NotFound("NO_CHECKPOINT",
                    $"No active checkpoint for job {query.ExportJobId} / partition '{query.PartitionKey}'."));
        }
        return ApplicationResult<ExportCheckpointRecord>.Success(cp);
    }
}
