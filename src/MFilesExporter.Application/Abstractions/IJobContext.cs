namespace MFilesExporter.Application.Abstractions;

/// <summary>
/// Ambient scope for the currently-running tracking-DB job. Populated by
/// whichever orchestrator owns the job lifecycle; consumed by downstream
/// components (checkpoint engine, audit log, tracking-DB repositories)
/// that need to attribute writes to a real <c>ExportJobId</c>.
/// </summary>
/// <remarks>
/// <para>
/// The default value is <c>0</c> — "no job registered on this thread".
/// Consumers should treat <c>0</c> as "skip anything that requires a real
/// tracking-DB job row" rather than passing it through as a fake FK.
/// </para>
/// <para>
/// Kept as a mutable singleton (not <see cref="AsyncLocal{T}"/>) because
/// the exporter runs one job per process. If concurrent-job hosting is
/// ever added, swap the implementation to <c>AsyncLocal</c>-backed.
/// </para>
/// </remarks>
public interface IJobContext
{
    /// <summary>Currently-active tracking-DB job id. <c>0</c> means "no job registered".</summary>
    long CurrentJobId { get; }

    /// <summary>Populate the job id for the current run. Called by the orchestrator once at start.</summary>
    void SetCurrent(long jobId);

    /// <summary>Clear the job id — called by the orchestrator on completion / failure.</summary>
    void Clear();
}
