using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Application.Abstractions.Tracking;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// <see cref="ITotalExpectedSource"/> backed by the tracking-DB job row.
/// Reads <c>ExportJobs.TotalDocumentsExpected</c> for the current job the
/// first time the dashboard asks, then caches. The dashboard only needs a
/// stable target — the row's value doesn't change during a run.
/// </summary>
public sealed class TotalExpectedFromJobRepository : ITotalExpectedSource
{
    private readonly IExportJobRepository _jobs;
    private readonly IJobContext _jobContext;

    private long _cachedExpected;
    private long _cachedForJobId;

    public TotalExpectedFromJobRepository(IExportJobRepository jobs, IJobContext jobContext)
    {
        _jobs = jobs;
        _jobContext = jobContext;
    }

    public long TotalExpected
    {
        get
        {
            var jobId = _jobContext.CurrentJobId;
            if (jobId == 0) return 0;

            // Same job as the last query → return cached value (usually 0 first, then filled).
            var cachedFor = Volatile.Read(ref _cachedForJobId);
            var cached    = Volatile.Read(ref _cachedExpected);
            if (cachedFor == jobId && cached > 0) return cached;

            // Fetch and cache. Blocking call — dashboard tick is 500 ms and
            // the tracking-DB SELECT is a point read, so ~1 ms per call max.
            var record = _jobs.GetAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();
            var value  = record?.TotalDocumentsExpected ?? 0;
            Volatile.Write(ref _cachedExpected, value);
            Volatile.Write(ref _cachedForJobId, jobId);
            return value;
        }
    }
}
