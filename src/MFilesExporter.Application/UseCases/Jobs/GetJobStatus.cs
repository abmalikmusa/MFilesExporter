using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Jobs;

/// <summary>Query for the current state of a single job.</summary>
public sealed record GetJobStatusQuery(long ExportJobId) : IQuery<ExportJobRecord>;

public sealed class GetJobStatusHandler : IQueryHandler<GetJobStatusQuery, ExportJobRecord>
{
    private readonly IExportJobRepository _jobs;

    public GetJobStatusHandler(IExportJobRepository jobs)
    {
        _jobs = jobs;
    }

    public async Task<ApplicationResult<ExportJobRecord>> HandleAsync(
        GetJobStatusQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ExportJobId <= 0)
        {
            return ApplicationResult<ExportJobRecord>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        }

        var job = await _jobs.GetAsync(query.ExportJobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return ApplicationResult<ExportJobRecord>.Failure(
                ApplicationError.NotFound("JOB_NOT_FOUND", $"Job {query.ExportJobId} not found."));
        }
        return ApplicationResult<ExportJobRecord>.Success(job);
    }
}
