using MFilesExporter.Application.Abstractions;

namespace MFilesExporter.Application;

/// <summary>
/// Default <see cref="IJobContext"/>. Single mutable field — one job per
/// process in the current deployment model. <c>Volatile</c> read/write
/// keeps other threads' view fresh; no lock needed because writes are
/// initiated by a single orchestrator.
/// </summary>
public sealed class JobContext : IJobContext
{
    private long _jobId;

    public long CurrentJobId => Volatile.Read(ref _jobId);

    public void SetCurrent(long jobId)
    {
        if (jobId < 0) throw new ArgumentOutOfRangeException(nameof(jobId), "Job id must be non-negative.");
        Volatile.Write(ref _jobId, jobId);
    }

    public void Clear() => Volatile.Write(ref _jobId, 0);
}
