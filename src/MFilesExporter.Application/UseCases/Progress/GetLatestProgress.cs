using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Progress;

public sealed record GetLatestProgressQuery(long ExportJobId) : IQuery<ExportProgressRecord>;

public sealed class GetLatestProgressHandler : IQueryHandler<GetLatestProgressQuery, ExportProgressRecord>
{
    private readonly IExportProgressRepository _progress;

    public GetLatestProgressHandler(IExportProgressRepository progress)
    {
        _progress = progress;
    }

    public async Task<ApplicationResult<ExportProgressRecord>> HandleAsync(
        GetLatestProgressQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ExportJobId <= 0)
        {
            return ApplicationResult<ExportProgressRecord>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        }
        var latest = await _progress.GetLatestAsync(query.ExportJobId, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return ApplicationResult<ExportProgressRecord>.Failure(
                ApplicationError.NotFound("NO_PROGRESS", $"No progress snapshots for job {query.ExportJobId}."));
        }
        return ApplicationResult<ExportProgressRecord>.Success(latest);
    }
}
