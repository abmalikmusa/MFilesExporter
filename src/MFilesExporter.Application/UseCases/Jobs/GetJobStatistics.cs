using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Jobs;

/// <summary>
/// Query returning the job's most recent progress snapshot merged with its
/// header. Purely a read; suitable for dashboards and REST endpoints.
/// </summary>
public sealed record GetJobStatisticsQuery(long ExportJobId) : IQuery<JobStatisticsView>;

/// <summary>Denormalized read model that composes header + latest progress.</summary>
public sealed record JobStatisticsView(
    ExportJobRecord Job,
    ExportProgressRecord? LatestProgress);

public sealed class GetJobStatisticsHandler : IQueryHandler<GetJobStatisticsQuery, JobStatisticsView>
{
    private readonly IExportJobRepository _jobs;
    private readonly IExportProgressRepository _progress;

    public GetJobStatisticsHandler(
        IExportJobRepository jobs,
        IExportProgressRepository progress)
    {
        _jobs = jobs;
        _progress = progress;
    }

    public async Task<ApplicationResult<JobStatisticsView>> HandleAsync(
        GetJobStatisticsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ExportJobId <= 0)
        {
            return ApplicationResult<JobStatisticsView>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        }

        var job = await _jobs.GetAsync(query.ExportJobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return ApplicationResult<JobStatisticsView>.Failure(
                ApplicationError.NotFound("JOB_NOT_FOUND", $"Job {query.ExportJobId} not found."));
        }

        var latest = await _progress.GetLatestAsync(query.ExportJobId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult<JobStatisticsView>.Success(new JobStatisticsView(job, latest));
    }
}
